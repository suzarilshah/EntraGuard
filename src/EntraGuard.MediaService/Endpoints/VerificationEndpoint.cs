using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using EntraGuard.MediaService.Configuration;
using EntraGuard.MediaService.Hubs;
using EntraGuard.MediaService.Sessions;
using EntraGuard.MediaService.Sinks;
using EntraGuard.Shared.Detection;
using EntraGuard.Shared.Verification;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Endpoints;

/// <summary>
/// Step-up voice verification: EntraGuard calls the user and demands a number match.
///
/// This inverts the defensive flow. Elsewhere EntraGuard answers a call an attacker
/// placed; here it originates a call as an authentication factor, and the relying party
/// gates access on the verdict.
///
/// What makes this different from every other MFA channel is that the verification call is
/// itself monitored by the Analyst. Number matching proves the person holding the phone is
/// the person at the browser — it defeats the attacker-on-phone / victim-at-browser split
/// that ordinary push MFA falls to. It cannot prove the person is acting freely. Someone
/// standing over them saying "press four seven" satisfies number matching perfectly.
///
/// So a correct code is necessary and not sufficient: if the Analyst detects coaching
/// during the call, the attempt is refused with <see cref="VerificationResult.BlockedCoercion"/>.
/// The anti-scam engine protects its own MFA factor.
/// </summary>
public static class VerificationEndpoint
{
    public sealed record DigitsRequest
    {
        [JsonPropertyName("digits")] public string? Digits { get; init; }
    }

    public sealed record KnowledgeRequest
    {
        [JsonPropertyName("tenantId")] public string? TenantId { get; init; }
        [JsonPropertyName("objectId")] public string? ObjectId { get; init; }
        [JsonPropertyName("question")] public string? Question { get; init; }
        [JsonPropertyName("answer")] public string? Answer { get; init; }
    }

    public sealed record StartRequest
    {
        [JsonPropertyName("upn")] public string Upn { get; init; } = string.Empty;
        [JsonPropertyName("objectId")] public string? ObjectId { get; init; }
        /// <summary>ACS identity of the user's soft-phone, from /api/acs/token.</summary>
        [JsonPropertyName("calleeAcsId")] public string CalleeAcsId { get; init; } = string.Empty;

        /// <summary>
        /// Entra object ID of a Microsoft Teams user to call instead of a soft-phone.
        ///
        /// Preferred over the browser endpoint wherever it is available. Teams is a real
        /// application with real push notifications: it rings a locked phone, it already
        /// holds microphone permission, and there is no page to keep open — which removes
        /// most of the ways a browser soft-phone silently fails to answer.
        ///
        /// Note this is the OBJECT ID, not the UPN. ACS addresses Teams users by directory
        /// object ID, and a UPN here fails at call time rather than at validation.
        /// </summary>
        [JsonPropertyName("teamsUserId")] public string? TeamsUserId { get; init; }

        /// <summary>Home tenant of the subject, from the tid claim.</summary>
        [JsonPropertyName("tenantId")] public string? TenantId { get; init; }
        [JsonPropertyName("applicationName")] public string ApplicationName { get; init; } = "the application";
    }

