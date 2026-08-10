using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using EntraGuard.MediaService.Configuration;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Sinks;

/// <param name="Template">The enrolled speaker embedding, unit length.</param>
/// <param name="ConsentVersion">Which consent text the user agreed to.</param>
/// <param name="ConsentAt">When they agreed, UTC.</param>
/// <param name="EnrolledAt">When the template was built.</param>
/// <param name="SelfConsistency">
/// Lowest pairwise similarity between the enrolment utterances. A template built from
/// recordings that disagree with each other is a template that will not match its owner.
/// </param>
public sealed record Voiceprint(
    double[] Template,
    string ConsentVersion,
    DateTimeOffset ConsentAt,
    DateTimeOffset EnrolledAt,
    double SelfConsistency);

/// <summary>
/// Stores enrolled voice templates, encrypted.
///
/// A speaker embedding is biometric data. It is not a password that can be changed after a
/// breach — the user's voice is the same voice forever — so it gets treated more carefully
/// than the knowledge answers next to it in the same account.
///
/// Two layers. The storage account already refuses shared-key access, so the data plane is
/// identity-only and nothing without a managed identity can read the table at all. On top
/// of that the template itself is AES-GCM encrypted, so a table dump, an over-broad RBAC
/// grant, or a support engineer with Reader sees ciphertext rather than a biometric.
///
/// Raw audio never reaches this class. Only the derived vector does, and an embedding
/// cannot be played back as speech.
/// </summary>
public sealed class VoiceprintStore
{
    private const string TableName = "EntraGuardVoiceprints";

    private readonly EntraGuardOptions _options;
    private readonly ILogger<VoiceprintStore> _logger;
    private readonly Lazy<TableClient?> _table;
    private readonly Lazy<byte[]?> _key;

    public VoiceprintStore(
        IOptions<EntraGuardOptions> options,
        TokenCredential credential,
        ILogger<VoiceprintStore> logger)
    {
        _options = options.Value;
        _logger = logger;

        _table = new Lazy<TableClient?>(() =>
        {
            if (string.IsNullOrEmpty(_options.StorageAccountName))
            {
                return null;
            }

            var client = new TableClient(
                new Uri($"https://{_options.StorageAccountName}.table.core.windows.net"),
                TableName,
                credential);

            client.CreateIfNotExists();
            return client;
        });

        _key = new Lazy<byte[]?>(() =>
        {
            // Derived from a configured secret rather than generated, so a restarted or
            // scaled-out replica can still read what an earlier one wrote. An ephemeral key
            // would silently orphan every enrolled template on the next deployment.
            var material = _options.VoiceprintKey;
            if (string.IsNullOrEmpty(material))
            {
                _logger.LogWarning(
                    "VOICEPRINT_KEY is not set — voice enrolment is disabled. "
                    + "A biometric template will not be stored unencrypted.");
                return null;
            }

            return SHA256.HashData(Encoding.UTF8.GetBytes(material));
        });
    }

    /// <summary>Storage and encryption are both available.</summary>
    public bool IsAvailable => _table.Value is not null && _key.Value is not null;

    public async Task<Voiceprint?> GetAsync(
        string tenantId, string objectId, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return null;
        }

        try
        {
            var entity = await _table.Value!.GetEntityAsync<TableEntity>(
                tenantId, objectId, cancellationToken: cancellationToken);

            var cipher = entity.Value.GetString("Template");
            var nonce = entity.Value.GetString("Nonce");
            var tag = entity.Value.GetString("Tag");

            if (cipher is null || nonce is null || tag is null)
            {
                return null;
            }

            var plaintext = Decrypt(cipher, nonce, tag);
            if (plaintext is null)
            {
                // Wrong key, or tampering. Both mean this template cannot be trusted, and a
                // template that cannot be trusted must not be compared against.
                _logger.LogError(
                    "Voice template for {ObjectId} failed authenticated decryption.", objectId);
                return null;
            }

            return new Voiceprint(
                JsonSerializer.Deserialize<double[]>(plaintext) ?? [],
                entity.Value.GetString("ConsentVersion") ?? "unknown",
                entity.Value.GetDateTimeOffset("ConsentAt") ?? DateTimeOffset.MinValue,
                entity.Value.GetDateTimeOffset("EnrolledAt") ?? DateTimeOffset.MinValue,
                entity.Value.GetDouble("SelfConsistency") ?? 0);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the voice template for {ObjectId}.", objectId);
            return null;
        }
    }

    public async Task<bool> SaveAsync(
        string tenantId, string objectId, Voiceprint print,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return false;
        }

        try
        {
            var (cipher, nonce, tag) = Encrypt(JsonSerializer.Serialize(print.Template));

            var entity = new TableEntity(tenantId, objectId)
            {
                ["Template"] = cipher,
                ["Nonce"] = nonce,
                ["Tag"] = tag,
                ["Dimensions"] = print.Template.Length,
                ["ConsentVersion"] = print.ConsentVersion,
                ["ConsentAt"] = print.ConsentAt,
                ["EnrolledAt"] = print.EnrolledAt,
                ["SelfConsistency"] = print.SelfConsistency,
            };

            await _table.Value!.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not store the voice template for {ObjectId}.", objectId);
            return false;
        }
    }

    /// <summary>
    /// Delete a user's voiceprint.
    /// </summary>
    /// <remarks>
    /// Immediate and complete — the row goes, not a flag on it. Biometric data the user has
    /// withdrawn consent for should not survive as a soft-deleted record they cannot see.
    /// </remarks>
    public async Task<bool> DeleteAsync(
        string tenantId, string objectId, CancellationToken cancellationToken = default)
    {
        if (_table.Value is null)
        {
            return false;
        }

        try
        {
            await _table.Value.DeleteEntityAsync(tenantId, objectId, cancellationToken: cancellationToken);
            _logger.LogInformation("Voice template deleted for {ObjectId}.", objectId);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not delete the voice template for {ObjectId}.", objectId);
            return false;
        }
    }

    // ── AES-GCM ─────────────────────────────────────────────────────────────

    private (string Cipher, string Nonce, string Tag) Encrypt(string plaintext)
    {
        var key = _key.Value!;
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[bytes.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, bytes, cipher, tag);

        return (Convert.ToBase64String(cipher),
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(tag));
    }

    private string? Decrypt(string cipher, string nonce, string tag)
    {
        try
        {
            var key = _key.Value!;
            var cipherBytes = Convert.FromBase64String(cipher);
            var plain = new byte[cipherBytes.Length];

            using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
            aes.Decrypt(
                Convert.FromBase64String(nonce),
                cipherBytes,
                Convert.FromBase64String(tag),
                plain);

            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException)
        {
            // Authentication failure. Deliberately indistinguishable from a wrong key here:
            // the caller's only correct response to either is to refuse the template.
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
