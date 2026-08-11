using EntraGuard.MediaService.Sinks;
using EntraGuard.Shared.Voice;

namespace EntraGuard.MediaService.Agents;

/// <summary>
/// Runs everything enrolment does except the phone call, so the parts that only a human
/// could previously exercise are provable without one.
///
/// The enrolment path had a specific problem: it was written, reviewed, deployed — and had
/// never executed. Quality gates, the consistency check, template construction, encryption,
/// the storage round trip and the verification comparison were all "should work". Every bug
/// in this project has come from exactly that state, and the ones that survived to a live
/// call cost a real person a real phone call to find.
///
/// This drives the same components with synthesised speech. What it cannot cover is ACS
/// itself — placing the call, the PlayCompleted timing, the media socket — so a passing
/// rehearsal narrows the untested surface to the telephony, rather than claiming the
/// feature works.
///
/// It writes under a reserved identity and deletes it afterwards, so no real profile is
/// touched and nothing is left behind.
/// </summary>
public sealed class EnrollmentRehearsal(
    VoiceCalibration speech,
    VoiceprintClient voiceprint,
    VoiceprintStore store,
    ILogger<EnrollmentRehearsal> logger)
{
    /// <summary>
    /// Reserved identity for rehearsals.
    ///
    /// A tenant of all-zeroes cannot collide with a real Entra tenant, so a rehearsal can
    /// never overwrite or delete somebody's actual voice profile even if the code below is
    /// wrong about which row it is touching.
    /// </summary>
    private const string RehearsalTenant = "00000000-0000-0000-0000-000000000000";
    private const string RehearsalObject = "rehearsal-subject";

    /// <summary>The enrolment voice, and a different one standing in for an impostor.</summary>
    private const string EnrolVoice = "en-GB-SoniaNeural";
    private const string ImpostorVoice = "en-US-GuyNeural";

    private static readonly string[] Phrases =
    [
        "My voice is the key to this account and nobody else may use it",
        "The quick brown fox jumps over the lazy dog near the river",
        "Bright yellow flowers grow beside the old stone bridge in autumn",
    ];

    public async Task<object> RunAsync(CancellationToken cancellationToken)
    {
        var steps = new List<object>();
        var ok = true;

        void Step(string name, bool passed, string detail)
        {
            steps.Add(new { step = name, passed, detail });
            if (!passed) ok = false;
        }

        try
        {
            if (!voiceprint.IsConfigured || !store.IsAvailable)
            {
                return new
                {
                    ran = false,
                    detail = "Voice scoring or encrypted storage is not configured.",
                };
            }

            // 1. Capture — the same gates OnPhraseSpokenAsync applies to each recording.
            var embeddings = new List<double[]>();

            foreach (var phrase in Phrases)
            {
                var pcm = await speech.SpeakAsPcmAsync(EnrolVoice, phrase, cancellationToken);
                if (pcm is null || pcm.Length == 0)
                {
                    Step("capture", false, "Speech synthesis produced no audio.");
                    return new { ran = true, passed = false, steps };
                }

                var seconds = AudioResampler.Seconds(pcm.Length);
                var voiced = AudioResampler.VoicedRatio(pcm);

                // Same thresholds as the live path, deliberately: a rehearsal that uses
                // looser gates proves the gates it did not run.
                if (seconds < 1.5 || voiced < 0.05)
                {
                    Step("quality-gate", false,
                        $"Phrase gave {seconds:F1}s at {voiced:P0} voiced — the live path would retry.");
                    return new { ran = true, passed = false, steps };
                }

                var embedding = await voiceprint.EmbedAsync(pcm, cancellationToken);
                Array.Clear(pcm);

                if (embedding is null)
                {
                    Step("embed", false, "The scorer returned no embedding.");
                    return new { ran = true, passed = false, steps };
                }

                embeddings.Add(embedding);
            }

            Step("capture", true,
                $"{embeddings.Count} phrases captured and embedded, each past the duration "
                + "and voiced-ratio gates.");

            // 2. Consistency — the check that refuses a template built from disagreeing
            //    recordings, which is what a second speaker in the room produces.
            var worst = 1.0;
            for (var i = 0; i < embeddings.Count; i++)
            {
                for (var j = i + 1; j < embeddings.Count; j++)
                {
                    var score = VoiceprintClient.Similarity(embeddings[i], embeddings[j]);
                    if (score is not null && score < worst) worst = score.Value;
                }
            }

            Step("consistency", worst >= 0.60,
                $"Lowest pairwise agreement {worst:F3} (must be at least 0.60).");

            // 3. Template — must be unit length or every later comparison is scaled wrongly.
            var template = VoiceprintClient.Average(embeddings);
            var length = Math.Sqrt(template.Sum(v => v * v));

            Step("template", Math.Abs(length - 1.0) < 1e-6,
                $"{template.Length} dimensions, length {length:F6}.");

            // 4. Encrypt, store, read back. The round trip is the part that silently fails
            //    when a key changes between replicas — and it fails by returning nothing,
            //    which reads identically to "never enrolled".
            var saved = await store.SaveAsync(
                RehearsalTenant, RehearsalObject,
                new Voiceprint(template, "rehearsal", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, worst),
                cancellationToken);

            Step("store", saved, saved ? "Template encrypted and written." : "Write failed.");

            var readBack = await store.GetAsync(RehearsalTenant, RehearsalObject, cancellationToken);
            var intact = readBack is not null
                && readBack.Template.Length == template.Length
                && VoiceprintClient.Similarity(readBack.Template, template) is > 0.9999;

            Step("round-trip", intact,
                intact
                    ? "Decrypted template is identical to the one stored."
                    : "The stored template did not survive decryption.");

            // 5. Verification — a genuine speaker and an impostor, scored the way a real
            //    call scores them, against the template that was just read back.
            double? genuineScore = null;
            double? impostorScore = null;

            if (readBack is not null)
            {
                var genuinePcm = await speech.SpeakAsPcmAsync(
                    EnrolVoice,
                    "I was in Petaling Jaya when I signed in this morning on my laptop.",
                    cancellationToken);

                var impostorPcm = await speech.SpeakAsPcmAsync(
                    ImpostorVoice,
                    "I was in Petaling Jaya when I signed in this morning on my laptop.",
                    cancellationToken);

                if (genuinePcm is not null)
                {
                    var e = await voiceprint.EmbedAsync(genuinePcm, cancellationToken);
                    Array.Clear(genuinePcm);
                    if (e is not null) genuineScore = VoiceprintClient.Similarity(e, readBack.Template);
                }

                if (impostorPcm is not null)
                {
                    var e = await voiceprint.EmbedAsync(impostorPcm, cancellationToken);
                    Array.Clear(impostorPcm);
                    if (e is not null) impostorScore = VoiceprintClient.Similarity(e, readBack.Template);
                }
            }

            // The impostor says the SAME sentence as the genuine speaker. If the model were
            // keying on words rather than voice this is where it would show, and the whole
            // feature would be worthless.
            var genuineDecision = VoiceThresholds.Evaluate(genuineScore, 8, enforce: true);
            var impostorDecision = VoiceThresholds.Evaluate(impostorScore, 8, enforce: true);

            Step("verify-genuine",
                genuineDecision.Outcome == VoiceOutcome.Match,
                $"Enrolled speaker scored {genuineScore:F3} → {genuineDecision.Outcome}.");

            Step("verify-impostor",
                impostorDecision.Outcome != VoiceOutcome.Match,
                $"Different speaker, same sentence, scored {impostorScore:F3} → "
                + $"{impostorDecision.Outcome}"
                + (impostorDecision.RequiresStepUp ? " (steps up)." : "."));

            // 6. Deletion — the user's right to withdraw, and the rehearsal's own cleanup.
            var deleted = await store.DeleteAsync(RehearsalTenant, RehearsalObject, cancellationToken);
            var gone = await store.GetAsync(RehearsalTenant, RehearsalObject, cancellationToken) is null;

            Step("delete", deleted && gone,
                gone ? "Rehearsal profile removed and confirmed absent." : "Deletion did not take.");

            logger.LogInformation("Enrolment rehearsal finished: {Result}.", ok ? "passed" : "FAILED");

            return new
            {
                ran = true,
                passed = ok,
                steps,
                genuineScore,
                impostorScore,
                separation = genuineScore - impostorScore,
                covers = "Quality gates, consistency, template construction, encryption, the "
                       + "storage round trip, scoring and deletion — every part of enrolment "
                       + "except ACS itself.",
                doesNotCover = "Placing the call, PlayCompleted timing, and the media socket. "
                             + "Those still need a live enrolment to prove.",
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Enrolment rehearsal failed.");

            // Best-effort cleanup, so a crashed rehearsal does not leave a row behind.
            await store.DeleteAsync(RehearsalTenant, RehearsalObject, CancellationToken.None);
            return new { ran = true, passed = false, steps, error = ex.Message };
        }
    }
}
