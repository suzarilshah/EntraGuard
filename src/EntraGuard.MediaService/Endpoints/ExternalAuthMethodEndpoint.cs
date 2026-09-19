using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Sessions;
using EntraGuard.Shared.Verification;
using EntraGuard.MediaService.Configuration;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Endpoints;

/// <summary>One in-flight sign-in that Entra handed to EntraGuard.</summary>
internal sealed record EamFlow(
    string Id,
    EamRequest Request,
    string VerificationId,
    DateTimeOffset StartedAt);

/// <summary>
/// EntraGuard as an Entra ID External Authentication Method.
///
/// <para>
/// This is what makes EntraGuard a second factor for <em>any</em> application rather than
/// only the bundled Treasury demo. A tenant adds the method and points one Conditional
/// Access policy at it; from then on Microsoft 365, the Azure portal, gallery SaaS and the
/// tenant's own applications all get the verification call, with no change to any of them.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// Three public endpoints, per Microsoft's provider contract: OIDC discovery, a JWKS
/// document, and an authorization endpoint that Entra reaches by <c>form_post</c> using the
/// implicit flow. Entra identifies the user with an intentionally EXPIRED, signed
/// <c>id_token_hint</c>; the signature is the authentication, the expiry stops the hint
/// being replayed as a credential.
/// </para>
///
/// <para>
/// These endpoints are exempt from <c>ApiAccessMiddleware</c>'s signed-in-user requirement
/// and authenticate themselves instead, the same way the Event Grid and ACS callbacks do.
/// Entra is not one of our users and has no session; what it presents is a token signed by
/// Microsoft, and nothing here proceeds until that has been validated.
/// </para>
/// </remarks>
public static class ExternalAuthMethodEndpoint
{
    /// <summary>Paths that authenticate by Microsoft's signature rather than by a session.</summary>
    public static readonly string[] AnonymousPaths =
    [
        "/.well-known/openid-configuration",
        "/.well-known/jwks",
        "/api/eam/authorize",
        "/api/eam/status",
    ];

    /// <summary>
    /// How long a sign-in may take before EntraGuard gives up on its own terms.
    /// </summary>
    /// <remarks>
    /// Entra abandons the attempt roughly five minutes after redirecting. Measured across 23
    /// real calls here the median is 108 seconds and the worst 156, so the usual call fits
    /// comfortably — but a slow answer plus a retry does not have unlimited room.
    ///
    /// Stopping at four minutes means the user gets a stated outcome that Entra surfaces,
    /// instead of a browser sitting on a page whose sign-in has already been discarded at the
    /// other end. A refusal somebody can act on beats a hang every time.
    /// </remarks>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(240);

    private static readonly ConcurrentDictionary<string, EamFlow> Flows = new();

