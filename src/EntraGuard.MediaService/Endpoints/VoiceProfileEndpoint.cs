using EntraGuard.MediaService.Agents;
using EntraGuard.MediaService.Configuration;
using EntraGuard.MediaService.Sinks;
using EntraGuard.Shared.Voice;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Endpoints;

/// <summary>
/// Voice diagnostics — the scorer's health and its accuracy.
///
/// Anonymous, because nothing here touches a user. Enrolment and deletion live in
/// <see cref="VoiceEnrollmentEndpoint"/> and are authenticated, since those DO touch a
/// person's biometric and must take identity from a validated token rather than a body.
///
/// These two endpoints answer different questions, and the distinction matters: the
/// self-test says the pipeline carries audio, and calibration says the model can tell two
/// speakers apart. A system can pass the first and be useless.
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

        // ── Does the model actually separate people? ─────────────────────────
        //
        // The self-test proves bytes move. This proves discrimination, which is a different
        // and much more important claim — and it produces the thresholds rather than
        // inheriting them from a published benchmark run on studio recordings.
        app.MapPost("/api/voice-profile/calibrate", async (
            VoiceCalibration calibration,
            CancellationToken cancellationToken) =>
        {
            var result = await calibration.RunAsync(cancellationToken);

            if (result.Failure is not null)
            {
                return Results.Ok(new { ran = false, detail = result.Failure });
            }

            return Results.Ok(new
            {
                ran = true,
                speakers = result.Speakers,
                recommendation = ThresholdRecommendation.Recommend(result.Genuine, result.Impostor),
                caveat = "Measured on synthesised voices, which are cleaner than telephony "
                       + "and more distinct from each other than two colleagues with the same "
                       + "accent. Treat the separation as an optimistic bound and the "
                       + "thresholds as a starting point to be moved once real calls "
                       + "accumulate in EntraGuard_Verification_CL.",
            });
        })
        .WithName("VoiceCalibrate");

        // ── Does enrolment work? ─────────────────────────────────────────────
        //
        // Runs the whole enrolment pipeline on synthesised speech: gates, consistency,
        // template, encryption, storage round trip, genuine and impostor scoring, deletion.
        // Everything except ACS. Writes under a reserved all-zero tenant and cleans up, so
        // no real profile can be touched.
        app.MapPost("/api/voice-profile/rehearse", async (
            EnrollmentRehearsal rehearsal,
            CancellationToken cancellationToken) =>
            Results.Ok(await rehearsal.RunAsync(cancellationToken)))
        .WithName("VoiceEnrollmentRehearsal");

        // ── Does the ACS half of enrolment work? ─────────────────────────────
        //
        // The rehearsal covers everything from audio to template. This covers the part it
        // cannot: placing a real Call Automation call, CallConnected, PlayCompleted timing,
        // the media socket attaching, and the state machine reaching a terminal state
        // instead of hanging.
        //
        // The callee is a throwaway ACS identity, answered automatically by this service's
        // own IncomingCall handler. It produces no speech, so enrolment is EXPECTED to end in the
        // no-usable-audio failure — and that is the point: reaching that failure proves the
        // prompts played, the socket attached, the quality gate ran and the retry bound
        // terminated the call. A hang would prove the opposite.
        //
        // Locked to a throwaway identity and the reserved all-zero tenant, so it cannot place
        // a call to a person or touch a real profile.
        app.MapPost("/api/voice-profile/acs-smoke", async (
            AcsEnrollmentSmokeTest smoke,
            CancellationToken cancellationToken) =>
            Results.Ok(await smoke.RunAsync(cancellationToken)))
        .WithName("VoiceAcsSmokeTest");
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
