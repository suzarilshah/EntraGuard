using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using System.Text.Json;
using Azure.Security.KeyVault.Certificates;
using Azure.Security.KeyVault.Keys.Cryptography;

namespace EntraGuard.MediaService.Auth;

/// <summary>One published signing key: the certificate to advertise and the key that signs.</summary>
/// <param name="KeyId">The JWK <c>kid</c>, and the Key Vault key version it came from.</param>
/// <param name="Certificate">The X.509 certificate, needed for the mandatory <c>x5c</c>.</param>
/// <param name="KeyUri">Key Vault key version URI, used to sign without ever holding the key.</param>
/// <param name="CreatedOn">When the version was created. Decides which one signs.</param>
public sealed record EamKey(
    string KeyId,
    X509Certificate2 Certificate,
    Uri KeyUri,
    DateTimeOffset CreatedOn);

/// <summary>
/// The RS256 keys EntraGuard signs External Authentication Method responses with.
///
/// <para>
/// The private key never leaves Key Vault. Signing is a <c>CryptographyClient</c> call
/// authorised by the container's managed identity, which keeps the property the README
/// claims — managed identity throughout, no key material in configuration.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>Rollover is the dangerous operation, so the rule is structural rather than procedural:
/// publish every enabled version, and sign with the OLDEST one.</b>
/// </para>
///
/// <para>
/// Entra caches a provider's JWKS for 24 hours and tells providers to assume up to two days.
/// Its guidance is: publish the new certificate, keep signing with the existing one until
/// the cache has turned over, and only then switch. Done by hand that is three steps with a
/// two-day wait in the middle, and getting the order wrong fails <em>every sign-in in the
/// tenant</em> — not this feature, the tenant's access to everything behind Conditional
/// Access.
/// </para>
///
/// <para>
/// Signing with the oldest published key makes that ordering impossible to get wrong. Adding
/// a version publishes it without promoting it, so the new key is in Entra's cache long
/// before anything signs with it; removing the old version is what promotes its successor,
/// by which time the successor has been advertised for as long as the old key survived.
/// The operator's job becomes "add a version, wait, disable the old one" with no step that
/// can be performed too early.
/// </para>
///
/// <para>
/// Newest-signs would have been the obvious implementation and is the broken one: a new
/// version starts signing the moment it is created, against a cache that has never seen it.
/// </para>
/// </remarks>
public sealed class EamSigningKeys(
    CertificateClient certificates,
    TokenCredential credential,
    IConfiguration config,
    TimeProvider time,
    ILogger<EamSigningKeys> logger)
{
    /// <summary>Key Vault is not on the request path; a cached set is refreshed periodically.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<EamKey> _keys = [];
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    /// <summary>The certificate name in Key Vault holding the signing versions.</summary>
    public string CertificateName => config["EAM_SIGNING_CERT"] ?? "eam-signing";

    /// <summary>Is EAM configured at all? Absent Key Vault means the endpoints stay off.</summary>
    public bool Configured => !string.IsNullOrWhiteSpace(config["EAM_KEYVAULT_URI"]);

    /// <summary>Every enabled version, newest first. All are published; the last one signs.</summary>
    public async Task<IReadOnlyList<EamKey>> AllAsync(CancellationToken cancellationToken)
    {
        if (time.GetUtcNow() - _loadedAt < CacheLifetime && _keys.Count > 0)
        {
            return _keys;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (time.GetUtcNow() - _loadedAt < CacheLifetime && _keys.Count > 0)
            {
                return _keys;
            }

            var loaded = new List<EamKey>();

            await foreach (var version in certificates
                .GetPropertiesOfCertificateVersionsAsync(CertificateName, cancellationToken))
            {
                if (version.Enabled is false || version.Version is null)
                {
                    continue;
                }

                var certificate = await certificates.GetCertificateVersionAsync(
                    CertificateName, version.Version, cancellationToken);

                if (certificate.Value.Cer is null || certificate.Value.KeyId is null)
                {
                    continue;
                }

                loaded.Add(new EamKey(
                    version.Version,
                    X509CertificateLoader.LoadCertificate(certificate.Value.Cer),
                    certificate.Value.KeyId,
                    version.CreatedOn ?? DateTimeOffset.MinValue));
            }

            _keys = [.. loaded.OrderByDescending(k => k.CreatedOn)];
            _loadedAt = time.GetUtcNow();

            logger.LogInformation(
                "EAM signing: {Count} enabled version(s) of {Name}; signing with {Kid} (oldest published).",
                _keys.Count, CertificateName, _keys.Count > 0 ? _keys[^1].KeyId : "none");

            return _keys;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The key that signs right now: the oldest still published. See the remarks above.</summary>
    public async Task<EamKey?> SigningKeyAsync(CancellationToken cancellationToken)
    {
        var keys = await AllAsync(cancellationToken);
        return keys.Count == 0 ? null : keys[^1];
    }

    /// <summary>Sign with Key Vault. The private key is never held in this process.</summary>
    public async Task<byte[]> SignAsync(EamKey key, byte[] digest, CancellationToken cancellationToken)
    {
        var crypto = new CryptographyClient(key.KeyUri, credential);
        var result = await crypto.SignAsync(SignatureAlgorithm.RS256, digest, cancellationToken);
        return result.Signature;
    }

    /// <summary>
    /// The JWKS document, with every published key.
    /// </summary>
    /// <remarks>
    /// <c>x5c</c> is mandatory here and is the part most easily missed: Microsoft's reference
    /// states the parameter "must be present to provide X.509 representations of provided
    /// keys". A JWKS carrying only <c>n</c> and <c>e</c> parses fine and is rejected.
    /// </remarks>
    public async Task<string> JwksAsync(CancellationToken cancellationToken)
    {
        var keys = await AllAsync(cancellationToken);
        var entries = new List<object>();

        foreach (var key in keys)
        {
            using var rsa = key.Certificate.GetRSAPublicKey();
            if (rsa is null)
            {
                continue;
            }

            var parameters = rsa.ExportParameters(includePrivateParameters: false);

            entries.Add(new Dictionary<string, object>
            {
                ["kty"] = "RSA",
                ["use"] = "sig",
                ["alg"] = "RS256",
                ["kid"] = key.KeyId,
                ["x5t"] = Base64Url(key.Certificate.GetCertHash()),
                ["n"] = Base64Url(parameters.Modulus!),
                ["e"] = Base64Url(parameters.Exponent!),
                ["x5c"] = new[] { Convert.ToBase64String(key.Certificate.RawData) },
            });
        }

        return JsonSerializer.Serialize(new { keys = entries });
    }

    internal static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
