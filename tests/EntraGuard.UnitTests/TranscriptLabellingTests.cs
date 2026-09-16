using EntraGuard.Shared.Detection;
using EntraGuard.Shared.Sessions;
using FluentAssertions;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// Who the Analyst thinks is talking.
///
/// This exists because of the most expensive labelling bug in this system so far. EntraGuard's
/// own voice went into the transcript as Unknown, and an unidentified speaker telling somebody
/// to key a two-digit code is the textbook signature of the vishing the Analyst hunts. It duly
/// found it — in EntraGuard.
///
/// Measured across five live calls: the coercion score tracked how much EntraGuard itself had
/// spoken. Zero on a call it never spoke on. Seventy and seventy-five on two calls where the
/// user was alone in the room, against a threshold of sixty that blocks access.
/// </summary>
public class TranscriptLabellingTests
{
    private static CallSession Session() => new()
    {
        SessionId = "s1",
        StartedAt = DateTimeOffset.UtcNow,
    };

    private static Utterance Said(SpeakerRole who, string text) =>
        new(who, text, 0, DateTimeOffset.UtcNow, true);

    [Fact]
    public void EntraGuards_own_voice_is_never_labelled_UNKNOWN()
    {
        var session = Session();
        session.AddUtterance(Said(SpeakerRole.Agent,
            "Please enter the two digit number shown on your screen."));

        var transcript = session.TranscriptWindow(TimeSpan.FromMinutes(5), 1000);

        transcript.Should().NotContain("[UNKNOWN]");
        transcript.Should().Contain("[VERIFICATION SYSTEM]");
    }

    [Fact]
    public void The_system_is_distinguishable_from_a_third_party_on_the_line()
    {
        // The distinction the whole fix rests on. Both sentences instruct the user to enter a
        // code; one is the product working and one is an attack. If they render the same, the
        // Analyst cannot tell them apart and neither can anyone reading the audit trail.
        var session = Session();
        session.AddUtterance(Said(SpeakerRole.Agent, "Enter the two digit number on your screen."));
        session.AddUtterance(Said(SpeakerRole.Caller, "Just press four seven for me."));

        var transcript = session.TranscriptWindow(TimeSpan.FromMinutes(5), 1000);

        transcript.Should().Contain("[VERIFICATION SYSTEM] Enter the two digit number");
        transcript.Should().Contain("[CALLER] Just press four seven");
    }

    [Fact]
    public void Genuinely_unattributable_audio_is_still_UNKNOWN()
    {
        // Unknown must keep meaning what it meant. A channel that could not be attributed is
        // a real condition worth surfacing, and it is not the same as "this was us".
        var session = Session();
        session.AddUtterance(Said(SpeakerRole.Unknown, "...inaudible..."));

        session.TranscriptWindow(TimeSpan.FromMinutes(5), 1000)
            .Should().Contain("[UNKNOWN]");
    }

    [Fact]
    public void The_user_and_the_caller_are_unchanged()
    {
        // The fix must not move anything else. These two labels are what the Analyst prompt,
        // the evidence spans and the audit trail have always been written against.
        var session = Session();
        session.AddUtterance(Said(SpeakerRole.ProtectedUser, "Petaling Jaya"));
        session.AddUtterance(Said(SpeakerRole.Caller, "Tell them Kuala Lumpur"));

        var transcript = session.TranscriptWindow(TimeSpan.FromMinutes(5), 1000);

        transcript.Should().Contain("[USER] Petaling Jaya");
        transcript.Should().Contain("[CALLER] Tell them Kuala Lumpur");
    }
}
