using System.Security.Cryptography;

namespace EntraGuard.MediaService.Agents;

/// <summary>
/// The phrases a user repeats while enrolling their voice.
///
/// Chosen at random per enrolment, and that randomness is doing security work rather than
/// decoration. SpeechBrain has no anti-spoofing, so nothing in the model stops a recording
/// being played back at it. What does help is that an attacker cannot have obtained a
/// recording of a phrase they could not predict — so the phrases are drawn fresh each time
/// and never reused within one enrolment.
///
/// Their content matters too. Each is phonetically varied, easy to read aloud once, and
/// long enough to produce two or three seconds of continuous speech: an embedding built
/// from "yes" is a poor description of a voice however good the model is.
/// </summary>
public static class EnrollmentPhrases
{
    private static readonly string[] Bank =
    [
        "My voice is the key to this account and nobody else may use it",
        "The quick brown fox jumps over the lazy dog near the river",
        "Please verify my identity using seven blue mountains and open water",
        "I am reading this sentence aloud to confirm that I am really here",
        "Twelve grey elephants walked slowly across the wide green field",
        "Security matters most when nobody is watching over your shoulder",
        "The northern lights appeared above the quiet harbour last winter",
        "She sells nine silver bracelets on the corner of the market street",
        "A journey of a thousand miles begins with a single careful step",
        "Bright yellow flowers grow beside the old stone bridge in autumn",
        "My grandmother baked bread every Sunday morning without fail",
        "The library closes at eight o'clock on weekdays and six on Saturday",
        "Rain fell steadily on the rooftops throughout the entire evening",
        "Please read this line clearly so the system can hear your voice",
        "Four large ships arrived at the harbour before the storm began",
        "Whispering pines line the road that leads towards the mountain pass",
        "He counted thirty seven stars before the clouds covered them all",
        "The oldest oak tree in the village has stood there for two centuries",
    ];

    /// <summary>
    /// Pick distinct phrases at random.
    /// </summary>
    /// <remarks>
    /// Cryptographic randomness, not <c>Random</c>. A predictable sequence would let someone
    /// who has seen one enrolment know what the next will ask for, which is precisely the
    /// property the randomness exists to deny them.
    /// </remarks>
    public static IReadOnlyList<string> Pick(int count)
    {
        var pool = Bank.ToList();
        var chosen = new List<string>(count);

        for (var i = 0; i < count && pool.Count > 0; i++)
        {
            var index = RandomNumberGenerator.GetInt32(pool.Count);
            chosen.Add(pool[index]);
            pool.RemoveAt(index);
        }

        return chosen;
    }
}
