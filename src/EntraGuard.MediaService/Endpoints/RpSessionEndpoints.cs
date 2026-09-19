using EntraGuard.MediaService.Auth;

namespace EntraGuard.MediaService.Endpoints;

public static class RpSessionEndpoints
{
    public static void MapRpSessions(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/rp/session", async (HttpContext context, RpSessionService sessions, CancellationToken ct) =>
        {
            var issued = await sessions.CreateAsync(context.User, ct);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { token = issued.Token, expiresAt = issued.Session.ExpiresAt });
        }).RequireAuthorization(VoiceProfileAuth.Policy);

        app.MapGet("/api/rp/session", async (HttpContext context, RpSessionService sessions, TimeProvider time, CancellationToken ct) =>
        {
            var session = await sessions.GetAsync(context.User, ct);
            return session is null ? Results.Unauthorized() : Results.Ok(new
            {
                session.Owner, session.ExpiresAt, session.VerificationId,
                verified = session.VerifiedUntil > time.GetUtcNow(), session.VerifiedUntil,
                isOperator = session.IsOperator,
            });
        });
        app.MapDelete("/api/rp/session", async (HttpContext context, RpSessionService sessions, CancellationToken ct) =>
        { await sessions.RevokeAsync(context.User, ct); return Results.NoContent(); });
        app.MapGet("/api/operator/session", () => Results.Ok(new { authorized = true }));
    }
}