    public static void MapExternalAuthMethod(this IEndpointRouteBuilder app)
    {
        // ── Discovery ───────────────────────────────────────────────────────
        //
        // The issuer must match, character for character, the value configured in the tenant
        // AND the `iss` of every token issued here. Microsoft's reference lists the ways this
        // goes wrong — an explicit :443, a trailing slash, a query string — and each one
        // fails every sign-in with a signature error that names none of them.
        app.MapGet("/.well-known/openid-configuration", (
            HttpContext context,
            EamSigningKeys keys,
            IOptions<EntraGuardOptions> options) =>
        {
            if (!keys.Configured)
            {
                return Results.NotFound();
            }

            var issuer = Issuer(options.Value);

            return Results.Json(new Dictionary<string, object>
            {
                ["issuer"] = issuer,
                ["authorization_endpoint"] = $"{issuer}/api/eam/authorize",
                ["jwks_uri"] = $"{issuer}/.well-known/jwks",
                ["scopes_supported"] = new[] { "openid" },
                ["response_types_supported"] = new[] { "id_token" },
                ["response_modes_supported"] = new[] { "form_post" },
                ["grant_types_supported"] = new[] { "implicit" },
                ["subject_types_supported"] = new[] { "public" },
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                ["claim_types_supported"] = new[] { "normal" },
                ["claims_supported"] = new[] { "sub", "iss", "aud", "exp", "iat", "nonce", "acr", "amr" },
            });
        }).ExcludeFromDescription();

        // ── Public keys ─────────────────────────────────────────────────────
        app.MapGet("/.well-known/jwks", async (
            EamSigningKeys keys,
            CancellationToken cancellationToken) =>
        {
            if (!keys.Configured)
            {
                return Results.NotFound();
            }

            return Results.Content(await keys.JwksAsync(cancellationToken), "application/json");
        }).ExcludeFromDescription();

        // ── Authorization ───────────────────────────────────────────────────
        app.MapPost("/api/eam/authorize", async (
            HttpContext context,
            EamHintValidator hints,
            EamSigningKeys keys,
            VerificationLauncher launcher,
            IOptions<EntraGuardOptions> options,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("ExternalAuthMethod");

            if (!keys.Configured)
            {
                return Results.NotFound();
            }

            var form = await context.Request.ReadFormAsync(cancellationToken);
            var redirectUri = form["redirect_uri"].ToString();

            // Checked before anything else and never echoed back on failure. redirect_uri
            // arrives in the request body, and posting a freshly signed identity assertion
            // to whatever address asked for it is how a provider becomes an oracle that
            // authenticates arbitrary users for whoever is collecting.
            if (!EamRequest.IsPublishedRedirect(redirectUri))
            {
                logger.LogWarning(
                    "EAM: refused a request naming an unpublished redirect_uri. "
                  + "Only Microsoft's own federation endpoints are valid destinations.");
                return Results.BadRequest(new { error = "invalid_request" });
            }

            var hint = await hints.ValidateAsync(form["id_token_hint"].ToString(), cancellationToken);
            if (hint is null)
            {
                logger.LogWarning("EAM: the id_token_hint did not validate; refusing the request.");
                return Results.BadRequest(new { error = "invalid_request" });
            }

            var (acr, amr) = EamClaimsRequest.Parse(form["claims"].ToString());

            var request = new EamRequest(
                ClientId: form["client_id"].ToString(),
                RedirectUri: redirectUri,
                Nonce: form["nonce"].ToString() is { Length: > 0 } n ? n : null,
                State: form["state"].ToString() is { Length: > 0 } s ? s : null,
                Subject: hint.Subject,
                ObjectId: hint.ObjectId,
                TenantId: hint.TenantId,
                PreferredUsername: hint.PreferredUsername,
                RequestedAcr: acr,
                RequestedAmr: amr);

            // Refuse BEFORE ringing anybody. If a telephone call cannot satisfy what this
            // sign-in asks for — an inherence factor, because the first factor was already
            // possession — then placing the call would interrupt somebody for a verification
            // whose result could never be accepted.
            if (EamClaims.SatisfiableAcr(acr, FactorType.Possession) is null
                || EamClaims.ChooseAmr(amr) is null)
            {
                logger.LogWarning(
                    "EAM: declining without calling — this sign-in needs a factor a phone call "
                  + "cannot supply (acr [{Acr}]).", string.Join(", ", acr));
                return Post(redirectUri, error: "access_denied", state: request.State);
            }

            try
            {
                var verification = await launcher.PlaceAsync(
                    new CallTarget(
                        Upn: hint.PreferredUsername ?? hint.ObjectId,
                        ObjectId: hint.ObjectId,
                        TenantId: hint.TenantId,
                        TeamsUserId: hint.ObjectId,
                        AcsUserId: null,
                        EndpointKind: "teams",

                        // Entra does not tell a provider which application the user was
                        // reaching for, so naming one would be a guess spoken aloud to
                        // somebody mid-sign-in. This is what we actually know.
                        ApplicationName: "your organisation's sign-in"),
                    cancellationToken);

                var flow = new EamFlow(
                    Id: Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                    Request: request,
                    VerificationId: verification.VerificationId,
                    StartedAt: DateTimeOffset.UtcNow);

                Flows[flow.Id] = flow;

                logger.LogInformation(
                    "EAM: verification {Verification} started for {Subject} in tenant {Tenant}.",
                    verification.VerificationId, hint.ObjectId, hint.TenantId);

                return Results.Content(WaitingPage(flow.Id, verification.MatchCode), "text/html");
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError(ex, "EAM: could not place the verification call.");
                return Post(redirectUri, error: "temporarily_unavailable", state: request.State);
            }
        }).ExcludeFromDescription();

        // ── Outcome, polled by the waiting page ─────────────────────────────
        app.MapGet("/api/eam/status/{flowId}", async (
            string flowId,
            VerificationRegistry registry,
            EamTokenIssuer issuer,
            IOptions<EntraGuardOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (!Flows.TryGetValue(flowId, out var flow))
            {
                return Results.NotFound();
            }

            var verification = registry.Get(flow.VerificationId);

            // Past our own deadline, or the record has already been evicted. Either way the
            // honest answer is that this did not complete, delivered as an error Entra can
            // surface rather than as a page that keeps polling something that is gone.
            if (DateTimeOffset.UtcNow - flow.StartedAt > Deadline || verification is null)
            {
                Flows.TryRemove(flowId, out _);
                return Results.Json(new
                {
                    done = true,
                    redirectUri = flow.Request.RedirectUri,
                    state = flow.Request.State,
                    error = "temporarily_unavailable",
                });
            }

            if (!verification.IsComplete)
            {
                return Results.Json(new { done = false });
            }

            Flows.TryRemove(flowId, out _);

            if (!verification.GrantsAccess)
            {
                return Results.Json(new
                {
                    done = true,
                    redirectUri = flow.Request.RedirectUri,
                    state = flow.Request.State,
                    error = "access_denied",
                });
            }

            var token = await issuer.IssueAsync(flow.Request, Issuer(options.Value), cancellationToken);

            return Results.Json(token is null
                ? new
                {
                    done = true,
                    redirectUri = flow.Request.RedirectUri,
                    state = flow.Request.State,
                    error = "access_denied",
                }
                : (object)new
                {
                    done = true,
                    redirectUri = flow.Request.RedirectUri,
                    state = flow.Request.State,
                    idToken = token,
                });
        }).ExcludeFromDescription();
    }

