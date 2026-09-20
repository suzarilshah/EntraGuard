using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EntraGuard.MediaService.Sinks;
using EntraGuard.Shared.Supportability;

namespace EntraGuard.MediaService.Auth;

/// <summary>
/// Proves at startup that this service can actually sign, and records a fault when it cannot.
/// </summary>
/// <remarks>
/// <para>
/// Publishing the certificate and using its key are two different Key Vault roles over two
/// different planes. A vault granting Reader but not Crypto User serves a perfectly good JWKS
/// and then fails every signature — so a healthy discovery document is not evidence that
/// anything can sign.
/// </para>
///
/// <para>
/// Without this, the first thing to exercise the signing path would be a real sign-in in
/// somebody's tenant, and the failure would arrive as AADSTS50012 on their screen rather than
/// as a fault on ours. That is the shape of every expensive failure this project has had: a
/// capability that looks configured, reports healthy, and has never once been executed.
/// </para>
///
/// <para>
/// There is a <c>/api/diagnostics/eam-selftest</c> endpoint that does the same thing on
/// demand. This exists as well as that one because a check somebody has to remember to run is
/// a check that gets run once, on the day it is written.
/// </para>
/// </remarks>
public sealed class EamSigningProbe(
    EamSigningKeys keys,
    FaultRecorder faults,
    ILogger<EamSigningProbe> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!keys.Configured)
        {
            // Not a fault. The external authentication method is opt-in, and a deployment
            // that has not enabled it is not broken.
            logger.LogInformation(
                "EAM signing probe skipped: EAM_KEYVAULT_URI is not set, so the external "
              + "authentication method is off.");
            return;
        }

        try
        {
            var published = await keys.AllAsync(cancellationToken);
            var signing = await keys.SigningKeyAsync(cancellationToken);

            if (signing is null)
            {
                faults.Record(Fault.EamSigningUnavailable(
                    "No enabled certificate version was found in Key Vault.",
                    "Run scripts/08-external-auth-method.sh to create the signing certificate."));
                return;
            }

            var digest = SHA256.HashData(
                Encoding.ASCII.GetBytes($"eam-signing-probe-{DateTimeOffset.UtcNow:O}"));
            var signature = await keys.SignAsync(signing, digest, cancellationToken);

            using var rsa = signing.Certificate.GetRSAPublicKey();
            var verified = rsa is not null && rsa.VerifyHash(
                digest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            if (!verified)
            {
                // Worse than being unable to sign: this signs, and Entra rejects every
                // token, because the key that signed is not the key we publish.
                faults.Record(Fault.EamSigningUnavailable(
                    $"Key Vault signed with {signing.KeyId}, but the signature does not verify "
                  + "against the certificate published in JWKS.",
                    "The published certificate and the signing key have diverged. Compare the "
                  + "kid in /.well-known/jwks against the certificate version in Key Vault."));
                return;
            }

            logger.LogInformation(
                "EAM signing probe OK: signed with {Kid} and verified against the published "
              + "certificate. {Count} version(s) published; signing key is {Position}.",
                signing.KeyId, published.Count,
                published.Count > 0 && published[^1].KeyId == signing.KeyId
                    ? "the oldest, as rotation requires"
                    : "NOT the oldest — rotation ordering is wrong");
        }
        catch (Exception ex)
        {
            faults.Record(Fault.EamSigningUnavailable(
                $"Signing failed at startup: {ex.Message}",
                "Most often the managed identity lacks Key Vault Crypto User on the vault. "
              + "Reading the certificate needs a different role from using its key, so JWKS "
              + "can look healthy while every signature fails."));

            logger.LogError(ex, "EAM signing probe failed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
