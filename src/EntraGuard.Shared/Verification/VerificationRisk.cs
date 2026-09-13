namespace EntraGuard.Shared.Verification;

/// <summary>How much attention a completed verification deserves.</summary>
public enum RiskBand
{
    /// <summary>Nothing unusual. The overwhelming majority of genuine sign-ins.</summary>
    Low,

    /// <summary>One weak signal. Worth seeing in aggregate, not worth a phone call.</summary>
    Moderate,

    /// <summary>Several weak signals, or one strong one. A human should look.</summary>
    Elevated,

    /// <summary>Strong evidence something was wrong, even if access was granted.</summary>
    High,
}

/// <param name="Score">0-100.</param>
/// <param name="Band">Which bucket <paramref name="Score"/> falls in.</param>
/// <param name="Contributors">
/// What actually drove the number, largest first, phrased for a human. Empty when nothing
/// contributed — which is a meaningful statement, not a missing value.
/// </param>
public readonly record struct VerificationRiskResult(
    double Score, RiskBand Band, IReadOnlyList<string> Contributors);

/// <summary>
/// Scores a completed verification for how much it should worry a security team.
///
/// Distinct from the verdict, and deliberately so. The verdict answers "was this person let
/// in?", which is binary and already decided by <c>VerificationAdjudicator</c>. This answers
/// "how much did this attempt look like an attack?", which is a gradient and is useful
/// precisely for the attempts that PASSED — a sign-in granted after two wrong codes, from an
/// unmanaged browser, with a voice that did not quite match, is not the same event as one
/// answered first time on a managed Teams device, and an audit trail that records both as
/// "Passed" cannot tell them apart.
///
/// <para>
/// It changes nothing. No caller may use this to refuse access; the adjudicator does not see
/// it and has no parameter for it. That is a deliberate constraint rather than an oversight:
/// every weight below is a guess until real calls justify it, and this project has already
/// locked its own owner out once by enforcing numbers measured against synthesised audio.
/// Turning it into a gate is a separate decision that should follow evidence, and the
/// evidence is what this produces.
/// </para>
///
/// <para>
/// Deterministic and model-free, like <c>PolicyGate</c>. A language model deciding how risky a
/// sign-in was would be unauditable and irreproducible, and "why was I flagged?" must have an
/// answer that survives being asked a second time.
/// </para>
/// </summary>
public static class VerificationRisk
{
    /// <summary>Coercion evidence can contribute at most half the scale on its own.</summary>
    private const double CoercionWeight = 0.5;

    public const double ModerateThreshold = 25;
    public const double ElevatedThreshold = 50;
    public const double HighThreshold = 75;

