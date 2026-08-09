using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using EntraGuard.MediaService.Configuration;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Agents;

/// <summary>
/// Direct HTTP client for Azure OpenAI chat completions.
///
/// Written against the REST API rather than the SDK for two reasons, both learned the
/// hard way:
///
/// 1. Version coupling. Azure.AI.OpenAI 2.1.0 is compiled against OpenAI 2.1.0 and calls
///    members that were removed by 2.12.0. Pinning the newer package to reach
///    ReasoningEffortLevel compiled cleanly and then threw MissingMethodException on every
///    request at runtime. There is no combination of stable versions that exposes the knob
///    this service needs, so the dependency is not worth keeping.
///
/// 2. Control. reasoning_effort is the single most important parameter here — it is the
///    difference between a 15-second verdict and a usable one — and it must not depend on
///    whether a wrapper has caught up with the service.
///
/// EntraGuard makes exactly one kind of model call. The wire format being legible in the
/// source is worth more than the ergonomics of a client library.
/// </summary>
public sealed class AnalystClient(
    IHttpClientFactory httpClientFactory,
    TokenCredential credential,
    IOptions<EntraGuardOptions> options,
    ILogger<AnalystClient> logger)
{
    public const string ClientName = "aoai";
    private const string Scope = "https://cognitiveservices.azure.com/.default";

    private readonly EntraGuardOptions _options = options.Value;

    public sealed record ChatResult(bool Success, string? Content, string? Error, int StatusCode);

    /// <summary>
    /// Request a structured verdict.
    /// </summary>
    /// <param name="systemPrompt">Detection rules.</param>
    /// <param name="userPrompt">The transcript window under assessment.</param>
    /// <param name="jsonSchema">Strict schema the response must satisfy.</param>
    public async Task<ChatResult> CompleteAsync(
        string systemPrompt,
        string userPrompt,
        string jsonSchema,
        CancellationToken cancellationToken = default)
    {
        var url =
            $"{_options.OpenAiEndpoint.TrimEnd('/')}/openai/deployments/{_options.OpenAiDeployment}" +
            $"/chat/completions?api-version={_options.OpenAiApiVersion}";

        var payload = new Dictionary<string, object?>
        {
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
            ["response_format"] = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "scam_assessment",
                    // Strict mode makes a malformed response impossible rather than merely
                    // unlikely — there is no parse-failure path to handle mid-call.
                    strict = true,
                    schema = JsonSerializer.Deserialize<JsonElement>(jsonSchema),
                },
            },
            // Latency is a correctness property here. Measured on this deployment, default
            // reasoning effort produced 15-16 second verdicts — longer than the gap between
            // "just approve it" and the victim approving, which makes the system forensic
            // rather than preventive.
            ["reasoning_effort"] = "low",
            // Reasoning models count reasoning tokens against this budget, so it has to be
            // generous enough that thinking does not starve the actual answer and return
            // an empty completion.
            ["max_completion_tokens"] = 8000,
        };

        try
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext([Scope]), cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

            using var client = httpClientFactory.CreateClient(ClientName);
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Logged in full: a 400 here usually means the api-version does not accept
                // reasoning_effort for this model, and the response body says exactly that.
                logger.LogError("Azure OpenAI returned {Status}: {Body}", (int)response.StatusCode, Truncate(body, 600));
                return new ChatResult(false, null, Truncate(body, 300), (int)response.StatusCode);
            }

            using var document = JsonDocument.Parse(body);
            var content = document.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(content))
            {
                // Happens when reasoning consumes the whole token budget. Distinguished
                // from a transport failure because the remedy is completely different.
                var finish = document.RootElement.GetProperty("choices")[0]
                    .TryGetProperty("finish_reason", out var reason) ? reason.GetString() : "unknown";
                logger.LogError("Azure OpenAI returned an empty completion (finish_reason: {Finish}). " +
                    "Raise max_completion_tokens if this persists.", finish);
                return new ChatResult(false, null, $"Empty completion (finish_reason: {finish})", 200);
            }

            return new ChatResult(true, content, null, 200);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Azure OpenAI request failed.");
            return new ChatResult(false, null, ex.Message, 0);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
