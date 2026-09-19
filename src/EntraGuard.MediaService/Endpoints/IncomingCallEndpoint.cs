using System.Collections.Concurrent;
using System.Text.Json;
using Azure.Communication.CallAutomation;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using EntraGuard.MediaService.Configuration;
using EntraGuard.MediaService.Hubs;
using EntraGuard.MediaService.Sessions;
using EntraGuard.Shared.Detection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using EntraGuard.MediaService.Auth;

namespace EntraGuard.MediaService.Endpoints;

/// <summary>
/// Answers inbound calls and attaches the media stream.
///
/// Latency is a correctness property here, not a nicety: a call rings for roughly 30
/// seconds, and everything between receiving the Event Grid notification and calling
/// AnswerCall runs inside that budget. Nothing slow — no database lookups, no Graph calls —
/// belongs on this path. Subject resolution happens afterwards, once the call is up.
/// </summary>
public static class IncomingCallEndpoint
{
    /// <summary>
    /// Event Grid delivers at least once, and a redirected call can produce a second
    /// IncomingCall for the same conversation. Answering twice fails noisily and looks
    /// exactly like a broken demo, so correlation IDs seen recently are dropped.
    /// </summary>
    private static readonly ConcurrentDictionary<string, DateTimeOffset> RecentCorrelationIds = new();

    private static readonly TimeSpan DedupeWindow = TimeSpan.FromMinutes(2);

