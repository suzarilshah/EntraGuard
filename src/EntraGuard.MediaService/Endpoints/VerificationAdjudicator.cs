using EntraGuard.Shared.Detection;
using EntraGuard.Shared.Verification;
using EntraGuard.Shared.Voice;

namespace EntraGuard.MediaService.Endpoints;

/// <summary>
/// Decides the outcome of a verification attempt.
///
/// Extracted from the ACS callback path so the decision is a pure function of
/// (expected code, entered code, attempts, Analyst verdict). That matters for two
/// reasons: it is the security-critical logic in this feature and deserves to be
/// unit-testable, and it lets the simulator exercise the REAL rules rather than a
/// parallel copy that could drift from them.
/// </summary>
public static class VerificationAdjudicator
{
    /// <summary>Analyst risk at which a verification call is refused even with the right code.</summary>
    public const double CoercionRiskThreshold = 60;

    /// <summary>Confidence required before coercion is acted on — the policy gate's bar.</summary>
    public const double CoercionConfidenceThreshold = 0.75;

    public const int MaxAttempts = 3;

    /// <param name="Result">Null means inconclusive — prompt again.</param>
    public readonly record struct Verdict(VerificationResult? Result, string Reason);

    /// <summary>
    /// Adjudicate one code entry.
    ///
    /// Coercion is evaluated BEFORE the code, deliberately. A coached user normally enters
    /// the correct digits — that is what coaching produces — so checking the code first and
    /// returning early on a match would let every coerced attempt straight through. The
    /// order of these two checks is the whole security property.
    /// </summary>
    public static Verdict Adjudicate(
        string expectedCode,
        string enteredCode,
        int attempts,
        RiskAssessment? assessment,
        VoiceDecision? voice = null)
    {
        var coerced = assessment is not null
            && assessment.RiskScore >= CoercionRiskThreshold
            && assessment.Confidence >= CoercionConfidenceThreshold;

        if (coerced)
        {
            var vectors = string.Join(", ", assessment!.Vectors);
            var codeNote = enteredCode == expectedCode
                ? "The code was correct, but"
                : "The code was incorrect, and separately";

            return new Verdict(
                VerificationResult.BlockedCoercion,
                $"{codeNote} EntraGuard detected the user was being coached during the " +
                $"verification call (risk {assessment.RiskScore:F0}/100, confidence " +
                $"{assessment.Confidence:P0}{(vectors.Length > 0 ? $", {vectors}" : "")}). Access was refused.");
        }

        if (enteredCode == expectedCode)
        {
            // The right digits, from the right phone, with no coaching — and still possibly
            // the wrong person. Possession and knowledge are both transferable; a stolen
            // handset carries the number match, and a researched attacker answers the
            // telemetry questions. This is the only check that asks who actually spoke.
            //
            // Gated on RequiresStepUp rather than on the outcome directly, because that flag
            // already carries VOICE_MODE: it is false for every outcome while observing, so
            // this branch cannot fire until thresholds have been measured against real calls.
            // NotAssessed never sets it, so no enrolled profile, an unreachable scorer, or
            // too little speech all leave the verification exactly as it was.
            if (voice is { RequiresStepUp: true })
            {
                var detail = voice.Outcome == VoiceOutcome.Mismatch
                    ? "the voice on the call did not match the enrolled voice profile"
                    : "the voice on the call could not be confirmed as the enrolled speaker";

                return new Verdict(
                    VerificationResult.BlockedVoiceMismatch,
                    $"The number match was correct, but {detail} "
                    + $"(score {voice.Score:F3}, {voice.Outcome.ToString().ToLowerInvariant()}). "
                    + "Access was refused.");
            }

            return new Verdict(
                VerificationResult.Passed,
                "Number match confirmed on the verification call. No coercion detected.");
        }

        if (attempts >= MaxAttempts)
        {
            return new Verdict(
                VerificationResult.Failed,
                $"Incorrect number match after {attempts} attempts.");
        }

        // Deliberately no hint about how close the entry was — partial feedback would
        // shrink an already small keyspace.
        return new Verdict(null, $"Incorrect entry, attempt {attempts} of {MaxAttempts}.");
    }
}
