using System.Text.Json.Serialization;
using Azure.Communication.Identity;
using Azure.Data.Tables;
using EntraGuard.MediaService.Configuration;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Endpoints;

/// <summary>
/// Mints ACS identities and VoIP access tokens for the browser soft-phone.
///
/// This is the GA token-broker path. ACS also supports authenticating Entra ID users
/// directly against a Communication Services resource, but that is public preview and
/// needs a tenant-level access assignment, so the flag stays off and the server issues
/// tokens instead. The user still signs in with Entra first — the RP app calls this only
/// after authentication — so the identity binding is preserved either way.
///
/// The Entra object ID to ACS identity mapping is persisted, because the ACS identity has
/// to be stable: a user who re-authenticates must be reachable at the same endpoint their
/// soft-phone is already registered on, or the verification call rings nobody.
/// </summary>
public static class AcsIdentityEndpoint
{
    public sealed record TokenRequest
    {
        /// <summary>Entra ID object ID of the signed-in user, when the RP app knows it.</summary>
        [JsonPropertyName("objectId")]
        public string? ObjectId { get; init; }

        [JsonPropertyName("upn")]
        public string? Upn { get; init; }

        /// <summary>
        /// Which device is asking — "browser" or "phone".
        ///
        /// Load-bearing. ACS forks an incoming call to EVERY endpoint registered on an
        /// identity, and the first to accept wins. When the desktop and the handset shared
        /// one identity, the desktop's automatic accept() answered instantly and the phone
        /// never rang — while the UI correctly reported the call as answered. Giving each
        /// device its own identity is what makes "ring my phone" mean that specific device.
        /// </summary>
        [JsonPropertyName("deviceKind")]
        public string DeviceKind { get; init; } = "browser";
    }

    public static void MapAcsIdentity(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/acs/token", async (
            TokenRequest request,
            CommunicationIdentityClient identityClient,
            TableServiceClient tableService,
            IOptions<EntraGuardOptions> options,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("AcsIdentity");

            // Keyed on object ID where available, falling back to UPN. A UPN can be
            // reassigned after a rename; the object ID cannot, so it is preferred.
            var user = request.ObjectId ?? request.Upn;
            if (string.IsNullOrWhiteSpace(user))
            {
                return Results.BadRequest(new { error = "objectId or upn is required." });
            }

            // One identity per user PER DEVICE. See TokenRequest.DeviceKind.
            var deviceKind = string.IsNullOrWhiteSpace(request.DeviceKind) ? "browser" : request.DeviceKind;
            var key = $"{user}|{deviceKind}";

            string acsUserId;
            try
            {
                var table = tableService.GetTableClient("IdentityMap");
                await table.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

                var existing = await TryGetMappingAsync(table, key, cancellationToken);
                if (existing is not null)
                {
                    acsUserId = existing;
                }
                else
                {
                    var created = await identityClient.CreateUserAsync(cancellationToken);
                    acsUserId = created.Value.Id;

                    await table.UpsertEntityAsync(new TableEntity("entra", Sanitise(key))
                    {
                        ["AcsUserId"] = acsUserId,
                        ["Upn"] = request.Upn ?? string.Empty,
                        ["DeviceKind"] = deviceKind,
                        ["CreatedAt"] = DateTimeOffset.UtcNow,
                    }, TableUpdateMode.Replace, cancellationToken);

                    logger.LogInformation("Created ACS identity for {Upn} ({DeviceKind}).",
                        request.Upn ?? user, deviceKind);
                }
            }
            catch (Exception ex)
            {
                // Without persistence a returning user would get a fresh ACS identity each
                // sign-in and the verification call would ring an endpoint nobody is on.
                // Better to fail loudly than to hand back a token that cannot be called.
                logger.LogError(ex, "Could not resolve an ACS identity for {Key}.", key);
                return Results.Problem("Could not resolve an ACS identity for this user.", statusCode: 500);
            }

            // VoIP only. This token lets the browser place and receive calls and nothing
            // else — no chat, no PSTN.
            var token = await identityClient.GetTokenAsync(
                new Azure.Communication.CommunicationUserIdentifier(acsUserId),
                [CommunicationTokenScope.VoIP],
                cancellationToken);

            return Results.Ok(new
            {
                acsUserId,
                deviceKind,
                token = token.Value.Token,
                expiresOn = token.Value.ExpiresOn,
                endpoint = options.Value.AcsEndpoint,
            });
        })
        .WithName("AcsToken");
    }

    private static async Task<string?> TryGetMappingAsync(
        TableClient table, string key, CancellationToken cancellationToken)
    {
        try
        {
            var entity = await table.GetEntityAsync<TableEntity>("entra", Sanitise(key), cancellationToken: cancellationToken);
            return entity.Value.GetString("AcsUserId");
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    /// <summary>
    /// Table Storage rejects these characters in a row key, and a UPN can legitimately
    /// contain them.
    /// </summary>
    private static string Sanitise(string key) =>
        key.Replace('/', '_').Replace('\\', '_').Replace('#', '_').Replace('?', '_');
}