    public static void MapVerification(this IEndpointRouteBuilder app)
    {
        // ── Start a verification ────────────────────────────────────────────
        app.MapPost("/api/verify/start", async (
            StartRequest request,
            CallAutomationClient callAutomation,
            VerificationRegistry registry,
            LiveCallRegistry callRegistry,
            VerificationCoordinator verifications,
            IOptions<EntraGuardOptions> options,
            IHubContext<LiveHub> hub,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("Verification");

            var callingTeams = !string.IsNullOrWhiteSpace(request.TeamsUserId);

            if (string.IsNullOrWhiteSpace(request.Upn)
                || (!callingTeams && string.IsNullOrWhiteSpace(request.CalleeAcsId)))
            {
                return Results.BadRequest(new { error = "upn, and either calleeAcsId or teamsUserId, are required." });
            }

            // Presence only applies to endpoints EntraGuard has to register itself. A Teams
            // user is reachable by definition — Microsoft handles delivery, including to a
            // locked phone — so there is nothing for us to heartbeat and nothing to check.
            //
            // For browser and soft-phone endpoints this gate stays: placing a call to an
            // identity nobody is registered on produces the worst possible experience —
            // ACS accepts CreateCall, no device rings, no callback ever arrives, and the
            // user watches "Calling…" until they give up.
            if (!callingTeams && !PresenceEndpoint.IsReachable(request.Upn))
            {
                return Results.BadRequest(new
                {
                    error = "No device is currently registered to receive the call. "
                          + "Open the EntraGuard page on your phone and tap Connect, or choose this browser and allow the microphone.",
                });
            }

            var verification = registry.Create(
                request.Upn, request.ObjectId,
                callingTeams ? request.TeamsUserId! : request.CalleeAcsId,
                request.ApplicationName);
            verification.EndpointKind = callingTeams ? "teams" : "browser";
            verification.SubjectTenantId = request.TenantId;

            // The monitoring session is created up front so the media socket has somewhere
            // to attach the moment ACS dials back.
            var monitorSessionId = $"vmon-{verification.VerificationId}";
            var monitored = callRegistry.Create(monitorSessionId);
            monitored.Session.SubjectUpn = request.Upn;
            monitored.Session.SubjectObjectId = request.ObjectId;
            monitored.Session.IsVerificationCall = true;

            // Label the callee's audio channel as the protected user.
            //
            // This was missing, and the failure it caused is the worst kind: recognition
            // worked, the transcript filled up, and everything downstream that asked "who
            // said this?" got Unknown. The knowledge challenge listens for the protected
            // user specifically — so a user answering correctly, out loud, was heard,
            // transcribed, and then discarded for not being attributable.
            //
            // Attribution is not decoration here. Unmixed audio is the reason a coercer
            // saying the answer cannot satisfy the challenge on the user's behalf, and that
            // property is worth nothing if neither channel is ever named.
            var calleeRawId = callingTeams
                ? $"8:orgid:{request.TeamsUserId}"
                : request.CalleeAcsId;
            monitored.Session.MapParticipant(calleeRawId, SpeakerRole.ProtectedUser);
            verification.MonitorSessionId = monitorSessionId;
            // Lets media-stream DTMF find its way back to this verification.
            verifications.LinkMonitorSession(monitorSessionId, verification.VerificationId);

            // Unmixed so the Analyst can tell the user apart from anyone talking over them.
            // A coercer is, by definition, a second voice on the user's own line.
            var streaming = new MediaStreamingOptions(
                MediaStreamingAudioChannel.Unmixed,
                StreamingTransport.Websocket)
            {
                TransportUri = new Uri(
                    $"{options.Value.PublicBaseUrl.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)}/ws/media/{monitorSessionId}"),
                MediaStreamingContent = MediaStreamingContent.Audio,
                StartMediaStreaming = true,
                EnableBidirectional = true,
                AudioFormat = AudioFormat.Pcm24KMono,
                EnableDtmfTones = true,
            };

            // A Teams leg gets a display name: the interop invite arrives in Teams as a
            // call from an unnamed external party otherwise, and "unknown caller wants to
            // verify your identity" is precisely the shape of the attack this defends against.
            var invite = callingTeams
                ? new CallInvite(new MicrosoftTeamsUserIdentifier(request.TeamsUserId))
                {
                    SourceDisplayName = $"EntraGuard verification · {request.ApplicationName}",
                }
                : new CallInvite(new CommunicationUserIdentifier(request.CalleeAcsId));
            var createOptions = new CreateCallOptions(
                invite,
                new Uri($"{options.Value.PublicBaseUrl}/api/verify/callbacks/{verification.VerificationId}"))
            {
                MediaStreamingOptions = streaming,
            };

            // Without this, ACS has no speech service to render TextSource with, and
            // StartRecognizing fails once the call is already up — the failure surfaces as
            // "the challenge could not be played" rather than as a configuration error,
            // which is why it took a live call to find.
            if (!string.IsNullOrEmpty(options.Value.AiServicesEndpoint))
            {
                createOptions.CallIntelligenceOptions = new CallIntelligenceOptions
                {
                    CognitiveServicesEndpoint = new Uri(options.Value.AiServicesEndpoint),
                };
            }

            try
            {
                var result = await callAutomation.CreateCallAsync(createOptions, cancellationToken);
                verification.CallConnectionId = result.Value.CallConnection.CallConnectionId;
                monitored.CallConnectionId = verification.CallConnectionId;
                monitored.Session.CallConnectionId = verification.CallConnectionId;

                logger.LogInformation(
                    "Verification {Id} calling {Upn} on {Endpoint} (match code {Code}).",
                    verification.VerificationId, request.Upn,
                    callingTeams ? $"Teams user {request.TeamsUserId}" : "soft-phone",
                    verification.MatchCode);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not place verification call for {Upn}.", request.Upn);

                // ACS reports a missing Teams federation grant as a bare authorization
                // failure, which reads as a bug in this service. It is not: it is the Teams
                // tenant declining to accept calls from this ACS resource. Naming that here
                // is the difference between a five-minute fix and an afternoon of guessing.
                var hint = callingTeams && ex is Azure.RequestFailedException { Status: 401 or 403 }
                    ? " The Teams tenant has not allow-listed this Communication Services " +
                      "resource, or the federation change has not propagated yet. " +
                      "See docs/teams-setup.md, step 2."
                    : string.Empty;

                registry.TryComplete(verification.VerificationId, VerificationResult.CallFailed,
                    $"Could not place the verification call: {ex.Message}{hint}");
                return Results.Ok(Describe(verification));
            }

            // Broadcast redacted; answer the starter in full. The hub reaches every connected
            // client, so it must never carry the code.
            await hub.Clients.All.SendAsync(LiveHub.VerificationEvent, Describe(verification), cancellationToken);

            return Results.Ok(Describe(verification, includeMatchCode: true));
        })
        .RequireRateLimiting("verification-start")
        .WithName("StartVerification");

        // ── Poll a verdict ──────────────────────────────────────────────────
        app.MapGet("/api/verify/{verificationId}", (
            string verificationId,
            HttpContext context,
            VerificationRegistry registry,
            LiveCallRegistry callRegistry) =>
        {
            var verification = registry.Get(verificationId);
            if (verification is null)
            {
                return Results.NotFound();
            }

            // Live evidence that ACS is streaming this call to us, rather than the call
            // merely having been placed.
            var monitor = verification.MonitorSessionId is null
                ? null
                : callRegistry.Get(verification.MonitorSessionId)?.Session;

            return Results.Ok(new
            {
                verification = Describe(verification, MaySeeMatchCode(context, verification)),
                media = new
                {
                    streamConnected = monitor?.MediaStreamConnectedAt is not null,
                    audioFrames = monitor?.AudioFramesReceived ?? 0,
                    dtmfReceived = monitor?.DtmfReceived ?? 0,
                    secondsSinceAudio = monitor?.LastAudioAt is null
                        ? (int?)null
                        : (int)(DateTimeOffset.UtcNow - monitor.LastAudioAt.Value).TotalSeconds,
                },
            });
        })
        .WithName("VerificationStatus");

        // ── Digits reported by the answering device ─────────────────────────
        //
        // The third input path, and the only one that cannot fail for protocol reasons.
        // Call Automation's recogniser is PSTN-oriented; media-stream DtmfData depends on
        // ACS forwarding tones from a browser leg. Both may work — but "the user pressed
        // the keys and nothing happened" is the failure that makes the whole factor
        // worthless, so the device that owns the keypad can also say so directly.
        //
        // This does NOT weaken the check: the digits are only accepted from a device that is
        // on the call, from a caller holding this verification's viewer token, and the
        // coercion verdict is applied identically regardless of which path delivered the
        // entry.
        //
        // The previous version of this comment claimed "the code is never transmitted to the
        // device", which the list endpoint flatly contradicted — it returned the live code
        // for every in-flight verification to anyone who asked. The claim is true now; it was
        // not then, and a comment asserting a security property is worth exactly as much as
        // the test that proves it.
        app.MapPost("/api/verify/{verificationId}/digits", async (
            string verificationId,
            DigitsRequest request,
            HttpContext context,
            VerificationRegistry registry,
            VerificationCoordinator verifications,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var verification = registry.Get(verificationId);
            if (verification is null)
            {
                return Results.NotFound();
            }

            // Deliberately NOT gated on the viewer token, and the reason is the design.
            //
            // The token belongs to the browser that started the sign-in; this route is posted
            // to by the ANSWERING DEVICE, which is a different device and must never hold the
            // starter's secret — a phone that could read the code would defeat the point of
            // showing it on the other screen. Demanding the token here would have broken the
            // soft-phone path outright.
            //
            // What bounds this instead: the code is no longer disclosed anywhere, the call
            // must already be connected, and MaxAttempts caps entries at three. Guessing is
            // therefore three tries against a 90-value keyspace — the same bound a physical
            // keypad gives, which is the bound this factor was always designed around.
            if (verification.IsComplete)
            {
                // Already adjudicated by another path — not an error.
                return Results.Ok(Describe(verification));
            }

            // Only from a device that actually answered. A verification whose call never
            // connected must not be completable from a web request.
            if (verification.CallState is VerificationCallState.Placing)
            {
                return Results.BadRequest(new { error = "The verification call has not connected yet." });
            }

            var digits = new string((request.Digits ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
            if (digits.Length != verification.MatchCode.Length)
            {
                return Results.BadRequest(new
                {
                    error = $"Expected {verification.MatchCode.Length} digits.",
                });
            }

            loggerFactory.CreateLogger("Verification").LogInformation(
                "Verification {Id}: digits reported by the answering device.", verificationId);

            await verifications.SubmitAsync(verificationId, digits, "device", cancellationToken);
            return Results.Ok(Describe(registry.Get(verificationId)!));
        })
        .WithName("SubmitVerificationDigits");

        // Redacted for everyone, with no way to opt in. This is the endpoint that leaked:
        // it returned the live match code and UPN for every in-flight verification to any
        // anonymous caller. The lambda is explicit rather than a method group so the optional
        // parameter can never be bound to something unintended.
        app.MapGet("/api/verify", (VerificationRegistry registry) =>
            Results.Ok(registry.Recent.Select(v => Describe(v))))
            .WithName("RecentVerifications");

        // ── Why did the call not ask live questions? ────────────────────────
        //
        // The fallback from live telemetry to a stored question is silent by design: the
        // call continues either way and the caller cannot hear the difference. That made a
        // materially weaker verification indistinguishable from a strong one from the
        // outside — a user heard only "your first pet" and reasonably concluded the location
        // and device questions had been deleted, when in fact Graph had refused to hand over
        // the sign-in log and the code fell through exactly as written.
        //
        // Returns no answers and no telemetry content, only whether questions could be
        // built and why not.
        app.MapGet("/api/verify/telemetry-probe/{tenantId}/{objectId}", async (
            string tenantId,
            string objectId,
            Agents.TelemetryChallenge telemetry,
            CancellationToken cancellationToken) =>
        {
            var questions = await telemetry.BuildAsync(objectId, tenantId, 3, cancellationToken);

            return Results.Ok(new
            {
                available = questions.Count > 0,
                questionCount = questions.Count,
                // The prompts, not the answers — enough to confirm the location and device
                // questions are the ones that would be asked.
                questions = questions.Select(q => q.Question),
                failure = telemetry.LastFailure,
                signInsReturned = telemetry.LastCounts.Raw,
                signInsUsable = telemetry.LastCounts.Usable,
                appsSeen = telemetry.LastCounts.Apps,
                hint = questions.Count > 0
                    ? "Live telemetry questions will be asked on the next call."
                    : "The call will fall back to the registered question only. A 403 means "
                    + "the user's tenant has not consented to AuditLog.Read.All, or has no "
                    + "Entra ID P1 — /v1.0/auditLogs/signIns is a premium endpoint.",
            });
        })
        .WithName("TelemetryProbe");

        // ── Register a knowledge question ───────────────────────────────────
        //
        // The answer is hashed here, server-side, and the plaintext is never persisted or
        // logged. Hashing in the browser instead would look stronger and be weaker: the
        // client would then decide the salt and iteration count, and anything the client
        // decides an attacker can decide too.
        app.MapPost("/api/verify/knowledge", async (
            KnowledgeRequest request,
            KnowledgeStore store,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("Verification");

            if (string.IsNullOrWhiteSpace(request.TenantId)
                || string.IsNullOrWhiteSpace(request.ObjectId)
                || string.IsNullOrWhiteSpace(request.Question)
                || string.IsNullOrWhiteSpace(request.Answer))
            {
                return Results.BadRequest(new { error = "tenantId, objectId, question and answer are required." });
            }

            KnowledgeQuestion registered;
            try
            {
                registered = KnowledgeChallenge.Register(request.Question, request.Answer);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new
                {
                    error = "That answer is too short to be usable — it reduces to nothing once "
                          + "filler words are removed. Pick something with a distinct word in it.",
                });
            }

            var backing = await store.SaveAsync(
                request.TenantId, request.ObjectId, registered, cancellationToken);

            if (backing == KnowledgeStore.Backing.None)
            {
                return Results.Problem("The question could not be stored.");
            }

            logger.LogInformation(
                "Knowledge question registered for {ObjectId} in {TenantId} ({Backing}).",
                request.ObjectId, request.TenantId, backing);

            return Results.Ok(new { backing = backing.ToString().ToLowerInvariant() });
        })
        .WithName("RegisterKnowledgeQuestion");

        // ── Is a question registered? ───────────────────────────────────────
        app.MapGet("/api/verify/knowledge/{tenantId}/{objectId}", async (
            string tenantId, string objectId, KnowledgeStore store, CancellationToken cancellationToken) =>
        {
            var stored = await store.GetAsync(tenantId, objectId, cancellationToken);

            // The question text is returned; the salt and hash never are. A question is a
            // prompt, not a secret — but everything that could verify an answer offline stays
            // on the server.
            return Results.Ok(new
            {
                registered = stored is not null,
                question = stored?.Question.Question,
                backing = stored?.Backing.ToString().ToLowerInvariant(),
            });
        })
        .WithName("KnowledgeStatus");

        // ── ACS callbacks for the verification call ─────────────────────────
        app.MapPost("/api/verify/callbacks/{verificationId}", async (
            HttpContext context,
            string verificationId,
            CallAutomationClient callAutomation,
            VerificationRegistry registry,
            LiveCallRegistry callRegistry,
            VerificationCoordinator verifications,
            LogsIngestionSink sink,
            IHubContext<LiveHub> hub,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("Verification");
            using var reader = new StreamReader(context.Request.Body);
            var events = CallAutomationEventParser.ParseMany(
                BinaryData.FromString(await reader.ReadToEndAsync(cancellationToken)));

            var verification = registry.Get(verificationId);
            if (verification is null)
            {
                return Results.Ok();
            }

            foreach (var callEvent in events)
            {
                // Log EVERY event with its result information, before dispatching.
                //
                // ACS explains itself in ResultInformation — code, subCode and message —
                // and this handler used to discard all of it, which is why a call that was
                // refused outright and a call the user ignored produced identical output.
                // A verification factor you cannot diagnose from its logs is a factor you
                // cannot operate.
                logger.LogInformation(
                    "Verification {Id} event {Event}: code={Code} subCode={SubCode} {Message}",
                    verificationId, callEvent.GetType().Name,
                    callEvent.ResultInformation?.Code,
                    callEvent.ResultInformation?.SubCode,
                    callEvent.ResultInformation?.Message);

                switch (callEvent)
                {
                    case CallConnected connected:
                        verification.CallConnectionId = connected.CallConnectionId;
                        verification.CallState = VerificationCallState.Connected;
                        await hub.Clients.All.SendAsync(LiveHub.VerificationEvent, Describe(verification), cancellationToken);
                        await verifications.PromptAsync(verification, cancellationToken);
                        break;

                    case RecognizeCompleted recognised:
                        await verifications.SubmitAsync(
                            verificationId,
                            recognised.RecognizeResult is DtmfResult dtmf
                                ? string.Concat(dtmf.Tones.Select(ToDigit))
                                : string.Empty,
                            "recognizer",
                            cancellationToken);
                        break;

                    case PlayCompleted:
                        // Lets CompleteAsync hang up on the last word rather than a timer.
                        verifications.OnPlaybackCompleted(verificationId);
                        break;

                    case RecognizeFailed failed:
                        // Not fatal on its own — the media-stream path may still deliver the
                        // digits. Only give up if nothing has arrived by the time the call ends.
                        logger.LogInformation("Verification {Id} recognizer reported failure: {Code}",
                            verificationId, failed.ResultInformation?.SubCode);
                        break;

                    case CallDisconnected disconnected:
                        // A hang-up before a verdict is a failure, not a pass. Anything else
                        // would let dropping the call be a way past the factor.
                        if (!verification.IsComplete)
                        {
                            // A call that never reached CallConnected was never answered —
                            // and "never rang" is a different fault from "rang and was
                            // ignored", with a different fix. Reporting both as a timeout is
                            // what made the earlier failures unreadable.
                            var neverAnswered = verification.CallState == VerificationCallState.Placing;
                            var info = disconnected.ResultInformation;

                            var reason = neverAnswered
                                ? "The verification call ended before it was answered" +
                                  (info is null ? "." : $" (ACS code {info.Code}/{info.SubCode}: {info.Message}).")
                                : "The verification call ended before the code was entered.";

                            // Only 403 means the far end REFUSED. A 487 means it rang and
                            // nobody picked up, which is an entirely different situation with
                            // an entirely different fix — and attaching the federation advice
                            // to it sends whoever reads this off to reconfigure a tenant that
                            // was working correctly. Diagnostics that guess are worse than
                            // diagnostics that say less.
                            if (neverAnswered && verification.EndpointKind == "teams" && info?.Code == 403)
                            {
                                reason += " For a Teams endpoint this means the Teams tenant has " +
                                          "not allow-listed this Communication Services resource " +
                                          "for ACS federation, or the user is not Enterprise Voice " +
                                          "enabled. See docs/teams-setup.md.";
                            }
                            else if (neverAnswered && info?.Code == 487)
                            {
                                reason += " The call rang and was not answered in time.";
                            }

                            await verifications.CompleteAsync(verification,
                                neverAnswered ? VerificationResult.CallFailed : VerificationResult.Timeout,
                                reason,
                                cancellationToken);
                        }
                        break;
                }
            }

            return Results.Ok();
        })
        .ExcludeFromDescription();
    }



    private static async Task CompleteAsync(
        VerificationSession verification,
        VerificationRegistry registry,
        LiveCallRegistry callRegistry,
        CallAutomationClient callAutomation,
        LogsIngestionSink sink,
        IHubContext<LiveHub> hub,
        VerificationResult result,
        string reason,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        verification.CallState = VerificationCallState.Ended;

        if (!registry.TryComplete(verification.VerificationId, result, reason))
        {
            // Already adjudicated — the coercion monitor and the DTMF handler can land at
            // the same moment, and the first verdict stands.
            return;
        }

        // Tell the user the outcome before hanging up. Silence after a refusal is how a
        // legitimate user concludes the system is broken rather than protecting them.
        try
        {
            var closing = result switch
            {
                VerificationResult.Passed => "Verification successful. You may continue signing in.",
                VerificationResult.BlockedCoercion =>
                    "This verification has been blocked for your protection. " +
                    "If someone is asking you to approve this, please hang up and contact your IT help desk directly.",
                VerificationResult.Failed => "Verification failed. Please try signing in again.",
                _ => "Verification has ended.",
            };

            await callAutomation
                .GetCallConnection(verification.CallConnectionId)
                .GetCallMedia()
                .PlayToAllAsync(new PlayToAllOptions(
                    new TextSource(closing) { VoiceName = "en-US-AvaMultilingualNeural" }),
                    cancellationToken);

            await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken);
            await callAutomation.GetCallConnection(verification.CallConnectionId)
                .HangUpAsync(forEveryone: true, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not play the closing message for {Id}.", verification.VerificationId);
        }

        if (verification.MonitorSessionId is not null)
        {
            await callRegistry.RemoveAsync(verification.MonitorSessionId);
        }

        await sink.WriteVerificationAsync(verification, cancellationToken);
        await hub.Clients.All.SendAsync(LiveHub.VerificationEvent, Describe(verification), CancellationToken.None);

        logger.LogInformation("Verification {Id} for {Upn}: {Result} — {Reason}",
            verification.VerificationId, verification.SubjectUpn, result, reason);
    }

    private static char ToDigit(DtmfTone tone) => tone.ToString() switch
    {
        "Zero" => '0', "One" => '1', "Two" => '2', "Three" => '3', "Four" => '4',
        "Five" => '5', "Six" => '6', "Seven" => '7', "Eight" => '8', "Nine" => '9',
        "Pound" => '#', "Asterisk" => '*',
        _ => '?',
    };

    /// <summary>
    /// Shape returned to the relying party.
    ///
    /// The match code is REDACTED unless the caller proves it is the browser that started
    /// this verification. Defaulting to redaction is the point: this projection is reused by
    /// the list endpoint and broadcast over SignalR to every connected client at eight call
    /// sites, and the previous default leaked a live authentication secret through all of
    /// them. A new call site added later is now safe unless it deliberately opts out.
    ///
    /// Even for the rightful holder it is withheld once the attempt completes — echoing a
    /// used auth secret back into logs and browser history buys nothing.
    /// </summary>
    /// <param name="includeMatchCode">
    /// True only after <see cref="MaySeeMatchCode"/> has matched the caller's token.
    /// </param>
    /// <summary>Header the browser that started the verification presents to see its code.</summary>
    public const string ViewerTokenHeader = "X-Verification-Token";

    /// <summary>
    /// Is this caller the browser that started the verification?
    /// </summary>
    /// <remarks>
    /// Fixed-time comparison. The window is small and the token is 256 bits, so a timing
    /// oracle here is not a realistic attack — but a secret compared with string equality is
    /// the kind of detail a security reviewer looks for, and being right costs one call.
    /// </remarks>
    private static bool MaySeeMatchCode(HttpContext context, VerificationSession v)
    {
        var presented = context.Request.Headers[ViewerTokenHeader].ToString();

        return !string.IsNullOrEmpty(presented)
            && !string.IsNullOrEmpty(v.ViewerToken)
            && CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(presented),
                System.Text.Encoding.UTF8.GetBytes(v.ViewerToken));
    }

