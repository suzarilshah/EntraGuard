using Azure.Core;
using EntraGuard.MediaService.Configuration;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Options;

namespace EntraGuard.MediaService.Agents;

/// <param name="Genuine">Scores between two utterances of the SAME speaker.</param>
/// <param name="Impostor">Scores between utterances of DIFFERENT speakers.</param>
public sealed record CalibrationResult(
    IReadOnlyList<double> Genuine,
    IReadOnlyList<double> Impostor,
    IReadOnlyList<string> Speakers,
    string? Failure = null);

/// <summary>
/// Measures whether the speaker model actually separates people, and where to put the
/// thresholds.
///
/// The problem this solves is that until now nothing had established ECAPA works in this
/// deployment at all. The self-test proves the pipeline carries bytes; it says nothing about
/// discrimination, and shipping thresholds copied from a VoxCeleb leaderboard would mean
/// refusing real users on the strength of somebody else's dataset.
///
/// Synthesised speech, from several Azure neural voices, standing in for several people.
/// Two utterances of the same voice are a genuine pair; two utterances of different voices
/// are an impostor pair. That is a real measurement of the model, run through the real
/// client, at the real sample rate.
///
/// It is NOT a substitute for enrolling humans. Synthetic voices are cleaner than telephony
/// and more distinct from one another than two colleagues with the same accent, so the
/// separation measured here is an optimistic bound. What it legitimately gives is proof the
/// model discriminates at all, and a defensible starting threshold to replace guesses —
/// which then moves as real calls accumulate in Sentinel.
/// </summary>
public sealed class VoiceCalibration(
    IOptions<EntraGuardOptions> options,
    TokenCredential credential,
    IConfiguration configuration,
    VoiceprintClient voiceprint,
    ILogger<VoiceCalibration> logger)
{
    /// <summary>
    /// Distinct voices, chosen for variety rather than convenience.
    ///
    /// Two of them are deliberately close — same locale, same apparent gender — because a
    /// calibration run against obviously dissimilar voices flatters the model and produces a
    /// threshold that collapses the first time two colleagues with the same accent use it.
    /// </summary>
    private static readonly string[] Voices =
    [
        "en-US-AvaMultilingualNeural",
        "en-US-JennyNeural",       // close to Ava: same locale, similar register
        "en-GB-RyanNeural",
        "en-US-GuyNeural",         // close to Ryan: different locale, similar register
        "en-IN-NeerjaNeural",
    ];

    /// <summary>
    /// Different sentences per utterance, on purpose.
    ///
    /// Comparing a voice to itself saying the SAME words measures how repeatable the
    /// synthesiser is, not how identifiable the speaker is — it would score near 1.0 and
    /// tell us nothing. Real verification compares speech about one thing to enrolment
    /// speech about another, so the calibration has to as well.
    /// </summary>
    private static readonly string[] Utterances =
    [
        "I was in Petaling Jaya when I last signed in to the system this morning.",
        "The verification code appeared on my screen and I entered it on the keypad.",
        "My first pet was a small grey cat that used to sleep under the stairs.",
    ];

    public async Task<CalibrationResult> RunAsync(CancellationToken cancellationToken)
    {
        if (!voiceprint.IsConfigured)
        {
            return new CalibrationResult([], [], [], "No voiceprint scorer is configured.");
        }

        var speechResourceId = configuration["SPEECH_RESOURCE_ID"];
        if (string.IsNullOrEmpty(speechResourceId))
        {
            return new CalibrationResult([], [], [], "Speech synthesis is not configured.");
        }

        try
        {
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]),
                cancellationToken);

            var config = SpeechConfig.FromAuthorizationToken(
                $"aad#{speechResourceId}#{token.Token}", options.Value.SpeechRegion);

            // Exactly what ECAPA wants and what the call path produces after resampling, so
            // this measures the same thing the live scorer will see.
            config.SetSpeechSynthesisOutputFormat(
                SpeechSynthesisOutputFormat.Raw16Khz16BitMonoPcm);

            // voice -> embeddings, one per utterance.
            var byVoice = new Dictionary<string, List<double[]>>();

            foreach (var voice in Voices)
            {
                config.SpeechSynthesisVoiceName = voice;
                var embeddings = new List<double[]>();

                foreach (var text in Utterances)
                {
                    var pcm = await SynthesiseAsync(config, text, cancellationToken);
                    if (pcm is null || pcm.Length == 0)
                    {
                        continue;
                    }

                    var embedding = await voiceprint.EmbedAsync(pcm, cancellationToken);
                    Array.Clear(pcm);

                    if (embedding is not null)
                    {
                        embeddings.Add(embedding);
                    }
                }

                if (embeddings.Count >= 2)
                {
                    byVoice[voice] = embeddings;
                }

                logger.LogInformation(
                    "Calibration: {Voice} produced {Count} embeddings.", voice, embeddings.Count);
            }

            if (byVoice.Count < 2)
            {
                return new CalibrationResult([], [], [],
                    "Not enough voices produced usable audio to measure separation.");
            }

            var genuine = new List<double>();
            var impostor = new List<double>();

            foreach (var (voice, embeddings) in byVoice)
            {
                // Same speaker, different sentences.
                for (var i = 0; i < embeddings.Count; i++)
                {
                    for (var j = i + 1; j < embeddings.Count; j++)
                    {
                        var score = VoiceprintClient.Similarity(embeddings[i], embeddings[j]);
                        if (score is not null) genuine.Add(score.Value);
                    }
                }

                // Different speakers. Compared against a TEMPLATE built the same way
                // enrolment builds one, so the impostor scores are what a real attacker
                // would actually be scored against rather than a single utterance.
                var template = VoiceprintClient.Average(embeddings);

                foreach (var (otherVoice, otherEmbeddings) in byVoice)
                {
                    if (otherVoice == voice) continue;

                    foreach (var other in otherEmbeddings)
                    {
                        var score = VoiceprintClient.Similarity(other, template);
                        if (score is not null) impostor.Add(score.Value);
                    }
                }
            }

            return new CalibrationResult(genuine, impostor, byVoice.Keys.ToList());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Voice calibration failed.");
            return new CalibrationResult([], [], [], ex.Message);
        }
    }

    /// <summary>
    /// Speech as 16 kHz PCM16, for exercising the voice path without a phone call.
    /// </summary>
    /// <remarks>
    /// Public so the enrolment rehearsal uses exactly this, rather than its own copy of the
    /// Speech SDK setup. Two copies drift, and the one that drifts is always the one that
    /// was only ever run in a test.
    /// </remarks>
    public async Task<byte[]?> SpeakAsPcmAsync(
        string voice, string text, CancellationToken cancellationToken)
    {
        var speechResourceId = configuration["SPEECH_RESOURCE_ID"];
        if (string.IsNullOrEmpty(speechResourceId))
        {
            return null;
        }

        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]),
            cancellationToken);

        var config = SpeechConfig.FromAuthorizationToken(
            $"aad#{speechResourceId}#{token.Token}", options.Value.SpeechRegion);

        config.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Raw16Khz16BitMonoPcm);
        config.SpeechSynthesisVoiceName = voice;

        return await SynthesiseAsync(config, text, cancellationToken);
    }

    private static async Task<byte[]?> SynthesiseAsync(
        SpeechConfig config, string text, CancellationToken cancellationToken)
    {
        // Null output means "give me the bytes" rather than "play it on a speaker" — there
        // is no audio device in a container, and the default would fail.
        using var synthesiser = new SpeechSynthesizer(config, null);
        using var result = await synthesiser.SpeakTextAsync(text).WaitAsync(cancellationToken);

        return result.Reason == ResultReason.SynthesizingAudioCompleted
            ? result.AudioData
            : null;
    }
}

