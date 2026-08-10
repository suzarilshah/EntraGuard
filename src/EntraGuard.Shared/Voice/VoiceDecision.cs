namespace EntraGuard.Shared.Voice;

/// <summary>What a voice score means for a verification.</summary>
public enum VoiceOutcome
{
    /// <summary>No profile, no usable audio, or the scorer was unreachable.</summary>
    NotAssessed,

    /// <summary>The voice matches the enrolled template.</summary>
    Match,

    /// <summary>Neither clearly the enrolled speaker nor clearly not. Step up.</summary>
    Inconclusive,

    /// <summary>Clearly not the enrolled speaker. Step up; deny only if that also fails.</summary>
    Mismatch,
}

/// <param name="Outcome">What to do about it.</param>
/// <param name="Score">Cosine similarity, or null when nothing was scored.</param>
/// <param name="Reason">Human-readable, for the audit row.</param>
/// <param name="RequiresStepUp">
/// True when the relying party must force interactive re-authentication before granting.
/// </param>
public sealed record VoiceDecision(
    VoiceOutcome Outcome, double? Score, string Reason, bool RequiresStepUp)
{
    public static readonly VoiceDecision NotAssessed =
        new(VoiceOutcome.NotAssessed, null, "No voice profile was compared.", false);
}

/// <summary>
/// Turns a similarity score into a decision.
///
/// Three bands, not two, and that is the whole design. A speaker model over a phone codec
/// produces overlapping distributions for genuine users and impostors — published equal
/// error rates come from studio recordings and do not survive contact with Teams audio, an
/// accent, or a noisy room. A single threshold forces every ambiguous call into a wrong
/// answer: either a stranger walks in or the account's owner is locked out of their own
/// money.
///
/// So the middle band does not decide. It escalates to something that can: interactive
/// re-authentication through Entra, where a passkey or Authenticator prompt settles it.
/// Voice narrows the question; it never answers it alone.
/// </summary>
public static class VoiceThresholds
{
    /// <summary>Above this, accept.</summary>
    /// <remarks>
    /// Measured, not inherited. A calibration run over five Azure neural voices — three
    /// sentences each, genuine pairs being one voice against itself on DIFFERENT sentences —
    /// produced genuine scores of 0.652 to 0.865 and impostor scores of -0.039 to 0.297.
    /// Accept sits just under the weakest genuine pair.
    ///
    /// Optimistic, and knowingly so: synthesised voices are cleaner than telephony and more
    /// distinct from each other than two colleagues with the same accent. The margin will
    /// narrow on real calls, which is why enforcement stays off until scores from real
    /// verifications say otherwise.
    /// </remarks>
    public const double DefaultAccept = 0.60;

    /// <summary>Below this, treat as a different speaker.</summary>
    /// <remarks>
    /// Just above the strongest measured impostor (0.297). Anything between this and
    /// <see cref="DefaultAccept"/> is the band that steps up rather than deciding.
    /// </remarks>
    public const double DefaultReject = 0.35;

    /// <summary>
    /// Minimum speech needed before a score is worth anything.
    ///
    /// ECAPA returns a vector for any input. Two seconds of "yes" produces a number that
    /// looks exactly as authoritative as one from ten seconds of speech and is far less
    /// reliable, so short audio is reported as NotAssessed rather than scored badly.
    /// </summary>
    public const double MinimumSeconds = 3.0;

    /// <summary>
    /// Decide, given a score and how much audio produced it.
    /// </summary>
    /// <param name="enforce">
    /// False while calibrating: the score is computed and recorded, but every outcome is
    /// reported as not requiring step-up. Thresholds derived from someone else's dataset
    /// would refuse real users, so they are proven against real calls before they bite.
    /// </param>
    public static VoiceDecision Evaluate(
        double? score,
        double seconds,
        bool enforce,
        double accept = DefaultAccept,
        double reject = DefaultReject)
    {
        if (score is null)
        {
            return VoiceDecision.NotAssessed;
        }

        if (seconds < MinimumSeconds)
        {
            return new VoiceDecision(
                VoiceOutcome.NotAssessed, score,
                $"Only {seconds:F1}s of speech — too little to judge a voice on.", false);
        }

        var (outcome, reason) = score switch
        {
            var s when s >= accept => (
                VoiceOutcome.Match,
                $"Voice matches the enrolled profile (score {score:F3})."),
            var s when s <= reject => (
                VoiceOutcome.Mismatch,
                $"Voice does not match the enrolled profile (score {score:F3})."),
            _ => (
                VoiceOutcome.Inconclusive,
                $"Voice match was inconclusive (score {score:F3})."),
        };

        // Only ever a trigger for a stronger check, never a denial in its own right.
        var stepUp = enforce && outcome is VoiceOutcome.Inconclusive or VoiceOutcome.Mismatch;

        return new VoiceDecision(
            outcome, score,
            enforce ? reason : reason + " Observing only; this did not affect the outcome.",
            stepUp);
    }
}