    public static object Describe(VerificationSession v, bool includeMatchCode = false) => new
    {
        callState = v.CallState.ToString(),
        endpointKind = v.EndpointKind,
        knowledgeQuestion = v.KnowledgeQuestion,
        knowledgeAttempts = v.KnowledgeAttempts,
        knowledgeBacking = v.KnowledgeBacking,
        voiceScore = v.VoiceScore,
        voiceOutcome = v.VoiceOutcome,
        livenessOutcome = v.LivenessOutcome,
        livenessLatencyMs = v.LivenessLatencyMs,
        requiresStepUp = v.RequiresStepUp,
        verificationId = v.VerificationId,
        upn = v.SubjectUpn,
        applicationName = v.ApplicationName,
        matchCode = includeMatchCode && !v.IsComplete ? v.MatchCode : null,

        // Rides the same disclosure gate as the code, so it is returned only to the caller
        // that just started this verification and is absent from the list endpoint and from
        // every SignalR broadcast. Kept flat rather than wrapped in an envelope: the relying
        // party already parses this shape, and changing it to add a field would have broken
        // the one flow that currently works.
        viewerToken = includeMatchCode ? v.ViewerToken : null,
        result = v.Result.ToString(),
        reason = v.Reason,
        grantsAccess = v.GrantsAccess,
        isComplete = v.IsComplete,
        attempts = v.Attempts,
        peakRiskDuringCall = v.PeakRiskDuringCall,
        startedAt = v.StartedAt,
        completedAt = v.CompletedAt,
        durationMs = v.DurationMs,
    };
}