/// <summary>Turns two score distributions into a threshold recommendation.</summary>
public static class ThresholdRecommendation
{
    /// <param name="genuine">Same-speaker scores.</param>
    /// <param name="impostor">Different-speaker scores.</param>
    /// <returns>
    /// Accept, reject, and the equal-error-ish crossing, plus whether the two distributions
    /// are separated well enough for a threshold to be meaningful at all.
    /// </returns>
    /// <remarks>
    /// Accept is placed below the weakest genuine score and reject above the strongest
    /// impostor score — deliberately conservative at both ends, so the middle band absorbs
    /// the overlap instead of a false accept or a false reject being forced.
    ///
    /// If the weakest genuine score sits BELOW the strongest impostor score the
    /// distributions overlap, and no single pair of thresholds separates them. That is
    /// reported rather than papered over: a recommendation derived from overlapping
    /// distributions is a guess wearing a number.
    /// </remarks>
    public static object Recommend(IReadOnlyList<double> genuine, IReadOnlyList<double> impostor)
    {
        if (genuine.Count == 0 || impostor.Count == 0)
        {
            return new { usable = false, detail = "Not enough measurements." };
        }

        var minGenuine = genuine.Min();
        var maxImpostor = impostor.Max();
        var separated = minGenuine > maxImpostor;

        // A margin inside each distribution rather than exactly on its edge, because the
        // sample here is small and the true tails extend past what was measured.
        var accept = Math.Round(minGenuine - 0.05, 2);
        var reject = Math.Round(maxImpostor + 0.05, 2);

        return new
        {
            usable = true,
            separated,
            genuine = new
            {
                count = genuine.Count,
                min = Math.Round(minGenuine, 4),
                mean = Math.Round(genuine.Average(), 4),
                max = Math.Round(genuine.Max(), 4),
            },
            impostor = new
            {
                count = impostor.Count,
                min = Math.Round(impostor.Min(), 4),
                mean = Math.Round(impostor.Average(), 4),
                max = Math.Round(maxImpostor, 4),
            },
            margin = Math.Round(minGenuine - maxImpostor, 4),
            recommendedAccept = accept,
            recommendedReject = separated ? reject : Math.Round(maxImpostor - 0.05, 2),
            detail = separated
                ? "Genuine and impostor scores do not overlap. These thresholds separate "
                + "the measured populations, with the middle band absorbing anything between."
                : "The distributions OVERLAP: at least one impostor scored above the weakest "
                + "genuine pair. No pair of thresholds separates them cleanly, so the middle "
                + "band will be wide and most calls will step up. Do not enable enforcement "
                + "on this evidence.",
        };
    }
}
