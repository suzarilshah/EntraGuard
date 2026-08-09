using Azure;
using Azure.Core;
using Azure.Data.Tables;
using EntraGuard.MediaService.Configuration;
using EntraGuard.Shared.Verification;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Sinks;

/// <summary>
/// Where a user's registered knowledge questions live.
///
/// Two backends, tried in order, because neither alone covers the product:
///
///   1. <b>Entra ID custom security attributes.</b> The question and its answer hash sit on
///      the user object in their own directory, which is where identity data belongs and
///      what makes this defensible as an identity feature rather than an app feature.
///      Requires the Attribute Definition and Attribute Assignment Administrator roles in
///      that tenant — deliberately separate from Global Administrator, so this only works
///      in tenants that have opted EntraGuard in.
///
///   2. <b>Table Storage.</b> Everyone else. Sign-in is multitenant, so a user can arrive
///      from a directory EntraGuard has no rights in at all; refusing to enrol them would
///      make the feature unusable for exactly the audience the multitenant app is for.
///
/// Which one answered is reported, never hidden. "Stored in your directory" and "stored in
/// our database" are materially different promises to a user, and a store that blurs them
/// is making a claim it cannot keep.
/// </summary>
public sealed class KnowledgeStore
{
    /// <summary>Attribute set and attribute names, mirrored in both backends.</summary>
    private const string AttributeSet = "EntraGuard";
    private const string QuestionAttribute = "verificationQuestion";
    private const string SaltAttribute = "verificationSalt";
    private const string HashAttribute = "verificationAnswerHash";

    private const string TableName = "EntraGuardKnowledge";

    private readonly EntraGuardOptions _options;
    private readonly TokenCredential _credential;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<KnowledgeStore> _logger;
    private readonly Lazy<TableClient?> _table;

    public KnowledgeStore(
        IOptions<EntraGuardOptions> options,
        TokenCredential credential,
        IHttpClientFactory httpClientFactory,
        ILogger<KnowledgeStore> logger)
    {
        _options = options.Value;
        _credential = credential;
        _httpClientFactory = httpClientFactory;
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
                _credential);

