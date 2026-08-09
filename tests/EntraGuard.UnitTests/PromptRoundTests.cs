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

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    /// <summary>Mirrors the guard in VerificationCoordinator.SubmitAsync.</summary>
    private static bool TryClaim(VerificationSession session, string entered, DateTimeOffset now)
    {
        var sameRound = session.AdjudicatedRound >= session.PromptRound;
        var sameEntry = session.LastAdjudicatedEntry == entered
            && now - session.LastAdjudicatedAt < Window;

        if (sameRound || sameEntry)
        {
            return false;
        }

        session.AdjudicatedRound = session.PromptRound;
        session.LastAdjudicatedEntry = entered;
        session.LastAdjudicatedAt = now;
        return true;
    }

    private static bool TryClaim(VerificationSession session) =>
        TryClaim(session, "99", DateTimeOffset.UnixEpoch);

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
        var t = DateTimeOffset.UnixEpoch;

        // Three DIFFERENT wrong entries, seconds apart — a real user working through their
        // attempts, with both input paths reporting each keypress.
        foreach (var entry in new[] { "11", "22", "33" })
        {
            session.PromptRound++;
            t = t.AddSeconds(10);

            if (TryClaim(session, entry, t)) adjudicated++;
            if (TryClaim(session, entry, t.AddMilliseconds(40))) adjudicated++;
        }

        Assert.Equal(3, adjudicated);
    }

    [Fact]
    public void ALateDuplicateCannotClaimTheRoundOpenedByItsOwnRejection()
    {
        // The exact live failure. A wrong entry re-prompts immediately, opening round 2 —
        // and the second input path, arriving milliseconds later with the SAME digits,
        // found that fresh round unclaimed and spent it. Two of three attempts on one
        // keypress. The round counter alone cannot see this; the digits can.
        var session = Session();
        var t = DateTimeOffset.UnixEpoch;

        session.PromptRound++;
        Assert.True(TryClaim(session, "11", t));       // media stream, wrong

        session.PromptRound++;                          // re-prompt fires at once
        Assert.False(TryClaim(session, "11", t.AddMilliseconds(40)));  // recogniser, same keypress
    }

    [Fact]
    public void TheSameDigitsEnteredAgainLaterDoCount()
    {
        // Dedupe must not swallow a genuine retry. A user who hears the re-prompt and
        // deliberately tries the same code again has spent an attempt, and pretending
        // otherwise would let them retry forever.
        var session = Session();
        var t = DateTimeOffset.UnixEpoch;

        session.PromptRound++;
        Assert.True(TryClaim(session, "11", t));

        session.PromptRound++;
        Assert.True(TryClaim(session, "11", t.AddSeconds(9)));
    }

    [Fact]
    public void ADifferentEntryInANewRoundIsAlwaysJudged()
    {
        var session = Session();
        var t = DateTimeOffset.UnixEpoch;

        session.PromptRound++;
        Assert.True(TryClaim(session, "11", t));

        session.PromptRound++;
        Assert.True(TryClaim(session, "22", t.AddMilliseconds(40)));
    }

    [Fact]
    public void ANewPromptReopensAdjudication()
    {
        var session = Session();
        var t = DateTimeOffset.UnixEpoch;

        session.PromptRound++;
        Assert.True(TryClaim(session, "11", t));

        // Re-prompt after a wrong entry, then a genuinely different one.
        session.PromptRound++;
        Assert.True(TryClaim(session, "22", t.AddSeconds(10)));
    }
}
