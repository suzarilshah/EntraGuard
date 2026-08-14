using System.Net.Http.Headers;
using System.Text.Json;
using EntraGuard.Shared.Supportability;
using EntraGuard.MediaService.Configuration;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Agents;

/// <summary>
/// Talks to the SpeechBrain sidecar.
///
/// Every method here fails soft and returns null. That is deliberate and it is the most
/// important property of this class: voice is a supplementary factor, so a scorer that is
/// down, slow, or misconfigured must degrade the check to "not assessed" and never to
/// "refused". The alternative — a dependency outage locking every enrolled user out of the
/// application EntraGuard protects — would be a worse security incident than the one voice
/// verification prevents.
/// </summary>
public sealed class VoiceprintClient(
    IHttpClientFactory httpClientFactory,
    IOptions<EntraGuardOptions> options,
    ILogger<VoiceprintClient> logger)
{
    public const string ClientName = "voiceprint";

    public bool IsConfigured => !string.IsNullOrEmpty(options.Value.VoiceprintUrl);

    /// <summary>
    /// Turn 16 kHz PCM16 into a normalised speaker embedding.
    /// </summary>
    /// <returns>Null when the audio is unusable or the scorer is unreachable.</returns>
    /// <summary>
    /// Retry and circuit state for the sidecar.
    ///
    /// The sidecar is the one dependency here that genuinely falls over: it holds a PyTorch
    /// model, cold-starts in tens of seconds, and lives on an internal ingress that can drop
    /// a connection during a revision change. Before this, a single dropped connection
    /// produced a NotAssessed verification and a puzzled user.
    ///
    /// Public so the diagnostics endpoint can report it without calling anything.
    /// </summary>
    public static readonly Resilient Circuit = new(
        "voiceprint-sidecar", failuresBeforeOpen: 5, openFor: TimeSpan.FromSeconds(30), maxAttempts: 3);

    /// <summary>Set once at startup, so shedding is recorded rather than merely quiet.</summary>
    public Sinks.FaultRecorder? Faults { get; set; }

    public async Task<double[]?> EmbedAsync(byte[] pcm16, CancellationToken cancellationToken)
    {
        if (!IsConfigured || pcm16.Length == 0)
        {
            return null;
        }

        // Retried, with backoff and jitter, and shed entirely once the sidecar has failed
        // five times running. Shedding is the self-healing part: a dead dependency stops
        // costing every subsequent caller a full timeout, and calls resume automatically on
        // the next probe with nothing to restart and nobody to page.
        return await Circuit.RunAsync(
            token => EmbedOnceAsync(pcm16, token),
            fallback: null,
            cancellationToken,
            onShed: detail => Faults?.Record(Shared.Supportability.Fault.DependencyOpen(
                Shared.Supportability.FaultComponent.Voice, "The voiceprint scorer", detail)));
    }

    /// <summary>
    /// One attempt. Transport failures are allowed to THROW rather than being swallowed, so
    /// the circuit above can count them — a version of this that returned null on a dropped
    /// connection would look identical to a healthy "no match" and the circuit would never
    /// open.
    /// </summary>
    private async Task<double[]?> EmbedOnceAsync(byte[] pcm16, CancellationToken cancellationToken)
    {
        {
            var client = httpClientFactory.CreateClient(ClientName);

            // Raw bytes rather than base64 JSON: audio triples in size through base64, and
            // this runs while somebody is holding a phone waiting for an answer.
            using var content = new ByteArrayContent(pcm16);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var response = await client.PostAsync(
                $"{options.Value.VoiceprintUrl.TrimEnd('/')}/embed", content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // 422 is the ordinary "not enough speech" case, not an incident.
                logger.LogInformation(
                    "Voiceprint embed returned {Status}: {Detail}",
                    response.StatusCode,
                    (await response.Content.ReadAsStringAsync(cancellationToken)).Length > 200
                        ? "(truncated)"
                        : await response.Content.ReadAsStringAsync(cancellationToken));
                return null;
            }

            using var payload = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));

            return payload.RootElement.GetProperty("embedding")
                .EnumerateArray().Select(e => e.GetDouble()).ToArray();
        }
    }

    /// <summary>
    /// Cosine similarity between two embeddings.
    /// </summary>
    /// <remarks>
    /// Computed locally rather than over HTTP. Both vectors are already L2-normalised by
    /// the sidecar, so this is a dot product — a network round trip for eight lines of
    /// arithmetic would add latency to a live call for nothing, and add one more way for
    /// the check to fail open.
    /// </remarks>
    public static double? Similarity(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        if (a.Count == 0 || a.Count != b.Count)
        {
            return null;
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        var denominator = Math.Sqrt(na) * Math.Sqrt(nb);
        return denominator < 1e-9 ? null : dot / denominator;
    }

    /// <summary>
    /// Average several embeddings of the same speaker into one template.
    /// </summary>
    /// <remarks>
    /// Summed, then L2-normalised. The division by count is skipped because normalising
    /// afterwards makes it irrelevant — and the template MUST end up unit length, since
    /// every comparison downstream assumes it and a template of the wrong magnitude scores
    /// uniformly low against everyone, including its owner.
    /// </remarks>
    public static double[] Average(IReadOnlyList<double[]> embeddings)
    {
        var dimensions = embeddings[0].Length;
        var sum = new double[dimensions];

        foreach (var e in embeddings)
        {
            for (var i = 0; i < dimensions; i++)
            {
                sum[i] += e[i];
            }
        }

        var norm = Math.Sqrt(sum.Sum(v => v * v));
        if (norm < 1e-9)
        {
            // Embeddings that cancel out entirely are not a usable template.
            return sum;
        }

        for (var i = 0; i < dimensions; i++)
        {
            sum[i] /= norm;
        }

        return sum;
    }
}