            client.CreateIfNotExists();
            return client;
        });
    }

    /// <summary>Where a stored question came from, surfaced to the caller.</summary>
    public enum Backing { None, Directory, Table }

    public sealed record Stored(KnowledgeQuestion Question, Backing Backing);

    /// <summary>
    /// Read the registered question for a user, preferring their own directory.
    /// </summary>
    public async Task<Stored?> GetAsync(
        string tenantId, string objectId, CancellationToken cancellationToken = default)
    {
        var directory = await TryReadDirectoryAsync(tenantId, objectId, cancellationToken);
        if (directory is not null)
        {
            return new Stored(directory, Backing.Directory);
        }

        var table = await TryReadTableAsync(tenantId, objectId, cancellationToken);
        return table is null ? null : new Stored(table, Backing.Table);
    }

    /// <summary>
    /// Register a question, into the directory when possible and the table otherwise.
    /// </summary>
    public async Task<Backing> SaveAsync(
        string tenantId, string objectId, KnowledgeQuestion question,
        CancellationToken cancellationToken = default)
    {
        if (await TryWriteDirectoryAsync(tenantId, objectId, question, cancellationToken))
        {
            return Backing.Directory;
        }

        return await TryWriteTableAsync(tenantId, objectId, question, cancellationToken)
            ? Backing.Table
            : Backing.None;
    }

    // ── Entra ID custom security attributes ─────────────────────────────────

    private async Task<KnowledgeQuestion?> TryReadDirectoryAsync(
        string tenantId, string objectId, CancellationToken cancellationToken)
    {
        try
        {
            using var client = await GraphClientAsync(tenantId, cancellationToken);
            using var response = await client.GetAsync(
                $"users/{objectId}?$select=customSecurityAttributes", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // 403 here is the ordinary case, not an incident: it means this tenant has
                // not granted the attribute roles, which is most tenants.
                return null;
            }

            using var payload = System.Text.Json.JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));

            if (!payload.RootElement.TryGetProperty("customSecurityAttributes", out var attributes)
                || attributes.ValueKind != System.Text.Json.JsonValueKind.Object
                || !attributes.TryGetProperty(AttributeSet, out var set))
            {
                return null;
            }

            var question = Text(set, QuestionAttribute);
            var salt = Text(set, SaltAttribute);
            var hash = Text(set, HashAttribute);

            // A partial record is not a usable challenge. Treating one as usable would
            // produce a question nobody can ever answer correctly.
            return question is null || salt is null || hash is null
                ? null
                : new KnowledgeQuestion(question, salt, hash);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Directory read unavailable for {ObjectId} in {TenantId}.", objectId, tenantId);
            return null;
        }
    }

    private async Task<bool> TryWriteDirectoryAsync(
        string tenantId, string objectId, KnowledgeQuestion question, CancellationToken cancellationToken)
    {
        try
        {
            using var client = await GraphClientAsync(tenantId, cancellationToken);

            var body = new
            {
                customSecurityAttributes = new Dictionary<string, object>
                {
                    [AttributeSet] = new Dictionary<string, object>
                    {
                        ["@odata.type"] = "#Microsoft.DirectoryServices.CustomSecurityAttributeValue",
                        [QuestionAttribute] = question.Question,
                        [SaltAttribute] = question.Salt,
                        [HashAttribute] = question.AnswerHash,
                    },
                },
            };

            using var content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(body),
                System.Text.Encoding.UTF8,
                "application/json");

            using var response = await client.PatchAsync($"users/{objectId}", content, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Directory write unavailable for {ObjectId} in {TenantId}.", objectId, tenantId);
            return false;
        }
    }

    private async Task<HttpClient> GraphClientAsync(string tenantId, CancellationToken cancellationToken)
    {
        // Scoped to the user's OWN tenant. Graph tokens are tenant-specific, and a token for
        // our tenant reads our directory no matter whose object ID is in the URL.
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext(
                ["https://graph.microsoft.com/.default"], tenantId: tenantId),
            cancellationToken);

        var client = _httpClientFactory.CreateClient();
        client.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
        return client;
    }

    private static string? Text(System.Text.Json.JsonElement set, string name) =>
        set.TryGetProperty(name, out var value)
        && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    // ── Table Storage ───────────────────────────────────────────────────────

    private async Task<KnowledgeQuestion?> TryReadTableAsync(
        string tenantId, string objectId, CancellationToken cancellationToken)
    {
        var table = _table.Value;
        if (table is null)
        {
            return null;
        }

        try
        {
            var entity = await table.GetEntityAsync<TableEntity>(
                tenantId, objectId, cancellationToken: cancellationToken);

            var question = entity.Value.GetString(nameof(KnowledgeQuestion.Question));
            var salt = entity.Value.GetString(nameof(KnowledgeQuestion.Salt));
            var hash = entity.Value.GetString(nameof(KnowledgeQuestion.AnswerHash));

            return question is null || salt is null || hash is null
                ? null
                : new KnowledgeQuestion(question, salt, hash);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the knowledge question for {ObjectId}.", objectId);
            return null;
        }
    }

    private async Task<bool> TryWriteTableAsync(
        string tenantId, string objectId, KnowledgeQuestion question, CancellationToken cancellationToken)
    {
        var table = _table.Value;
        if (table is null)
        {
            return false;
        }

        try
        {
            // Partitioned by tenant so one directory's registrations never collide with
            // another's, even if two tenants somehow shared an object ID.
            var entity = new TableEntity(tenantId, objectId)
            {
                [nameof(KnowledgeQuestion.Question)] = question.Question,
                [nameof(KnowledgeQuestion.Salt)] = question.Salt,
                [nameof(KnowledgeQuestion.AnswerHash)] = question.AnswerHash,
                ["RegisteredAt"] = DateTimeOffset.UtcNow,
            };

            await table.UpsertEntityAsync(entity, TableUpdateMode.Replace, cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not store the knowledge question for {ObjectId}.", objectId);
            return false;
        }
    }
}
