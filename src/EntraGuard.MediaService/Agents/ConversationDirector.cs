namespace EntraGuard.MediaService.Agents;

/// <summary>
/// Decides whether the verification call goes deeper, and with which question.
///
/// The conversational agent renders what it is handed. It does not choose to keep somebody
/// on the phone and it does not choose what to ask them, for the same reason the Analyst
/// does not choose to revoke a session: the model proposes wording, a deterministic thing
/// disposes of structure. A chatty agent widens the attack surface, so it needs that rule
/// more than a scripted one did, not less.
///
/// Pure and free of I/O so it can be exhaustively tested, because a rule nobody can test is
/// a claim rather than a control — the same relationship <c>VoiceGuardrail</c> has to the
/// agent's speech and <c>PolicyGate</c> has to the Analyst's proposals.
/// </summary>
public static class ConversationDirector
{
    /// <summary>
    /// Probes allowed on a call that has given no cause for concern.
    /// </summary>
    /// <remarks>
    /// One, not zero. The location probe earns its place on every call: a country-only
    /// answer is weak evidence whether or not anything else about the call looks wrong, and
    /// waiting for the Analyst to worry first would mean the commonest weak answer is never
    /// improved on.
    /// </remarks>
    public const int CalmBudget = 1;

    /// <summary>
    /// Probes allowed once the Analyst is worried.
    ///
    /// Two, not more. Rising risk buys more conversation rather than colder conversation —
    /// another question reads as ordinary thoroughness, and every extra second of speech
    /// feeds the coercion analysis. But a genuine user's call must still end in about the
    /// time they expect, so the budget stops here rather than growing with the score.
    /// </remarks>
    public const int ElevatedBudget = 2;

    public static int Budget(bool riskElevated) => riskElevated ? ElevatedBudget : CalmBudget;

    /// <summary>
    /// The next probe to ask, or null to stop.
    /// </summary>
    /// <param name="candidates">Probes composed for this call. Order is not significant.</param>
    /// <param name="heard">
    /// What the caller has said so far. Probes whose ground they have already covered are
    /// skipped: asking for something somebody just told you reads as not listening.
    /// </param>
    /// <param name="facetsAsked">Facets already probed. One probe per facet, at most.</param>
    /// <param name="riskElevated">Whether the Analyst has crossed the elevated threshold.</param>
    public static FollowUpProbe? NextProbe(
        IReadOnlyList<FollowUpProbe> candidates,
        string heard,
        IReadOnlyCollection<string> facetsAsked,
        bool riskElevated)
    {
        if (facetsAsked.Count >= Budget(riskElevated))
        {
            return null;
        }

        return candidates
            .Where(p => !facetsAsked.Contains(p.Facet))
            .Where(p => !Covers(heard, p.AlreadyCovered))
            .OrderByDescending(p => p.Strength)
            .FirstOrDefault();
    }

    /// <summary>
    /// Has the caller already said one of these facts?
    /// </summary>
    /// <remarks>
    /// Substring rather than equality, and deliberately generous. People answer in sentences
    /// — "I was in Petaling Jaya this morning" — and the two mistakes do not cost the same.
    /// Skipping a probe that could have been asked loses one weak signal. Asking for
    /// something the caller has just said makes the call sound like it was not listening,
    /// which is the exact impression this whole feature exists to remove.
    /// </remarks>
    internal static bool Covers(string heard, IReadOnlyList<string> facts) =>
        !string.IsNullOrWhiteSpace(heard)
        && facts.Any(f => heard.Contains(f, StringComparison.OrdinalIgnoreCase));
}
