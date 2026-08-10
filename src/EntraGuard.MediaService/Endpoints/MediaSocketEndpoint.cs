using System.Net.WebSockets;
using System.Text;
using Azure.Core;
using EntraGuard.MediaService.Agents;
using EntraGuard.MediaService.Configuration;
using EntraGuard.MediaService.Hubs;
using EntraGuard.MediaService.Sessions;
using EntraGuard.Shared.Policy;
using EntraGuard.Shared.Sessions;
using EntraGuard.Shared.Streaming;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Endpoints;

/// <summary>
/// The media plane: ACS streams call audio in over this WebSocket, and EntraGuard streams
/// synthesised speech back out over the same connection.
///
/// This endpoint is why the service runs on Container Apps rather than Azure Functions —
/// Functions cannot accept an inbound WebSocket upgrade at all.
/// </summary>
public static class MediaSocketEndpoint
{
    /// <summary>
    /// 24 kHz 16-bit mono at 20 ms per packet. Matching ACS's own framing on the way out
    /// keeps playback smooth; one oversized write arrives as a burst.
    /// </summary>
    private const int OutboundFrameBytes = 960;

    public static void MapMediaSocket(this IEndpointRouteBuilder app)
    {
        app.Map("/ws/media/{sessionId}", async (
            HttpContext context,
            string sessionId,
            LiveCallRegistry registry,
            AnalystAgent analyst,
            ActuatorAgent actuator,
            VerificationCoordinator verifications,
            VerificationRegistry verificationRegistry,
            VoiceAgentRegistry voiceAgents,
            VoiceprintClient voiceprint,
            IOptions<EntraGuardOptions> options,
            TokenCredential credential,
            IConfiguration configuration,
            IHubContext<LiveHub> hub,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("MediaSocket");

            if (!context.WebSockets.IsWebSocketRequest)
            {
                return Results.BadRequest("This endpoint accepts WebSocket connections only.");
            }

            var call = registry.Get(sessionId);
            if (call is null)
            {
                logger.LogWarning("Media socket for unknown session {SessionId}.", sessionId);
                return Results.NotFound();
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            call.Session.MediaStreamConnectedAt = DateTimeOffset.UtcNow;
            logger.LogInformation("Media stream connected for {SessionId}.", sessionId);

            await using var perception = await PerceptionAgent.CreateAsync(
                call.Session,
                options.Value,
                credential,
                configuration["SPEECH_RESOURCE_ID"] ?? string.Empty,
                loggerFactory.CreateLogger<PerceptionAgent>(),
                call.Lifetime.Token);

            call.Perception = perception;
            call.SendAudioAsync = (pcm, token) => SendAudioAsync(socket, pcm, token);

            // Collects the protected user's channel for voice comparison. Only created when
            // a scorer is configured, so a deployment without one behaves exactly as before.
            if (voiceprint.IsConfigured)
            {
                call.Biometrics = new VoiceBiometricAgent(
                    call.Session, loggerFactory.CreateLogger("VoiceBiometrics"));
            }

            // ── Conversational agent ────────────────────────────────────────
            //
            // Only on a verification call. An INTERCEPTED call already has two humans on
            // it; injecting a third voice into a scam in progress is a different product
            // decision, and the Actuator's targeted warning is the deliberate one there.
            var verificationId = verifications.VerificationForMonitorSession(sessionId);
            VoiceAgent? voice = null;
            Task? voiceLoop = null;

            if (verificationId is not null && verificationRegistry.Get(verificationId) is { } verification)
            {
                voice = await VoiceAgent.CreateAsync(
                    options.Value,
                    credential,
                    loggerFactory.CreateLogger<VoiceAgent>(),
                    verification.ApplicationName,
                    verification.MatchCode,
                    verification.KnowledgeQuestion,
                    call.Lifetime.Token);

                if (voice is not null)
                {
                    voice.AudioProduced += (pcm, token) => SendAudioAsync(socket, pcm, token);

                    voice.TranscriptProduced += (text, isCaller, isFinal) =>
                    {
                        // Into the same transcript the Analyst scores. The agent's own words
                        // belong there too: an agent that has been talked into something is
                        // itself evidence, and a transcript showing only the human half
                        // would hide it.
                        call.Session.AddUtterance(new Utterance(
                            isCaller
                                ? EntraGuard.Shared.Detection.SpeakerRole.ProtectedUser
                                : EntraGuard.Shared.Detection.SpeakerRole.Unknown,
                            text,
                            call.Session.ElapsedMs(DateTimeOffset.UtcNow),
                            DateTimeOffset.UtcNow,
                            isFinal));

                        _ = hub.Clients.All.SendAsync(LiveHub.TranscriptEvent, new
                        {
                            sessionId,
                            speaker = isCaller ? "ProtectedUser" : "Agent",
                            text,
                            offsetMs = call.Session.ElapsedMs(DateTimeOffset.UtcNow),
                            isFinal,
                        }, CancellationToken.None);
                    };

                    voice.GuardrailTripped += violation =>
                        logger.LogWarning(
                            "Voice guardrail refused agent speech on {SessionId}: {Violation}",
                            sessionId, violation);

                    // From here until the call ends, this agent is the only voice.
                    voiceAgents.Register(verificationId, voice);
                    voiceLoop = voice.RunAsync(call.Lifetime.Token);
                }
                else if (!string.IsNullOrEmpty(options.Value.RealtimeEndpoint))
                {
                    // Only when an agent was EXPECTED and failed to connect. With the agent
                    // switched off entirely the coordinator never skipped its own prompt, so
                    // speaking here put a second challenge on top of the first — the user
                    // heard the same question asked over and over. Checking "voice is null"
                    // could not tell "the agent broke" from "there is no agent", and those
                    // need opposite behaviour.
                    await verifications.SpeakScriptedFallbackAsync(verification, call.Lifetime.Token);
                }
            }

            perception.UtteranceRecognized += utterance => _ = hub.Clients.All.SendAsync(
                LiveHub.TranscriptEvent,
                new
                {
                    sessionId,
                    speaker = utterance.Speaker.ToString(),
                    text = utterance.Text,
                    offsetMs = utterance.OffsetMs,
                    isFinal = utterance.IsFinal,
                },
                CancellationToken.None);

            // The analysis loop runs alongside the receive loop rather than inside it:
            // pulling frames off the socket must never block on a model call, or audio
            // backs up and the transcript falls behind the live conversation.
            var analysisLoop = RunAnalysisLoopAsync(
                call, analyst, actuator, options.Value, hub, logger, call.Lifetime.Token);

            try
            {
                await ReceiveLoopAsync(
                    socket, call, perception, verifications, voice, sessionId, logger, call.Lifetime.Token);
            }
            finally
            {
                await call.Lifetime.CancelAsync();
                await analysisLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                if (voiceLoop is not null)
                {
                    await voiceLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }

                if (voice is not null)
                {
                    voiceAgents.Remove(verificationId!);
                    await voice.DisposeAsync();
                }

                logger.LogInformation("Media stream closed for {SessionId}.", sessionId);
            }

            return Results.Empty;
        });
    }

    /// <summary>
    /// Pull frames off the socket, decode them, and feed audio to recognition.
    /// </summary>
    private static async Task ReceiveLoopAsync(
        WebSocket socket,
        LiveCall call,
        PerceptionAgent perception,
        VerificationCoordinator verifications,
        VoiceAgent? voice,
        string sessionId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // 8 KB: comfortably larger than a single ACS frame, so the common case completes in
        // one read while the assembler still handles anything that spans reads.
        var buffer = new byte[8192];
        var assembler = new AcsMessageAssembler();

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                logger.LogWarning(ex, "Media socket faulted for {SessionId}.", call.Session.SessionId);
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            // Buffer until the message completes. Decoding each fragment separately would
            // truncate larger frames and corrupt UTF-8 sequences split across reads.
            var message = assembler.Append(buffer.AsSpan(0, result.Count), result.EndOfMessage);
            if (message is null)
            {
                continue;
            }

            switch (AcsFrameCodec.Decode(message))
            {
                case AudioMetadataFrame metadata:
                    call.Session.SampleRate = metadata.SampleRate > 0 ? metadata.SampleRate : 24000;
                    logger.LogInformation(
                        "Audio format for {SessionId}: {Encoding} {SampleRate}Hz x{Channels}",
                        call.Session.SessionId, metadata.Encoding, metadata.SampleRate, metadata.Channels);
                    break;

                case AudioDataFrame { IsSilent: false } audio:
                    call.Session.AudioFramesReceived++;
                    call.Session.LastAudioAt = DateTimeOffset.UtcNow;
                    perception.PushAudio(
                        audio.ParticipantRawId, audio.Pcm, call.Session.SampleRate);

                    // Both consumers get the same frames. Speech recognition still drives
                    // the Analyst — the coercion detection must not depend on the voice
                    // agent being configured, reachable, or behaving.
                    if (voice is not null)
                    {
                        await voice.PushAudioAsync(audio.Pcm, cancellationToken);
                    }

                    // Participant id matters here in a way it does not for the realtime
                    // agent: this keeps only the enrolled user's channel, which is what
                    // stops a second person in the room being scored as them.
                    call.Biometrics?.Offer(
                        audio.ParticipantRawId, audio.Pcm, call.Session.SampleRate);
                    break;

                case AudioDataFrame:
                    // Silent frame — skipped rather than forwarded, so recognition is not
                    // billed for silence.
                    break;

                case DtmfFrame dtmf when verifications.VerificationForMonitorSession(sessionId) is not null:
                    call.Session.DtmfReceived++;
                    // This is a step-up verification call and the user just pressed a key.
                    // Routed here rather than to the transcript because Call Automation's
                    // DTMF recogniser is built for PSTN, and cannot be assumed to hear a
                    // browser soft-phone's sendDtmf on a pure VoIP leg. This path does not
                    // depend on it.
                    await verifications.OnMediaDtmfAsync(sessionId, dtmf.Tone, cancellationToken);
                    break;

                case DtmfFrame dtmf:
                    // Keypad entry mid-authentication is signal in its own right: it is how
                    // a victim relays an OTP without ever speaking the digits, which would
                    // otherwise leave no trace in the transcript at all.
                    call.Session.AddUtterance(new Utterance(
                        EntraGuard.Shared.Detection.SpeakerRole.ProtectedUser,
                        $"(keypad tone entered: {dtmf.Tone})",
                        call.Session.ElapsedMs(DateTimeOffset.UtcNow),
                        DateTimeOffset.UtcNow,
                        IsFinal: true));
                    break;

                case UnknownFrame unknown:
                    logger.LogDebug("Ignoring frame on {SessionId}: {Reason}",
                        call.Session.SessionId, unknown.Reason);
                    break;
            }
        }
    }

