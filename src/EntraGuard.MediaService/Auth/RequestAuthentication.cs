using Microsoft.AspNetCore.Authentication;

namespace EntraGuard.MediaService.Auth;

public static class RequestAuthentication
{
    public static IServiceCollection AddRequestAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(o => { o.DefaultAuthenticateScheme = "EntraGuardRequest"; o.DefaultChallengeScheme = "EntraGuardRequest"; })
            .AddPolicyScheme("EntraGuardRequest", "Bearer or revocable session", o => o.ForwardDefaultSelector = context =>
                context.Request.Headers.ContainsKey(RpSessionService.Header) ? SessionAuthenticationHandler.SchemeName : VoiceProfileAuth.Scheme)
            .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationHandler.SchemeName, _ => { });
        return services;
    }
}
