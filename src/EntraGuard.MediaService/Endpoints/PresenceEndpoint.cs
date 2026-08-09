using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace EntraGuard.MediaService.Endpoints;

/// <summary>
/// Tracks which endpoints are actually registered and able to answer a call.
///
/// This exists because of a real failure: enrollment previously decided a phone was
/// "connected" by asking the token broker for an ACS identity. The broker always returns
/// one — it mints or looks up an identity regardless of whether any device is registered
/// on it — so the check could never fail. The UI showed "Connected", the user pressed
/// Continue, EntraGuard placed a call to an identity nobody was listening on, and the
/// phone never rang.
///
/// The lesson generalises: a readiness check that cannot fail is not a check. Presence has
/// to be reported BY the device that will answer, not inferred by the server that wants it
/// to be there.
/// </summary>
public static class PresenceEndpoint
{
    private sealed record Presence(string AcsUserId, string DeviceKind, DateTimeOffset LastSeen);

    private static readonly ConcurrentDictionary<string, Presence> Registered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How long a heartbeat keeps an endpoint considered reachable.
    ///
    /// The device re-reports every 10 seconds, so this tolerates two missed beats. Short
    /// enough that a closed browser tab stops counting as a phone before anyone tries to
    /// call it.
    /// </summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    public sealed record Heartbeat
    {
        [JsonPropertyName("upn")] public string Upn { get; init; } = string.Empty;
        [JsonPropertyName("acsUserId")] public string AcsUserId { get; init; } = string.Empty;
        /// <summary>"phone" or "browser" — shown to the user so they know what will ring.</summary>
        [JsonPropertyName("deviceKind")] public string DeviceKind { get; init; } = "browser";
    }

    public static void MapPresence(this IEndpointRouteBuilder app)
    {
        // Reported by the device once its ACS CallAgent is live and listening.
        app.MapPost("/api/presence", (Heartbeat beat, ILoggerFactory loggerFactory) =>
        {
            if (string.IsNullOrWhiteSpace(beat.Upn) || string.IsNullOrWhiteSpace(beat.AcsUserId))
            {
                return Results.BadRequest(new { error = "upn and acsUserId are required." });
            }

            var key = Key(beat.Upn, beat.DeviceKind);
            var isNew = !Registered.ContainsKey(key);
            Registered[key] = new Presence(beat.AcsUserId, beat.DeviceKind, DateTimeOffset.UtcNow);

            if (isNew)
            {
                loggerFactory.CreateLogger("Presence").LogInformation(
                    "{DeviceKind} registered for {Upn} and is ready to receive calls.",
                    beat.DeviceKind, beat.Upn);
            }

            return Results.Ok(new { registered = true });
        })
        .WithName("ReportPresence");

        // Asked by the desktop before it offers to start a verification.
        app.MapGet("/api/presence/{upn}", (string upn) =>
        {
            var now = DateTimeOffset.UtcNow;
            var live = new[] { "phone", "browser" }
                .Select(kind => (kind, present: Registered.TryGetValue(Key(upn, kind), out var p) && now - p.LastSeen < StaleAfter
                    ? p : null))
                .Where(entry => entry.present is not null)
                .ToList();

            return Results.Ok(new
            {
                any = live.Count > 0,
                endpoints = live.Select(entry => new
                {
                    deviceKind = entry.kind,
                    acsUserId = entry.present!.AcsUserId,
                    lastSeenSecondsAgo = (int)(now - entry.present.LastSeen).TotalSeconds,
                }),
            });
        })
        .WithName("CheckPresence");

        app.MapDelete("/api/presence/{upn}/{deviceKind}", (string upn, string deviceKind) =>
        {
            Registered.TryRemove(Key(upn, deviceKind), out _);
            return Results.NoContent();
        })
        .WithName("ClearPresence");
    }

    /// <summary>True when at least one device for this user is live enough to answer.</summary>
    public static bool IsReachable(string upn)
    {
        var now = DateTimeOffset.UtcNow;
        return new[] { "phone", "browser" }.Any(kind =>
            Registered.TryGetValue(Key(upn, kind), out var presence)
            && now - presence.LastSeen < StaleAfter);
    }

    private static string Key(string upn, string deviceKind) => $"{upn}|{deviceKind}";
}
