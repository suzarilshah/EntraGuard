using System.Text.RegularExpressions;

namespace EntraGuard.Shared.Voice;

/// <summary>What the guardrail decided about one piece of model output.</summary>
/// <param name="Allowed">False means this must not be spoken.</param>
/// <param name="Violation">Machine-readable reason, for the audit trail.</param>
/// <param name="Replacement">Safe text to speak instead, when refusing.</param>
public sealed record GuardrailVerdict(bool Allowed, string? Violation = null, string? Replacement = null)
{
    public static readonly GuardrailVerdict Ok = new(true);
}

/// <summary>
/// The deterministic gate between the voice model and the caller's ear.
///
/// A conversational agent on an authentication call is a genuinely dangerous thing to
/// build. It is a machine that can be talked to, by definition, by whoever picked up —
/// including an attacker who has the victim beside them. Everything it says is heard by
/// them, and everything they say enters its context. So the design rule is that the model
/// is never trusted with anything worth stealing and never trusted with any decision:
///
///   * It is never given the match code, the knowledge answer, or the answer hash. It
///     cannot leak what it does not hold. This is the only leak defence that actually
///     holds, because a prompt instruction not to reveal a secret is a request, not a
///     control.
///
///   * It does not issue the verdict. It conducts a conversation; the deterministic
///     adjudicator decides. No sentence the model can be induced to say grants access.
///
///   * Every utterance passes through this class before it is spoken. Prompt text is a
///     preference; this is a gate — the same relationship the Policy Gate has to the
///     Analyst elsewhere in this system.
///
/// This class is pure and has no I/O so it can be exhaustively tested, which is the point:
/// a guardrail nobody can test is a claim, not a control.
/// </summary>
public static class VoiceGuardrail
{
    /// <summary>Spoken instead of anything refused. Deliberately dull and non-committal.</summary>
    public const string SafeFallback =
        "I can only help with this verification. Please enter the number shown on your screen.";

    /// <summary>
    /// Digit sequences the agent must never say, in words or numerals.
    ///
    /// The match code is displayed on the user's screen and keyed on the phone; an agent
    /// that reads it aloud has converted a possession factor into something anyone within
    /// earshot can satisfy — which is the exact attack this product exists to stop.
    /// </summary>
    private static readonly Dictionary<char, string> DigitWords = new()
    {
        ['0'] = "zero", ['1'] = "one", ['2'] = "two", ['3'] = "three", ['4'] = "four",
        ['5'] = "five", ['6'] = "six", ['7'] = "seven", ['8'] = "eight", ['9'] = "nine",
    };

    /// <summary>
    /// Phrases that indicate the model has been steered off the verification task.
    ///
    /// Deliberately about EFFECT, not topic. An agent discussing the weather is harmless;
    /// an agent announcing an outcome, offering to bypass a step, or claiming authority it
    /// does not have is not, regardless of how politely it got there.
    /// </summary>
    private static readonly string[] ForbiddenClaims =
    [
        "access granted", "you are verified", "you're verified", "verification successful",
        "verification complete", "i have approved", "i've approved", "i can approve",
        "i'll approve", "i will approve", "skip the", "bypass", "you can ignore",
        "no need to enter", "don't worry about the number", "i can let you in",
        "i'll let you through", "override", "as an ai", "ignore previous",
        "system prompt", "my instructions",
    ];

    /// <summary>
    /// Check one model utterance before it is spoken.
    /// </summary>
    /// <param name="text">Exactly what the model intends to say.</param>
    /// <param name="matchCode">The code on the user's screen. Never spoken, in any form.</param>
    /// <param name="knowledgeAnswerIsSecret">
    /// True when a knowledge answer exists. The agent asks the question; it must never
    /// utter anything resembling the answer, so the answer is never in its context at all —
    /// this flag only enables the extra checks that catch a lucky guess.
    /// </param>
    public static GuardrailVerdict Inspect(string text, string matchCode, bool knowledgeAnswerIsSecret = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            // Silence is not a violation, but it is not speech either.
            return new GuardrailVerdict(false, "empty", null);
        }

        var lowered = text.ToLowerInvariant();

        // 1. The match code, in numerals or words, contiguous or spaced.
        if (!string.IsNullOrEmpty(matchCode) && ContainsCode(lowered, matchCode))
        {
            return new GuardrailVerdict(false, "spoke_match_code", SafeFallback);
        }

        // 2. Claims of authority or outcome the model does not have.
        foreach (var phrase in ForbiddenClaims)
        {
            if (lowered.Contains(phrase, StringComparison.Ordinal))
            {
                return new GuardrailVerdict(false, $"forbidden_claim:{phrase.Replace(' ', '_')}", SafeFallback);
            }
        }

        // 3. Length. A verification agent that monologues is either malfunctioning or being
        // used as a channel for something else, and either way the user stops listening.
        if (text.Length > 400)
        {
            return new GuardrailVerdict(false, "too_long", SafeFallback);
        }

        // 4. A registered knowledge question means the answer is a secret in play. The model
        // never holds it, but it must also not fish for it by offering candidates.
        if (knowledgeAnswerIsSecret && LooksLikeOfferingAnAnswer(lowered))
        {
            return new GuardrailVerdict(false, "offered_an_answer", SafeFallback);
        }

        return GuardrailVerdict.Ok;
    }

    /// <summary>
    /// Does this text contain the code, however it is written?
    /// </summary>
    private static bool ContainsCode(string lowered, string matchCode)
    {
        var digits = matchCode.Where(char.IsAsciiDigit).ToArray();
        if (digits.Length == 0)
        {
            return false;
        }

        // Numerals, contiguous OR separated: "42", "4 2", "4-2", "4, 2".
        //
        // The separator is zero-or-more, so this covers the contiguous case too — a plain
        // substring check would additionally fire on "42" inside "1423", which reveals
        // nothing and would push whoever tunes this to loosen the whole rule. The digit
        // boundaries are what keep it precise.
        var spaced = string.Join(@"[\s\-,\.]*", digits.Select(d => Regex.Escape(d.ToString())));
        if (Regex.IsMatch(lowered, $@"(?<!\d){spaced}(?!\d)", RegexOptions.None, TimeSpan.FromMilliseconds(100)))
        {
            return true;
        }

        // Words: "four two", "four-two". A speech model asked to read a code aloud will
        // usually spell it in words, which a numeral-only check misses entirely.
        var words = string.Join(@"[\s\-,\.]+", digits.Select(d => DigitWords[d]));
        return Regex.IsMatch(lowered, $@"\b{words}\b", RegexOptions.None, TimeSpan.FromMilliseconds(100));
    }

    /// <summary>
    /// Is the model proposing an answer rather than asking for one?
    /// </summary>
    private static bool LooksLikeOfferingAnAnswer(string lowered) =>
        lowered.Contains("is it ", StringComparison.Ordinal)
        || lowered.Contains("was it ", StringComparison.Ordinal)
        || lowered.Contains("did you mean", StringComparison.Ordinal)
        || lowered.Contains("for example", StringComparison.Ordinal)
        || lowered.Contains("such as", StringComparison.Ordinal);
}
