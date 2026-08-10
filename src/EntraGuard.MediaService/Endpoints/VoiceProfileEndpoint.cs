using EntraGuard.MediaService.Agents;
using EntraGuard.MediaService.Configuration;
using EntraGuard.MediaService.Sinks;
using EntraGuard.Shared.Voice;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Endpoints;

/// <summary>
/// Voice profile diagnostics.
///
/// The enrolment and deletion endpoints are NOT here yet, deliberately. They must derive the
/// user from a validated access token rather than a request body, and until that token
/// validation exists, publishing them would mean anyone who can reach this service could
/// enrol their own voice against somebody else's account — the precise attack voice
/// verification is meant to prevent.
///
/// What is here reveals nothing about any user: whether the scorer is reachable, and
/// whether it discriminates between two different signals.
/// </summary>
public static class VoiceProfileEndpoint
{
    public static void MapVoiceProfile(this IEndpointRouteBuilder app)
    {
        // ── Is voice verification actually working? ──────────────────────────
        //
        // Answers with evidence rather than configuration. "VOICEPRINT_URL is set" only
        // means somebody typed a URL; this proves the sidecar answers, that it produces
        // embeddings of the expected shape, and that two different signals do not score as
        // the same speaker — which is the one property the whole feature rests on.
        app.MapGet("/api/voice-profile/selftest", async (
            VoiceprintClient client,
            VoiceprintStore store,
            IOptions<EntraGuardOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (!client.IsConfigured)
            {
                return Results.Ok(new
                {
                    configured = false,
                    detail = "No scorer configured. Verification runs without voice comparison.",
                });
            }

            // Two synthetic signals. Not speech, so the scores are not speaker-verification
            // results and must not be read as accuracy — they establish that the pipeline
            // carries audio in and distinct numbers out.
            var a = Tone(220, seconds: 4);
            var b = Tone(660, seconds: 4);

            var embeddingA = await client.EmbedAsync(a, cancellationToken);
            var embeddingB = await client.EmbedAsync(b, cancellationToken);

            if (embeddingA is null || embeddingB is null)
            {
                return Results.Ok(new
                {
                    configured = true,
                    reachable = false,
                    detail = "The scorer did not return an embedding. Voice will not be assessed.",
                });
            }

            return Results.Ok(new
            {
                configured = true,
                reachable = true,
                dimensions = embeddingA.Length,
                selfScore = VoiceprintClient.Similarity(embeddingA, embeddingA),
                crossScore = VoiceprintClient.Similarity(embeddingA, embeddingB),
                storageReady = store.IsAvailable,
                mode = options.Value.VoiceEnforce ? "enforce" : "observe",
                accept = options.Value.VoiceAcceptThreshold,
                reject = options.Value.VoiceRejectThreshold,
                detail = "selfScore must be 1. crossScore below it shows the model "
                       + "distinguishes inputs. Neither is a speaker-verification accuracy "
                       + "figure — only real enrolled speech gives that.",
            });
        })
        .WithName("VoiceSelfTest");
    }

    /// <summary>A sine wave as 16 kHz PCM16, for exercising the pipeline without a person.</summary>
    private static byte[] Tone(double hz, double seconds)
    {
        var samples = (int)(AudioResampler.ModelSampleRate * seconds);
        var pcm = new byte[samples * 2];

        for (var i = 0; i < samples; i++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * hz * i / AudioResampler.ModelSampleRate) * 12000);
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), value);
        }

        return pcm;
    }
}
