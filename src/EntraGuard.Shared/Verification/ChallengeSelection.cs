using System.Security.Cryptography;

namespace EntraGuard.Shared.Verification;

/// <summary>Where a fact came from, and therefore how much it is worth.</summary>
public enum FactSource
{
    /// <summary>Sign-in activity. Minutes old, expires by itself, unresearchable.</summary>
    SignIn,

    /// <summary>Calendar, mail, chat, files. Recent and unpredictable.</summary>
    Activity,

    /// <summary>
    /// Manager, office, department, title.
    ///
    /// Weaker than it looks, and the reason the selection below refuses to build a challenge
    /// out of these alone: they are stable for years and most of them are on the caller's
    /// public professional profile. An attacker who researched the target before ringing the
    /// help desk — which is the entire Scattered Spider playbook this product exists to
    /// defeat — arrives already knowing them.
    /// </summary>
    Directory,
}

/// <param name="Facet">Coarse category. At most one question per facet reaches a call.</param>
/// <param name="Strength">
/// How much a correct answer is worth, 1-3. Used to order, never to decide: the caller is not
/// told, and the adjudicator does not see it.
/// </param>
public sealed record ChallengeCandidate(
    string Facet,
    string Question,
    IReadOnlyList<string> ExpectedFacts,
    FactSource Source,
    int Strength);

/// <summary>
/// Picks which questions a call asks, and decides how many must be answered.
///
/// Pure and free of I/O, for the same reason <c>PolicyGate</c> and <c>ConversationDirector</c>
/// are: this decides who is let in, and a rule nobody can test exhaustively is a claim rather
/// than a control.
/// </summary>
public static class ChallengeSelection
{
    /// <summary>Most callers should hear four questions; three is the floor worth asking.</summary>
    public const int DefaultCount = 4;

    /// <summary>
    /// Choose the questions for one call.
    ///
    /// <para>
    /// Random, because a fixed set is a set an attacker can prepare for. Somebody who has
    /// researched the target and rehearsed "who is your manager" should not know that the
    /// manager question is even coming, let alone that it is the only one.
    /// </para>
    ///
    /// <para>
    /// Two rules constrain the randomness, and both exist because the obvious version is
    /// weaker than it looks. At most one question per facet, so a call cannot become three
    /// rephrasings of "where were you". And at least one question from a source that expires
    /// — sign-in activity, calendar, mail — because a challenge built entirely from directory
    /// facts asks only things a prepared attacker already read on LinkedIn.
    /// </para>
    /// </summary>
    /// <param name="pool">Everything that could be asked. Order is ignored.</param>
    /// <param name="count">How many to ask. Clamped to what the pool can honestly supply.</param>
    /// <param name="shuffle">
    /// Injected so tests are deterministic. Production passes a CSPRNG shuffle — a predictable
    /// question order is the thing this whole method exists to prevent.
    /// </param>
    public static IReadOnlyList<ChallengeCandidate> Select(
        IReadOnlyList<ChallengeCandidate> pool,
        int count = DefaultCount,
        Func<IReadOnlyList<ChallengeCandidate>, IReadOnlyList<ChallengeCandidate>>? shuffle = null)
    {
        if (pool.Count == 0 || count <= 0)
        {
            return [];
        }

        shuffle ??= CryptographicShuffle;

        // One per facet. Which one survives is itself random, so a caller who hears the
        // location question twice across two calls has not learned the order.
        var byFacet = shuffle(pool)
            .GroupBy(c => c.Facet, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var chosen = new List<ChallengeCandidate>();

        // Seat an expiring fact first, if one exists at all. Doing this before the random
        // fill rather than checking afterwards means the guarantee holds even when the pool
        // is mostly directory facts, which is exactly when it matters.
        var expiring = shuffle(byFacet.Where(c => c.Source != FactSource.Directory).ToList());
        if (expiring.Count > 0)
        {
            chosen.Add(expiring[0]);
        }

        foreach (var candidate in shuffle(byFacet))
        {
            if (chosen.Count >= count)
            {
                break;
            }

            if (!chosen.Contains(candidate))
            {
                chosen.Add(candidate);
            }
        }

        // Strongest first. A caller who is going to fail should fail on the question that
        // actually proves something, while they are still concentrating.
        return chosen.OrderByDescending(c => c.Strength).ToList();
    }

    /// <summary>
    /// How many of the questions asked must be answered correctly.
    /// </summary>
    /// <remarks>
    /// One miss is forgiven once three or more are asked, and the reason is asymmetry rather
    /// than generosity. Asking four and requiring four is a HARDER challenge than asking two
    /// and requiring two — every extra question is another chance for a real person to blank
    /// on where they were on Tuesday. Refusing the account owner is the expensive failure
    /// here, and it has already happened repeatedly.
    ///
    /// It is not a weakening: a guesser now has to be right three times instead of twice.
    /// Two or fewer questions require all of them, because "most of two" is one, and one
    /// correct answer out of two is a coin toss.
    /// </remarks>
    public static int Required(int asked) => asked <= 2 ? asked : asked - 1;

    /// <summary>
    /// Fisher-Yates over a cryptographic RNG.
    ///
    /// Not <c>Random</c>: the order of these questions is a security property, and a
    /// predictable sequence hands a prepared attacker the one thing this randomisation
    /// exists to deny them.
    /// </summary>
    private static IReadOnlyList<ChallengeCandidate> CryptographicShuffle(
        IReadOnlyList<ChallengeCandidate> source)
    {
        var items = source.ToArray();

        for (var i = items.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }

        return items;
    }

    /// <summary>Has the caller done enough, given what they have answered so far?</summary>
    /// <returns>
    /// True to pass, false to refuse, null while it is still undecided and there are questions
    /// left to ask.
    /// </returns>
    public static bool? Verdict(int asked, int correct, int wrong)
    {
        var required = Required(asked);

        if (correct >= required)
        {
            return true;
        }

        // Refuse as soon as passing has become arithmetically impossible, rather than asking
        // questions whose answers cannot change the outcome. Being interrogated after the
        // decision is made is its own small cruelty, and it lengthens a call that is already
        // too long.
        return wrong > asked - required ? false : null;
    }
}