    /// <summary>
    /// Score, decide, act — on a fixed cadence for the life of the call.
    /// </summary>
    private static async Task RunAnalysisLoopAsync(
        LiveCall call,
        AnalystAgent analyst,
        ActuatorAgent actuator,
        EntraGuardOptions options,
        IHubContext<LiveHub> hub,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.AnalysisInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var assessment = await analyst.AssessAsync(call.Session, cancellationToken);
                if (assessment is null)
                {
                    continue;
                }

                call.Session.CurrentAssessment = assessment;

                await hub.Clients.All.SendAsync(LiveHub.AssessmentEvent, new
                {
                    sessionId = call.Session.SessionId,
                    riskScore = assessment.RiskScore,
                    confidence = assessment.Confidence,
                    stage = assessment.Stage.ToString(),
                    vectors = assessment.Vectors.Select(v => v.ToString()),
                    evidence = assessment.Evidence.Select(e => new
                    {
                        e.Quote,
                        speaker = e.Speaker.ToString(),
                    }),
                    assessment.Rationale,
                    assessment.AnalysisLatencyMs,
                    at = assessment.AssessedAt,
                }, cancellationToken);

                var context = new PolicyContext
                {
                    RiskTier = options.RiskTier,
                    AlreadyExecuted = call.Session.ExecutedActions,
                    HasIdentifiedSubject = !string.IsNullOrEmpty(call.Session.SubjectObjectId),
                    AutonomousActionsEnabled = options.AutonomousActionsEnabled,
                };

                var decision = PolicyGate.Evaluate(assessment, context);
                call.Session.PeakRisk = Math.Max(call.Session.PeakRisk, decision.EffectiveRisk);

                await actuator.ExecuteAsync(call.Session, decision, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the call ended.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Analysis loop failed for {SessionId}.", call.Session.SessionId);
        }
    }

    /// <summary>
    /// Stream synthesised PCM back into the live call, framed the way ACS frames its own audio.
    /// </summary>
    private static async Task SendAudioAsync(
        WebSocket socket,
        ReadOnlyMemory<byte> pcm,
        CancellationToken cancellationToken)
    {
        for (var offset = 0; offset < pcm.Length; offset += OutboundFrameBytes)
        {
            if (socket.State != WebSocketState.Open || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var length = Math.Min(OutboundFrameBytes, pcm.Length - offset);
            var json = AcsFrameCodec.EncodeOutboundAudio(pcm.Slice(offset, length).Span);

            await socket.SendAsync(
                Encoding.UTF8.GetBytes(json),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
    }
}
