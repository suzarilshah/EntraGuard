using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Sessions;

namespace EntraGuard.MediaService.Endpoints;

public static class PresenceEndpoint
{
    public sealed record Heartbeat(string AcsUserId, string DeviceKind = "browser");
    public static void MapPresence(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/presence", async (Heartbeat beat, HttpContext context, DeviceService devices, CancellationToken ct) =>
        {
            var session = context.User.FindFirst(RpSessionService.Claim)?.Value;
            if (session is null) return Results.Unauthorized();
            return await devices.HeartbeatAsync(Owner.From(context.User), beat.DeviceKind, beat.AcsUserId, session, ct)
                ? Results.Ok(new { registered = true }) : Results.NotFound();
        });
        app.MapGet("/api/presence/{upn}", async (HttpContext context, DeviceService devices, TimeProvider time, CancellationToken ct) =>
        {
            // Kept for route compatibility; the path's UPN is never an ownership key.
            var owner = Owner.From(context.User);
            var live = (await devices.ListAsync(owner, ct)).Where(devices.Reachable).ToArray();
            return Results.Ok(new { any = live.Length > 0, endpoints = live.Select(d => new
            { deviceKind = d.Kind, acsUserId = d.AcsUserId, lastSeenSecondsAgo = (int)(time.GetUtcNow() - d.LastSeen!.Value).TotalSeconds }) });
        });
        app.MapDelete("/api/presence/{upn}/{deviceKind}", async (string deviceKind, HttpContext context, DeviceService devices, CancellationToken ct) =>
            await devices.RevokeAsync(Owner.From(context.User), deviceKind, ct) ? Results.NoContent() : Results.Conflict());
    }
}
