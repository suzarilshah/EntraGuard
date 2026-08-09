using System.Security.Cryptography;
using System.Text;

namespace EntraGuard.Shared.Verification;

/// <summary>
/// One registered knowledge question and the hash of its answer.
/// </summary>
/// <param name="Question">Shown at registration and spoken on the call.</param>
/// <param name="Salt">Per-answer salt, base64.</param>
/// <param name="AnswerHash">Base64 PBKDF2 hash of the normalised answer.</param>
public sealed record KnowledgeQuestion(string Question, string Salt, string AnswerHash);

/// <summary>
/// Matching for spoken knowledge answers.
///
/// The answers are never stored in a form anything can read back — not by an administrator,
/// not by this service, not by whoever gets hold of the store. That mirrors how Entra
/// treats its own security questions, and it is the only defensible way to hold something a
/// user picked: people reuse these answers across systems, so a readable copy here is a
/// credential leak waiting for a breach.
///
/// The consequence is that matching has to happen against the hash, which means the
/// normalisation below IS the matching rule. It is deliberately generous about how speech
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

    /// <summary>Register an answer. The plaintext is not retained anywhere.</summary>
    public static KnowledgeQuestion Register(string question, string answer)
    {
        var normalized = Normalize(answer);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("The answer is empty once normalised.", nameof(answer));
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        return new KnowledgeQuestion(question.Trim(), Convert.ToBase64String(salt), Hash(normalized, salt));
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
