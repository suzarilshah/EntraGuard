using System.Text.Json.Serialization;
using Azure.Communication;
using Azure.Communication.Identity;
using EntraGuard.MediaService.Auth;
using EntraGuard.MediaService.Sessions;

namespace EntraGuard.MediaService.Endpoints;

public static class AcsIdentityEndpoint
{
    public sealed record TokenRequest([property: JsonPropertyName("deviceKind")] string DeviceKind = "browser");
    public static void MapAcsIdentity(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/acs/token", async (TokenRequest request, HttpContext context,
            DeviceService devices, CommunicationIdentityClient identities, CancellationToken ct) =>
        {
            var sessionId = context.User.FindFirst(RpSessionService.Claim)?.Value;
            if (sessionId is null) return Results.Unauthorized();
            if (!DeviceService.ValidKind(request.DeviceKind)) return Results.BadRequest(new { error = "Choose browser or phone." });
            var device = await devices.RegisterAsync(Owner.From(context.User), request.DeviceKind, sessionId,
                async () => (await identities.CreateUserAsync(ct)).Value.Id, ct);
            var token = await identities.GetTokenAsync(new CommunicationUserIdentifier(device.AcsUserId), [CommunicationTokenScope.VoIP], ct);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { acsUserId = device.AcsUserId, deviceKind = device.Kind, token = token.Value.Token, expiresOn = token.Value.ExpiresOn });
        });
    }
}
