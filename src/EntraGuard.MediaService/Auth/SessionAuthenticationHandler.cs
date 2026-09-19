using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Auth;

public sealed class SessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    RpSessionService sessions) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "RpSession";
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Headers[RpSessionService.Header].ToString();
        if (string.IsNullOrEmpty(token)) return AuthenticateResult.NoResult();
        var session = await sessions.ValidateAsync(token, Context.RequestAborted);
        return session is null ? AuthenticateResult.Fail("Session expired or revoked.")
            : AuthenticateResult.Success(new AuthenticationTicket(RpSessionService.Principal(session), SchemeName));
    }
}