    public static void MapIncomingCall(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/events/incoming-call", async (
            HttpContext context,
            CallAutomationClient callAutomation,
            LiveCallRegistry registry,
            TransportProtection transport,
            IOptions<EntraGuardOptions> options,
            IHubContext<LiveHub> hub,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("IncomingCall");

            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var events = EventGridEvent.ParseMany(BinaryData.FromString(body));

            foreach (var gridEvent in events)
            {
                // Event Grid proves endpoint ownership before delivering anything. Failing
                // this handshake means the subscription never activates and no call is ever
                // intercepted — with no error surfaced anywhere obvious.
                if (gridEvent.TryGetSystemEventData(out var systemEvent) &&
                    systemEvent is SubscriptionValidationEventData validation)
                {
                    logger.LogInformation("Event Grid subscription validation handshake received.");
                    return Results.Ok(new SubscriptionValidationResponse
                    {
                        ValidationResponse = validation.ValidationCode,
                    });
                }

                if (gridEvent.EventType != "Microsoft.Communication.IncomingCall")
                {
                    continue;
                }

                await HandleIncomingCallAsync(
                    gridEvent, callAutomation, registry, options.Value, hub, logger, transport, cancellationToken);
            }

            return Results.Ok();
        })
        .WithName("IncomingCall")
        .ExcludeFromDescription();
    }

    private static async Task HandleIncomingCallAsync(
        EventGridEvent gridEvent,
        CallAutomationClient callAutomation,
        LiveCallRegistry registry,
        EntraGuardOptions options,
        IHubContext<LiveHub> hub,
        ILogger logger,
        TransportProtection transport,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(gridEvent.Data.ToString());
        var data = document.RootElement;

        var incomingCallContext = data.GetProperty("incomingCallContext").GetString();
        var correlationId = data.TryGetProperty("correlationId", out var c) ? c.GetString() : null;

        if (string.IsNullOrEmpty(incomingCallContext))
        {
            logger.LogWarning("IncomingCall event carried no incomingCallContext; ignoring.");
            return;
        }

        if (!string.IsNullOrEmpty(correlationId) && !TryClaimCorrelationId(correlationId))
        {
            logger.LogInformation("Duplicate IncomingCall for correlation {CorrelationId}; already answered.",
                correlationId);
            return;
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var call = registry.Create(sessionId);
        call.Session.AcsCorrelationId = correlationId;
        call.Session.CallerIdentity = ReadRawId(data, "from");

        // The caller is the counterparty; the callee is the protected user. Attribution
        // comes from the call topology rather than from anything the model infers, which is
        // what makes "the CALLER said this" safe to act on.
        if (!string.IsNullOrEmpty(call.Session.CallerIdentity))
        {
            call.Session.MapParticipant(call.Session.CallerIdentity, SpeakerRole.Caller);
        }

        var calleeRawId = ReadRawId(data, "to");
        if (!string.IsNullOrEmpty(calleeRawId))
        {
            call.Session.MapParticipant(calleeRawId, SpeakerRole.ProtectedUser);
        }

        var websocketUri = new Uri(transport.Url(
            $"{options.PublicBaseUrl.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)}/ws/media/{sessionId}"));

        var mediaStreaming = new MediaStreamingOptions(
            // Unmixed gives one channel per participant with a participantRawID, which is
            // what lets EntraGuard attribute coaching language to the caller rather than to
            // the victim. Mixed audio would make that attribution guesswork.
            MediaStreamingAudioChannel.Unmixed,
            StreamingTransport.Websocket)
        {
            TransportUri = websocketUri,
            MediaStreamingContent = MediaStreamingContent.Audio,
            // Start with the call rather than on a later API call: the scam is already
            // under way when the phone is answered, so there is no safe moment to miss.
            StartMediaStreaming = true,
            // Bidirectional is what makes the spoken warning possible. Receive-only would
            // reduce EntraGuard to after-the-fact reporting.
            EnableBidirectional = true,
            AudioFormat = AudioFormat.Pcm24KMono,
            // Keypad entry is how a victim reads back an OTP without saying the digits aloud.
            EnableDtmfTones = true,
        };

        var answerOptions = new AnswerCallOptions(
            incomingCallContext,
            new Uri(transport.Url($"{options.PublicBaseUrl}/api/callbacks/{sessionId}")))
        {
            MediaStreamingOptions = mediaStreaming,
        };

        // The injected spoken warning is synthesised by ACS from a TextSource, so the
        // answered call needs the same AI services link the verification call does.
        if (!string.IsNullOrEmpty(options.AiServicesEndpoint))
        {
            answerOptions.CallIntelligenceOptions = new CallIntelligenceOptions
            {
                CognitiveServicesEndpoint = new Uri(options.AiServicesEndpoint),
            };
        }

        try
        {
            var result = await callAutomation.AnswerCallAsync(answerOptions, cancellationToken);
            call.CallConnectionId = result.Value.CallConnection.CallConnectionId;
            call.Session.CallConnectionId = call.CallConnectionId;

            logger.LogInformation(
                "Answered call {SessionId} (connection {ConnectionId}, correlation {CorrelationId}).",
                sessionId, call.CallConnectionId, correlationId);

            await hub.Clients.All.SendAsync(LiveHub.SessionEvent, new
            {
                sessionId,
                state = "answered",
                caller = call.Session.CallerIdentity,
                at = DateTimeOffset.UtcNow,
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to answer call {SessionId}. If this is ACS 8581, PUBLIC_BASE_URL ({Url}) " +
                "is not reachable from ACS over wss.", sessionId, options.PublicBaseUrl);
            await registry.RemoveAsync(sessionId);
        }
    }

    /// <summary>Claim a correlation ID, evicting entries older than the dedupe window.</summary>
    private static bool TryClaimCorrelationId(string correlationId)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var (key, seenAt) in RecentCorrelationIds)
        {
            if (now - seenAt > DedupeWindow)
            {
                RecentCorrelationIds.TryRemove(key, out _);
            }
        }

        return RecentCorrelationIds.TryAdd(correlationId, now);
    }

    /// <summary>
    /// Extract a participant raw ID from the event's <c>from</c> / <c>to</c> objects, which
    /// carry either a <c>rawId</c>, a nested <c>communicationUser</c>, or a phone number
    /// depending on the participant kind.
    /// </summary>
    private static string? ReadRawId(JsonElement data, string property)
    {
        if (!data.TryGetProperty(property, out var participant) ||
            participant.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (participant.TryGetProperty("rawId", out var rawId) && rawId.ValueKind == JsonValueKind.String)
        {
            return rawId.GetString();
        }

        if (participant.TryGetProperty("communicationUser", out var communicationUser) &&
            communicationUser.TryGetProperty("id", out var id))
        {
            return id.GetString();
        }

        if (participant.TryGetProperty("phoneNumber", out var phone) &&
            phone.TryGetProperty("value", out var value))
        {
            return value.GetString();
        }

        return null;
    }
}
