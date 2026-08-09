using EntraGuard.Shared.Verification;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// The prompt-round guard.
///
/// Pins the rule that cost a live verification: the DTMF recogniser and the media stream
/// both report the same keypress, so without a round to claim, one entry was adjudicated
/// twice and spent two of the three attempts. A user who mistyped once was then refused
/// "after 3 attempts" having genuinely tried twice.
///
/// The coordinator itself needs a live call to exercise, so these test the state machine
/// the guard is built on — the part that decides whether a submission counts.
/// </summary>
public sealed class PromptRoundTests
{
    private static VerificationSession Session() => new()
    {
        VerificationId = "vrf-test",
        StartedAt = DateTimeOffset.UnixEpoch,
        SubjectUpn = "user@contoso.com",
        CalleeAcsId = "8:acs:whoever",
        MatchCode = "42",
    };

    /// <summary>Mirrors the guard in VerificationCoordinator.SubmitAsync.</summary>
    private static bool TryClaim(VerificationSession session)
    {
        if (session.AdjudicatedRound >= session.PromptRound)
        {
            return false;
        }

        session.AdjudicatedRound = session.PromptRound;
        return true;
    }

    [Fact]
    public void ASubmissionBeforeAnyPromptIsNotAdjudicated()
    {
        // Round 0 means nothing has been asked. Digits arriving here belong to no challenge
        // — stray tones must never consume an attempt.
        Assert.False(TryClaim(Session()));
    }

    [Fact]
    public void TheFirstPathToArriveClaimsTheRound()
    {
        var session = Session();
        session.PromptRound++;

        Assert.True(TryClaim(session));
    }

    [Fact]
    public void TheSecondPathReportingTheSameKeypressIsIgnored()
    {
        // The actual bug: recogniser and media stream both deliver, milliseconds apart.
        var session = Session();
        session.PromptRound++;

        Assert.True(TryClaim(session));
        Assert.False(TryClaim(session));
    }

    [Fact]
    public void ThreeEntriesGetThreeAttemptsNotOneAndAHalf()
    {
        var session = Session();
        var adjudicated = 0;

        for (var entry = 0; entry < 3; entry++)
        {
            session.PromptRound++;

            // Both paths report every entry, exactly as they do on a real call.
            if (TryClaim(session)) adjudicated++;
            if (TryClaim(session)) adjudicated++;
        }

        Assert.Equal(3, adjudicated);
    }

    [Fact]
    public void ANewPromptReopensAdjudication()
    {
        var session = Session();

        session.PromptRound++;
        Assert.True(TryClaim(session));

        // Re-prompt after a wrong entry.
        session.PromptRound++;
        Assert.True(TryClaim(session));
    }
}
