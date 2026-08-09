using System.Security.Cryptography;
using System.Text;

namespace EntraGuard.Shared.Verification;

/// <summary>
/// One registered knowledge question and the hash of its answer.
/// </summary>
/// <param name="Question">Shown at registration and spoken on the call.</param>
/// <param name="Salt">Per-answer salt, base64.</param>
/// <param name="AnswerHash">Base64 PBKDF2 hash of the normalised answer.</param>
/// <param name="PlainAnswer">
/// The answer in the clear, kept ONLY so a language model can judge whether what the user
/// said means the same thing.
///
/// This is a real reduction in security and is named rather than buried: a salted hash
/// cannot be read back by anyone, and this can. It exists because exact matching refuses
/// real people — "Saint Mary's" for "St Mary's", "Volkswagen" for "VW" — and a factor that
/// refuses correct users gets turned off.
///
/// The hash is still checked first and still decides the common case. This is only read
/// when the hash fails, and only inside the media service; the conversational agent on the
/// call is never given it.
/// </param>
public sealed record KnowledgeQuestion(
    string Question, string Salt, string AnswerHash, string? PlainAnswer = null);

/// <summary>
/// Matching for spoken knowledge answers.
///
/// The hash is the first and usual check, and it decides most calls on its own. It is a
/// salted PBKDF2 digest that nothing can read back, which is how a user-chosen secret
/// should be held — people reuse these answers across systems.
///
/// A readable copy is ALSO kept, so a language model can judge whether what someone said
/// means the same thing. That is a genuine weakening and is documented on
/// <see cref="KnowledgeQuestion.PlainAnswer"/> rather than hidden here; it exists because
/// exact matching refused people who had answered correctly.
///
/// Matching against the hash means the normalisation below IS the exact-match rule. It is deliberately generous about how speech
/// recognition renders an answer and deliberately strict about the answer itself: case,
/// punctuation, articles and surrounding filler are discarded, nothing else is.
/// </summary>
public static class KnowledgeChallenge
{
    private const int Iterations = 100_000;
    private const int HashBytes = 32;
    private const int SaltBytes = 16;

    /// <summary>
    /// Words a speaker adds without meaning to. "Um, it was Bluebell I think" and
    /// "Bluebell" are the same answer, and failing the first one teaches users to distrust
    /// the system rather than teaching them to speak more clearly.
    /// </summary>
    private static readonly string[] Filler =
    [
        "um", "uh", "er", "erm", "well", "so", "like", "i", "think", "it", "its", "it's",
        "was", "is", "the", "a", "an", "my", "our", "that", "would", "be", "answer",
        "hmm", "okay", "ok", "yeah", "yes", "please",
    ];

    /// <summary>
    /// Reduce a spoken or typed answer to the token that gets hashed.
    /// </summary>
    public static string Normalize(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return string.Empty;
        }

        var cleaned = new StringBuilder(answer.Length);
        foreach (var ch in answer)
        {
            if (char.IsLetterOrDigit(ch))
            {
                cleaned.Append(char.ToLowerInvariant(ch));
            }
            else if (char.IsWhiteSpace(ch) || ch is '\'' or '-')
            {
                // Apostrophes and hyphens join a word rather than separate it, so they
                // collapse away instead of splitting "o'brien" into two tokens.
                if (char.IsWhiteSpace(ch)) cleaned.Append(' ');
            }
        }

        var words = cleaned.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !Filler.Contains(w))
            .ToArray();

        // Joined with NOTHING, so word boundaries are not load-bearing.
        //
        // This is a deliberate trade, and it costs something: "blue bell" now matches
        // "Bluebell". The reason is that in a spoken challenge the speaker does not choose
        // the word boundaries — the recogniser does. "Mary-Anne" comes back as two words,
        // "New York" sometimes as one, and a user who answered correctly cannot tell why
        // they were refused or do anything differently next time. A factor that fails
        // people at random is one they stop trusting.
        //
        // The security cost is close to nil: an attacker guessing the answer gains nothing
        // from spacing, because they still have to know the word.
        //
        // Everything was filler: the user said something, but nothing that identifies an
        // answer. Empty is correct — and never matches, because Verify rejects it outright.
        return string.Concat(words);
    }

    /// <summary>
    /// Register an answer: a salted hash for exact matching, plus the text itself for the
    /// semantic judge. See <see cref="KnowledgeQuestion.PlainAnswer"/> for why.
    /// </summary>
    public static KnowledgeQuestion Register(string question, string answer)
    {
        var normalized = Normalize(answer);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("The answer is empty once normalised.", nameof(answer));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return new KnowledgeQuestion(
            question.Trim(),
            Convert.ToBase64String(salt),
            Hash(normalized, salt),
            answer.Trim());
    }

    /// <summary>
    /// Check a spoken answer against a registered question.
    /// </summary>
    /// <remarks>
    /// Fixed-time comparison. The margin is small on one 32-byte hash, but a verification
    /// factor that leaks how much of an answer was right is a factor that can be walked.
    /// </remarks>
    public static bool Verify(KnowledgeQuestion registered, string spoken)
    {
        var normalized = Normalize(spoken);
        if (normalized.Length == 0)
        {
            return false;
        }

        byte[] salt;
        try
        {
            salt = Convert.FromBase64String(registered.Salt);
        }
        catch (FormatException)
        {
            return false;
        }

        var candidate = Hash(normalized, salt);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidate),
            Encoding.UTF8.GetBytes(registered.AnswerHash));
    }

    private static string Hash(string normalized, byte[] salt) =>
        Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(normalized), salt, Iterations, HashAlgorithmName.SHA256, HashBytes));
}
