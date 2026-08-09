using Azure.Communication.CallAutomation;
using EntraGuard.MediaService.Hubs;
using EntraGuard.MediaService.Sessions;
using Microsoft.AspNetCore.SignalR;

namespace EntraGuard.MediaService.Endpoints;

/// <summary>
/// ACS Call Automation lifecycle callbacks.
///
/// Separate from the Event Grid endpoint: Event Grid tells us a call is arriving, these
/// tell us what happened to a call we already answered.
/// </summary>
public static class CallbackEndpoint
{
    public static void MapCallbacks(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/callbacks/{sessionId}", async (
            HttpContext context,
            string sessionId,
            LiveCallRegistry registry,
            IHubContext<LiveHub> hub,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("Callbacks");

            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            var events = CallAutomationEventParser.ParseMany(BinaryData.FromString(body));

            foreach (var callEvent in events)
            {
                logger.LogInformation("ACS callback for {SessionId}: {EventType}",
                    sessionId, callEvent.GetType().Name);

                switch (callEvent)
                {
                    case CallConnected connected:
                        var call = registry.Get(sessionId);
                        if (call is not null)
                        {
                            call.CallConnectionId = connected.CallConnectionId;
                            call.Session.CallConnectionId = connected.CallConnectionId;
                            call.Session.ServerCallId = connected.ServerCallId;
                        }
                        await hub.Clients.All.SendAsync(LiveHub.SessionEvent, new
                        {
                            sessionId, state = "connected", at = DateTimeOffset.UtcNow,
                        }, cancellationToken);
                        break;

                    case MediaStreamingStarted:
                        logger.LogInformation("Media streaming started for {SessionId}.", sessionId);
                        break;

                    case MediaStreamingFailed failed:
                        // Almost always an unreachable transport URL (ACS subcode 8581).
                        // Called out explicitly because the failure surfaces here while the
                        // cause is a configuration value set somewhere else entirely.
                        logger.LogError(
                            "Media streaming FAILED for {SessionId}: {Code}/{SubCode} {Message}. " +
                            "Check that PUBLIC_BASE_URL is publicly reachable over wss.",
                            sessionId, failed.ResultInformation?.Code,
                            failed.ResultInformation?.SubCode, failed.ResultInformation?.Message);
                        break;

                    case CallDisconnected:
                        await hub.Clients.All.SendAsync(LiveHub.SessionEvent, new
                        {
                            sessionId, state = "disconnected", at = DateTimeOffset.UtcNow,
                        }, cancellationToken);
                        await registry.RemoveAsync(sessionId);
                        break;
                }
            }

            return Results.Ok();
        })
        .WithName("CallAutomationCallbacks")
        .ExcludeFromDescription();
    }
}