    /// <summary>
    /// The issuer, derived from the public base URL and nothing else.
    /// </summary>
    /// <remarks>
    /// Trimmed of a trailing slash on purpose: Microsoft's own examples list
    /// <c>https://example.com/</c> against a discovery URL of <c>https://example.com</c> as
    /// an INVALID pair. A single character here fails every sign-in in the tenant with a
    /// signature error that mentions neither the slash nor the issuer.
    /// </remarks>
    private static string Issuer(EntraGuardOptions options) =>
        options.PublicBaseUrl.TrimEnd('/');

    /// <summary>An auto-submitting form POST back to Entra, which is how this protocol replies.</summary>
    private static IResult Post(string redirectUri, string error, string? state)
    {
        var fields = $"""<input type="hidden" name="error" value="{WebUtility.HtmlEncode(error)}">""";

        if (!string.IsNullOrEmpty(state))
        {
            fields += $"""<input type="hidden" name="state" value="{WebUtility.HtmlEncode(state)}">""";
        }

        return Results.Content($"""
            <!doctype html><html><head><meta charset="utf-8"><title>Returning you to sign-in</title></head>
            <body onload="document.forms[0].submit()">
            <form method="post" action="{WebUtility.HtmlEncode(redirectUri)}">{fields}
            <noscript><button type="submit">Continue</button></noscript>
            </form></body></html>
            """, "text/html");
    }

    /// <summary>
    /// What the user looks at while their phone rings.
    /// </summary>
    /// <remarks>
    /// It shows the match code, because the code is the whole point: the caller has to read
    /// it from a screen an attacker on the phone cannot see, and a page that hides it turns
    /// the number match into an ordinary one-time code read aloud.
    ///
    /// Plain and self-contained — no framework, no external fetch. This renders inside a
    /// sign-in the user cannot complete until it finishes, so it must not depend on anything
    /// that can be slow or blocked.
    /// </remarks>
    private static string WaitingPage(string flowId, string matchCode) => $$"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Verifying your identity</title>
        <style>
          :root { color-scheme: light dark; }
          body { font-family: -apple-system, "Segoe UI", system-ui, sans-serif; margin: 0;
                 min-height: 100vh; display: grid; place-items: center; background: #faf9f8; color: #201f1e; }
          @media (prefers-color-scheme: dark) { body { background: #1b1a19; color: #f3f2f1; } }
          .card { max-width: 26rem; padding: 2rem 1.5rem; text-align: center; }
          h1 { font-size: 1.25rem; font-weight: 600; margin: 0 0 .5rem; }
          p { margin: .5rem 0; line-height: 1.5; opacity: .85; }
          .code { font-size: 3.5rem; font-weight: 700; letter-spacing: .1em; margin: 1.5rem 0; }
          .quiet { font-size: .8125rem; opacity: .6; }
        </style></head>
        <body><div class="card">
          <h1>Answer the call to continue</h1>
          <p>EntraGuard is calling you now. Enter this number when asked.</p>
          <div class="code">{{matchCode}}</div>
          <p class="quiet">Nobody should ask you to read this number aloud, or offer to help you answer.</p>
          <form id="reply" method="post"></form>
        </div>
        <script>
          const flow = {{flowId}};
          async function poll() {
            try {
              const r = await fetch('/api/eam/status/' + flow, { cache: 'no-store' });
              if (!r.ok) { return setTimeout(poll, 2000); }
              const d = await r.json();
              if (!d.done) { return setTimeout(poll, 1500); }
              const f = document.getElementById('reply');
              f.action = d.redirectUri;
              for (const [k, v] of Object.entries({ id_token: d.idToken, error: d.error, state: d.state })) {
                if (!v) continue;
                const i = document.createElement('input');
                i.type = 'hidden'; i.name = k; i.value = v;
                f.appendChild(i);
              }
              f.submit();
            } catch { setTimeout(poll, 2000); }
          }
          poll();
        </script></body></html>
        """.Replace("{{flowId}}", System.Text.Json.JsonSerializer.Serialize(flowId));
}
