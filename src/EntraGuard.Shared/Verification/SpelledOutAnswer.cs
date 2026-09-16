namespace EntraGuard.Shared.Verification;

/// <summary>
/// Reads an answer the caller spelled out letter by letter.
///
/// The retry prompt asks callers to "spell out anything unusual, letter by letter", which is
/// the right instruction on a phone line — speech recognition mangles exactly the names and
/// places these questions ask for. It was shipped without anything able to read the result: a
/// caller spelled MALAYSIA, the transcript arrived as separate letters, and the judge compared
/// "M A L A Y S I A" against "Malaysia" and refused it.
///
/// <para>
/// Deterministic rather than left to the judge. A language model would recognise spelling most
/// of the time, and "most of the time" is how a factor starts refusing people who did exactly
/// what it asked them to do.
/// </para>
/// </summary>
public static class SpelledOutAnswer
{
    /// <summary>
    /// Minimum single letters before this counts as spelling rather than an abbreviation.
    ///
    /// Three, so "K L" for Kuala Lumpur and other two-letter forms are left alone — those are
    /// abbreviations a caller means as written, not a word being spelled out.
    /// </summary>
    private const int MinimumLetters = 3;

    /// <summary>
    /// The word the caller spelled, or null if they were not spelling.
    /// </summary>
    /// <remarks>
    /// Null is the important case: it means "judge what they said, unchanged". Anything that
    /// is not plainly a spelling must pass through untouched, because turning a normal answer
    /// into its initials would refuse somebody who answered perfectly well.
    /// </remarks>
    public static string? Collapse(string? spoken)
    {
        if (string.IsNullOrWhiteSpace(spoken))
        {
            return null;
        }

        // Hyphens and full stops are how people punctuate spelling out loud — "m-a-l-a-y-s-i-a"
        // and "M. A. L." are the same act as saying the letters with pauses.
        var tokens = spoken.Split(
            [' ', ',', '.', '-', '\t'], StringSplitOptions.RemoveEmptyEntries);

        var letters = tokens
            .Where(t => t.Length == 1 && char.IsLetter(t[0]))
            .Select(t => char.ToUpperInvariant(t[0]))
            .ToArray();

        // Every token that is not a single letter is lead-in ("it's", "the answer is") or
        // noise. What matters is whether enough of the utterance is bare letters.
        return letters.Length >= MinimumLetters ? new string(letters) : null;
    }
}
