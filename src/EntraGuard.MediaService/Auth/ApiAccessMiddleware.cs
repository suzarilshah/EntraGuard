using System.Security.Claims;

namespace EntraGuard.MediaService.Auth;

public static class OperatorAccess
{
    public static bool Allowed(ClaimsPrincipal principal, IConfiguration config)
    {
        var caller = principal.Caller();
        if (caller is null || !Guid.TryParse(config["AZURE_TENANT_ID"], out var home)
            || !Guid.TryParse(caller.TenantId, out var tenant) || home != tenant) return false;
        var allowed = (config["ENTRAGUARD_OPERATOR_IDS"] ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return principal.HasClaim("roles", "EntraGuard.Operator") || allowed.Any(id =>
            Guid.TryParse(id, out var value) && Guid.TryParse(caller.ObjectId, out var subject) && value == subject);
    }
}

/// <summary>Fail-closed default boundary, including endpoints added without authorization metadata.</summary>
public sealed class ApiAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TransportProtection transport, IConfiguration config)
    {
        var path = context.Request.Path.Value ?? "";
        if (path is "/health/live" or "/health/ready") { await next(context); return; }
        if (path == "/api/events/incoming-call")
        {
            if (!transport.ValidateEventGrid(context.Request.Query["code"].ToString())) { context.Response.StatusCode = 401; return; }
            await next(context); return;
        }
        if (path.StartsWith("/api/callbacks/", StringComparison.Ordinal)
            || path.StartsWith("/api/verify/callbacks/", StringComparison.Ordinal)
            || path.StartsWith("/api/voice-profile/callbacks/", StringComparison.Ordinal)
            || path.StartsWith("/ws/media/", StringComparison.Ordinal))
        {
            if (!transport.Validate(path, context.Request.Query["expires"].ToString(), context.Request.Query["signature"].ToString()))
            { context.Response.StatusCode = 401; return; }
            await next(context); return;
        }
        if (context.User.Identity?.IsAuthenticated != true || context.User.Caller() is not { } caller
            || !Guid.TryParse(caller.TenantId, out _) || !Guid.TryParse(caller.ObjectId, out _))
        { context.Response.StatusCode = 401; return; }

        if (!context.User.HasClaim(c => c.Type == RpSessionService.Claim)
            && !((context.User.FindFirst("scp")?.Value ?? "").Split(' ').Contains("VoiceProfile.Manage", StringComparer.Ordinal))
            && !OperatorAccess.Allowed(context.User, config))
        { context.Response.StatusCode = 403; return; }

        var operatorOnly = path.StartsWith("/api/sessions", StringComparison.Ordinal)
            || path.StartsWith("/api/diagnostics", StringComparison.Ordinal)
            || path.StartsWith("/api/simulate", StringComparison.Ordinal)
            || path.StartsWith("/hubs/", StringComparison.Ordinal)
            || path is "/api/build" or "/api/verify/simulate" or "/api/operator/session"
            or "/api/voice-profile/selftest" or "/api/voice-profile/calibrate"
            or "/api/voice-profile/rehearse" or "/api/voice-profile/acs-smoke";
        if (operatorOnly && !OperatorAccess.Allowed(context.User, config)) { context.Response.StatusCode = 403; return; }

        // Route identity is checked before any Graph/knowledge lookup. Operator access does not bypass ownership.
        if (context.Request.RouteValues.TryGetValue("tenantId", out var tenant)
            && context.Request.RouteValues.TryGetValue("objectId", out var subject)
            && !Owner.From(context.User).Owns(tenant?.ToString(), subject?.ToString()))
        { context.Response.StatusCode = 404; return; }
        await next(context);
    }
}