    /// <summary>
    /// Score a verification from signals recorded on the call itself.
    /// </summary>
    /// <param name="peakRisk">
    /// Highest Analyst risk observed while the call was up, already urgency-weighted by the
    /// policy gate. The strongest signal here by some distance: it is the only one derived
    /// from what was actually said, rather than from how the mechanics went.
    /// </param>
    /// <param name="voiceOutcome">Match, Inconclusive, Mismatch or NotAssessed.</param>
    /// <param name="attempts">Code entries consumed. One is the normal case.</param>
    /// <param name="endpointKind">"teams", "phone" or "browser".</param>
    /// <param name="knowledgeAttempts">Spoken answers given. One per question is normal.</param>
    /// <param name="followUps">
    /// Probes asked after an answer that was correct but coarse. Only the unmet ones
    /// contribute, and they are capped below the moderate threshold on purpose: a probe that
    /// cannot refuse anybody must not be able to flag anybody on its own either.
    /// </param>
    public static VerificationRiskResult Score(
        double peakRisk,
        string? voiceOutcome,
        int attempts,
        string? endpointKind,
        int knowledgeAttempts = 0,
        IReadOnlyList<FollowUpOutcome>? followUps = null)
    {
        var contributors = new List<(double Points, string Why)>();

        // ── Coercion, from the transcript ────────────────────────────────────
        if (peakRisk > 0)
        {
            contributors.Add((
                peakRisk * CoercionWeight,
                $"Coercion analysis peaked at {peakRisk:F0}/100 during the call"));
        }

        // ── Voice ────────────────────────────────────────────────────────────
        //
        // NotAssessed scores above zero on purpose. It is not innocence — it means the one
        // check that asks WHO spoke did not run, so this attempt rests entirely on factors a
        // thief of the phone would also satisfy. Small, because the common cause is simply
        // that the user never enrolled.
        var voicePoints = voiceOutcome switch
        {
            "Mismatch" => (30d, "The voice did not match the enrolled profile"),
            "Inconclusive" => (15d, "The voice could not be confirmed as the enrolled speaker"),
            "NotAssessed" => (5d, "No voice comparison was possible on this call"),
            _ => (0d, string.Empty),
        };

        if (voicePoints.Item1 > 0)
        {
            contributors.Add(voicePoints);
        }

        // ── Fumbling the code ────────────────────────────────────────────────
        //
        // The first attempt is free — people mistype. Each one after that is weak evidence of
        // somebody reading digits they cannot see.
        if (attempts > 1)
        {
            var extra = attempts - 1;
            contributors.Add((
                extra * 10d,
                $"The number match took {attempts} attempts"));
        }

        // ── How we reached them ──────────────────────────────────────────────
        //
        // Not a judgement about the user. A Teams identity is a managed, signed-in
        // application on a device the tenant knows; a browser soft-phone is whatever tab
        // happened to be open, and an attacker can open a tab.
        var endpointPoints = endpointKind switch
        {
            "browser" => (10d, "Verified through a browser rather than a managed device"),
            "phone" => (5d, "Verified through a phone endpoint rather than Teams"),
            _ => (0d, string.Empty),
        };

        if (endpointPoints.Item1 > 0)
        {
            contributors.Add(endpointPoints);
        }

        // ── Spoken answers ───────────────────────────────────────────────────
        //
        // Answering at the second attempt is a real signal, and a mild one. People mishear
        // questions on bad lines, which is exactly why a retry exists at all.
        if (knowledgeAttempts > 1)
        {
            contributors.Add((
                (knowledgeAttempts - 1) * 5d,
                $"Identity questions needed {knowledgeAttempts} answers"));
        }

        // ── Follow-up probes ─────────────────────────────────────────────────
        //
        // Capped at 20 against a moderate threshold of 25, so probes alone never move a call
        // out of Low however many are missed. That cap is the point rather than a detail: a
        // probe follows a question the caller has ALREADY answered correctly, so failing one
        // cannot mean they are the wrong person — it means they could not recall a detail,
        // which is what people do. The signal is worth having in company and worth nothing
        // by itself.
        //
        // Facets rather than questions, and distinct, because two device probes are composed
        // for every call — which half the caller left unsaid is not known until they speak —
        // and "device or device" is not a sentence anybody should have to read.
        var unmet = followUps?.Where(f => !f.Correct).Select(f => f.Facet).Distinct().ToList()
            ?? [];

        if (unmet.Count > 0)
        {
            contributors.Add((
                Math.Min(unmet.Count * 10d, 20d),
                $"Could not confirm {string.Join(" or ", unmet)} when asked for more detail"));
        }

        var score = Math.Clamp(contributors.Sum(c => c.Points), 0, 100);

        var band = score switch
        {
            >= HighThreshold => RiskBand.High,
            >= ElevatedThreshold => RiskBand.Elevated,
            >= ModerateThreshold => RiskBand.Moderate,
            _ => RiskBand.Low,
        };

        return new VerificationRiskResult(
            score,
            band,
            contributors
                .OrderByDescending(c => c.Points)
                .Select(c => c.Why)
                .ToList());
    }
}
