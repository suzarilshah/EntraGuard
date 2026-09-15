namespace EntraGuard.Shared.Verification;

/// <summary>
/// How much a verification actually established about the caller.
/// </summary>
/// <remarks>
/// EntraGuard's own scale, deliberately NOT labelled AAL or eIDAS. Those names carry
/// conformance claims nobody has assessed, and borrowing them to sound rigorous is the kind
/// of thing that reads well in a deck and misleads whoever has to rely on it.
/// </remarks>
public enum AssuranceLevel
{
    /// <summary>The verification did not pass. Nothing was established.</summary>
    None,

    /// <summary>
    /// Possession only: the number match was the entire check.
    ///
    /// Reached when no knowledge question could be asked — the tenant withholds sign-in logs
    /// or has no Entra ID P1, and the user never registered a question. Access is still
    /// granted, because refusing here would lock out every user who never enrolled, and that
    /// is a decision for the relying party rather than for this scale.
    /// </summary>
    Low,

    /// <summary>
    /// Possession and knowledge, but of one kind and uncorroborated.
    ///
    /// Either a stored secret — researchable, permanent, possibly already breached — or live
    /// telemetry with no second signal behind it.
    /// </summary>
    Substantial,

    /// <summary>
    /// Possession, live telemetry the caller lived through, and an independent second signal:
    /// a confirmed follow-up probe, or a voice that matched the enrolled speaker.
    /// </summary>
    High,
}

/// <param name="Level">See <see cref="AssuranceLevel"/>.</param>
/// <param name="Basis">What was actually established, in plain language.</param>
/// <param name="Gaps">
/// What would have raised the level. Empty means nothing was missing, which is a statement
/// rather than an absence of one.
/// </param>
public readonly record struct AssuranceResult(
    AssuranceLevel Level, IReadOnlyList<string> Basis, IReadOnlyList<string> Gaps);

/// <summary>
/// Reports what a verification proved. Decides nothing.
///
/// <see cref="VerificationRisk"/> answers "how much did this attempt look like an attack?".
/// This answers the question a relying party actually has to act on: "how much did you
/// establish?" — because <c>Passed</c> currently covers both a call that answered live
/// telemetry questions and matched an enrolled voice, and a call where two keyed digits were
/// the whole check.
///
/// <para>
/// It has no authority, on purpose and by the same argument as everything else here.
/// <c>VerificationAdjudicator</c> issues the verdict and never sees this; <c>GrantsAccess</c>
/// is unchanged. A relying party may refuse a <see cref="AssuranceLevel.Low"/> call for a
/// large transfer — that is its policy, taken with information it does not have today.
/// </para>
///
/// <para>
/// Deterministic and model-free, like <c>PolicyGate</c>. "Why was this only Substantial?"
/// must have an answer that survives being asked a second time.
/// </para>
/// </summary>
public static class VerificationAssurance
{
    /// <param name="passed">Whether the verification granted access.</param>
    /// <param name="knowledgeBacking">
    /// Where the questions came from: "telemetry", "telemetry+directory", "telemetry+table",
    /// "directory", "table", or null when nothing was asked.
    /// </param>
    /// <param name="followUps">Probes asked after a correct answer.</param>
    /// <param name="voiceOutcome">Match, Inconclusive, Mismatch or NotAssessed.</param>
    /// <param name="endpointKind">"teams", "phone" or "browser". Reported, never scored.</param>
    public static AssuranceResult Evaluate(
        bool passed,
        string? knowledgeBacking,
        IReadOnlyList<FollowUpOutcome>? followUps,
        string? voiceOutcome,
        string? endpointKind)
    {
        if (!passed)
        {
            return new AssuranceResult(AssuranceLevel.None, [], []);
        }

        var basis = new List<string> { "The number match confirmed the phone and the browser are the same person" };
        var gaps = new List<string>();

        // Live telemetry is the only backing that can reach High. A stored secret cannot,
        // however well everything else went — see the corroboration rule below.
        var liveTelemetry = knowledgeBacking?.StartsWith("telemetry", StringComparison.OrdinalIgnoreCase) == true;
        var anyKnowledge = !string.IsNullOrWhiteSpace(knowledgeBacking);

        if (liveTelemetry)
        {
            basis.Add("Questions were built from the caller's own sign-in activity, minutes old");
        }
        else if (anyKnowledge)
        {
            basis.Add("A registered question was answered");
            gaps.Add("No live sign-in telemetry was available, so the question was a stored secret "
                   + "rather than something an attacker could not have researched");
        }
        else
        {
            gaps.Add("No identity question was asked at all — the tenant returned no sign-in "
                   + "telemetry and the user has no registered question");
        }

        // Corroboration: an independent second signal, of a different kind from the questions.
        //
        // A confirmed probe qualifies because it was invented DURING the call, so it cannot
        // have been researched beforehand, pre-recorded, or answered by somebody who joined
        // late. A voice match qualifies because it speaks to WHO was talking rather than to
        // what they knew.
        var probeConfirmed = followUps?.Any(f => f.Correct) == true;
        var voiceMatched = string.Equals(voiceOutcome, "Match", StringComparison.OrdinalIgnoreCase);

        if (probeConfirmed)
        {
            basis.Add("A follow-up invented during the call was answered correctly");
        }

        if (voiceMatched)
        {
            basis.Add("The voice matched the enrolled speaker");
        }

        if (!probeConfirmed && !voiceMatched)
        {
            gaps.Add("Nothing corroborated the answers — no follow-up was confirmed and the "
                   + "voice was not matched against an enrolled profile");
        }

        // Stacking two weak-in-different-ways signals into the top level is how a scale stops
        // meaning anything, so High requires the STRONG question source specifically. A voice
        // match cannot promote a researchable secret into a fact the caller lived through.
        var level = liveTelemetry && (probeConfirmed || voiceMatched)
            ? AssuranceLevel.High
            : anyKnowledge
                ? AssuranceLevel.Substantial
                : AssuranceLevel.Low;

        if (!string.IsNullOrWhiteSpace(endpointKind) && endpointKind != "teams")
        {
            basis.Add($"Delivered to a {endpointKind} endpoint rather than a managed Teams client");
        }

        return new AssuranceResult(level, basis, gaps);
    }
}
