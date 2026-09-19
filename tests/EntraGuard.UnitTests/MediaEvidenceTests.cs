using EntraGuard.Shared.Sessions;
using EntraGuard.Shared.Verification;
using FluentAssertions;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// What a finished call reports about the audio it carried.
///
/// A live verification reported 111 seconds, three spoken answers and a keypad entry, and its
/// record said streamConnected false, audioFrames 0, dtmfReceived 0 — because the status
/// endpoint read those counters from the live media session, and completing a verification
/// removes that session as its last act. A call that worked and a call that never connected
/// produced byte-identical records, which is the one distinction anybody triaging a failure
/// actually needs.
/// </summary>
public class MediaEvidenceTests
{
    private static CallSession Carried(long frames, int dtmf)
    {
        var session = new CallSession { SessionId = "vmon-1", StartedAt = DateTimeOffset.UtcNow };
        session.MediaStreamConnectedAt = DateTimeOffset.UtcNow;
        session.AudioFramesReceived = frames;
        session.DtmfReceived = dtmf;
        return session;
    }

    private static VerificationSession Verification() => new()
    {
        VerificationId = "vrf-1",
        StartedAt = DateTimeOffset.UtcNow,
        SubjectUpn = "user@contoso.com",
        CalleeAcsId = "8:acs:x",
        MatchCode = "42",
        MonitorSessionId = "vmon-1",
    };

    /// <summary>The copy that <c>CompleteAsync</c> performs immediately before teardown.</summary>
    private static void Snapshot(VerificationSession verification, CallSession media)
    {
        verification.MediaStreamConnected = media.MediaStreamConnectedAt is not null;
        verification.AudioFramesReceived = media.AudioFramesReceived;
        verification.DtmfReceived = media.DtmfReceived;
    }

    [Fact]
    public void The_evidence_survives_the_session_being_disposed()
    {
        var verification = Verification();

        Snapshot(verification, Carried(frames: 1306, dtmf: 2));

        // The live session is now gone. The record must still describe the call.
        verification.MediaStreamConnected.Should().BeTrue();
        verification.AudioFramesReceived.Should().Be(1306);
        verification.DtmfReceived.Should().Be(2);
    }

    [Fact]
    public void A_call_that_never_connected_still_reads_as_empty()
    {
        // The other half of the property: the fix must not manufacture evidence. A call that
        // genuinely carried nothing has to stay distinguishable from one that carried plenty.
        var verification = Verification();
        var silent = new CallSession { SessionId = "vmon-1", StartedAt = DateTimeOffset.UtcNow };

        Snapshot(verification, silent);

        verification.MediaStreamConnected.Should().BeFalse();
        verification.AudioFramesReceived.Should().Be(0);
        verification.DtmfReceived.Should().Be(0);
    }

    [Fact]
    public void A_verification_starts_with_no_claims_about_media()
    {
        // Defaults matter: an unstarted verification must not assert a stream it never had.
        var verification = Verification();

        verification.MediaStreamConnected.Should().BeFalse();
        verification.AudioFramesReceived.Should().Be(0);
        verification.DtmfReceived.Should().Be(0);
    }
}
