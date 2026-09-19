using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Persistence;
using EntraGuard.MediaService.Sessions;
using Azure.Communication;
using Azure.Communication.Identity;

namespace EntraGuard.MediaService.Endpoints;

public static class AccountEndpoints
{
    public sealed record GrantRequest(string VerificationId);
    public static void MapAccount(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/account/preferences", async (HttpContext context, PreferenceService preferences, CancellationToken ct) =>
            Results.Ok(await preferences.GetAsync(Owner.From(context.User), ct)));
        app.MapPut("/api/account/preferences", async (UserPreferences input, HttpContext context, PreferenceService preferences, CancellationToken ct) =>
        {
            var error = await preferences.SaveAsync(Owner.From(context.User), input, ct);
            return error is null ? Results.Ok(await preferences.GetAsync(Owner.From(context.User), ct)) : Results.Conflict(new { error });
        });
        app.MapGet("/api/account/devices", async (HttpContext context, DeviceService devices, CancellationToken ct) =>
            Results.Ok((await devices.ListAsync(Owner.From(context.User), ct)).Select(d => new
            { d.Kind, d.Name, d.RegisteredAt, d.LastSeen, d.Revoked, reachable = devices.Reachable(d),
                currentSession = d.SessionId == context.User.FindFirst(RpSessionService.Claim)?.Value })));
        app.MapDelete("/api/account/devices/{kind}", async (string kind, HttpContext context, DeviceService devices, CommunicationIdentityClient identities, CancellationToken ct) =>
        {
            var owner = Owner.From(context.User); var device = await devices.GetAsync(owner, kind, ct);
            if (device is null) return Results.NotFound();
            if (!await devices.RevokeAsync(owner, kind, ct)) return Results.Conflict(new { error = "Device changed. Retry revocation." });
            // Both are required. A downstream error is returned, never mislabeled as full success.
            await identities.RevokeTokensAsync(new CommunicationUserIdentifier(device.AcsUserId), ct);
            return Results.Ok(new { revoked = true, currentSession = device.SessionId == context.User.FindFirst(RpSessionService.Claim)?.Value });
        });
        app.MapGet("/api/account/notifications", async (string? cursor, HttpContext context, PreferenceService preferences, CancellationToken ct) =>
        {
            var page = await preferences.InboxAsync(Owner.From(context.User), cursor, ct);
            return Results.Ok(new { items = page.Items.Select(d => d.Value<InboxNotification>()), cursor = page.ContinuationToken, channel = "in-app" });
        });
        app.MapPost("/api/account/notifications/{id}/read", async (string id, HttpContext context, PreferenceService preferences, CancellationToken ct) =>
            await preferences.MarkReadAsync(Owner.From(context.User), id, ct) ? Results.NoContent() : Results.NotFound());
        app.MapGet("/api/account/payments", async (HttpContext context, PaymentService payments, GrantService grants, CancellationToken ct) =>
        {
            var sid = context.User.FindFirst(RpSessionService.Claim)?.Value;
            var owner = Owner.From(context.User);
            if (sid is null || !(await grants.CurrentAsync(owner, sid, ct)).Granted) return Results.StatusCode(403);
            return Results.Ok(new { items = (await payments.ListAsync(owner, ct)).Select(p => p.Describe()), demoOnly = true,
                enabled = payments.Enabled, canApprove = context.User.HasClaim("roles", "EntraGuard.PaymentApprover") });
        });
        app.MapPost("/api/account/payments/{id}/approve", async (string id, GrantRequest request, HttpContext context, PaymentService payments, CancellationToken ct) =>
        {
            var sid = context.User.FindFirst(RpSessionService.Claim)?.Value;
            if (sid is null) return Results.Unauthorized();
            var outcome = await payments.ApproveAsync(Owner.From(context.User), sid, id, request.VerificationId,
                context.Request.Headers["Idempotency-Key"].ToString(), ct);
            return Results.Json(outcome, statusCode: outcome.Approved ? 200 : 409);
        });
        app.MapGet("/api/account/readiness", async (HttpContext context, ReadinessService readiness, CancellationToken ct) =>
            Results.Ok(await readiness.GetAsync(Owner.From(context.User), ct)));
        app.MapGet("/api/account/policy", async (HttpContext context, TenantPolicyService policies, IConfiguration config, CancellationToken ct) =>
            Results.Ok(new { policy = await policies.GetAsync(Owner.From(context.User).TenantId, ct),
                canEdit = context.User.HasClaim("roles", "EntraGuard.TenantAdmin") || OperatorAccess.Allowed(context.User, config) }));
        app.MapPut("/api/account/policy", async (TenantPolicy policy, HttpContext context, TenantPolicyService policies, IConfiguration config, CancellationToken ct) =>
        {
            if (!context.User.HasClaim("roles", "EntraGuard.TenantAdmin") && !OperatorAccess.Allowed(context.User, config)) return Results.StatusCode(403);
            if (policy.Validate() is { } error) return Results.BadRequest(new { error });
            return await policies.UpdateAsync(Owner.From(context.User), policy, policy.Version, ct)
                ? Results.Ok(new { saved = true, version = policy.Version + 1 }) : Results.Conflict(new { error = "Policy changed. Reload before saving." });
        });
        app.MapPost("/api/account/stepup", async (GrantRequest request, HttpContext context, StepUpService stepup, CancellationToken ct) =>
        {
            var sid = context.User.FindFirst(RpSessionService.Claim)?.Value;
            if (sid is null) return Results.Unauthorized();
            var error = await stepup.ConfirmAsync(Owner.From(context.User), sid, request.VerificationId, context.Request.Headers["X-Id-Token"], ct);
            return error is null ? Results.Ok(new { confirmed = true }) : Results.Json(new { error }, statusCode: 403);
        });
        app.MapGet("/api/account/history", async (HttpContext context, VerificationLedger ledger, int? limit, string? cursor, CancellationToken ct) =>
        {
            var page = await ledger.HistoryAsync(Owner.From(context.User), limit ?? 20, cursor, ct);
            return Results.Ok(new { items = page.Items.Select(d => d.Value<VerificationReceipt>().Describe()), cursor = page.ContinuationToken });
        });
        app.MapGet("/api/account/history/{id}", async (string id, HttpContext context, VerificationLedger ledger, CancellationToken ct) =>
        {
            var receipt = await ledger.GetAsync(Owner.From(context.User), id, ct);
            return receipt is null ? Results.NotFound() : Results.Ok(receipt.Describe());
        });
        app.MapPost("/api/account/grant", async (GrantRequest request, HttpContext context, GrantService grants, CancellationToken ct) =>
        {
            var session = context.User.FindFirst(RpSessionService.Claim)?.Value;
            if (session is null) return Results.Unauthorized();
            var result = await grants.GrantAsync(Owner.From(context.User), session, request.VerificationId, ct);
            return Results.Json(new { granted = result.Granted, error = result.Error, verification = result.Verification?.Describe() }, statusCode: result.Granted ? 200 : 403);
        });
        app.MapGet("/api/account/grant", async (HttpContext context, GrantService grants, CancellationToken ct) =>
        {
            var session = context.User.FindFirst(RpSessionService.Claim)?.Value;
            if (session is null) return Results.Unauthorized();
            var result = await grants.CurrentAsync(Owner.From(context.User), session, ct);
            return Results.Ok(new { granted = result.Granted, verification = result.Verification?.Describe() });
        });
    }
}
