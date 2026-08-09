using EntraGuard.Shared.Verification;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// The knowledge challenge is matched against a hash, so normalisation IS the matching
/// rule. These tests pin both directions of it: what a speaker may vary freely, and what
/// must still fail.
/// </summary>
public sealed class KnowledgeChallengeTests
{
    private static KnowledgeQuestion Registered(string answer) =>
        KnowledgeChallenge.Register("What was the name of your first pet?", answer);

    [Theory]
    [InlineData("Bluebell", "bluebell")]
    [InlineData("Bluebell", "Bluebell.")]
    [InlineData("Bluebell", "um, Bluebell")]
    [InlineData("Bluebell", "I think it was Bluebell")]
    [InlineData("Bluebell", "  BLUEBELL  ")]
    [InlineData("O'Brien", "obrien")]
    [InlineData("Mary-Anne", "Mary Anne")]
    // Word boundaries come from the recogniser, not the speaker, so they are deliberately
    // not load-bearing. The cost of that decision is pinned by
    // WordBoundariesAreNotLoadBearing below rather than left implicit.
    [InlineData("New York", "newyork")]
    public void SpeechVariationsStillMatch(string registered, string spoken)
    {
        // A user who answers correctly must not fail because recognition punctuated it
        // differently or because they hesitated. Rejecting these teaches people the system
        // is broken, which is how a real factor gets abandoned.
        Assert.True(KnowledgeChallenge.Verify(Registered(registered), spoken));
    }

    [Theory]
    [InlineData("Bluebell", "Bluebelle")]
    [InlineData("Bluebell", "Rex")]
    [InlineData("Bluebell", "")]
    [InlineData("Bluebell", "um, uh, well")]
    public void WrongAnswersFail(string registered, string spoken)
    {
        Assert.False(KnowledgeChallenge.Verify(Registered(registered), spoken));
    }

    [Fact]
    public void WordBoundariesAreNotLoadBearing()
    {
        // The accepted cost of matching "Mary Anne" to "Mary-Anne": spacing is ignored in
        // BOTH directions. Asserted explicitly so the trade-off is visible rather than
        // discovered later by someone assuming spaces are checked.
        Assert.True(KnowledgeChallenge.Verify(Registered("Bluebell"), "Blue bell"));
    }

    [Fact]
    public void AnAnswerOfNothingButFillerIsNotAnAnswer()
    {
        // Guards the case where the normaliser strips everything: an empty normalised
        // answer must never match an empty registered one, or silence becomes a pass.
        var registered = Registered("Bluebell");
        Assert.False(KnowledgeChallenge.Verify(registered, "um uh er well so"));
    }

    [Fact]
    public void RegistrationRejectsAnAnswerThatNormalisesToNothing()
    {
        Assert.Throws<ArgumentException>(() => KnowledgeChallenge.Register("Question?", "the a an"));
    }

    [Fact]
    public void TheSameAnswerRegisteredTwiceProducesDifferentHashes()
    {
        // Per-answer salt. Without it, two users who picked the same childhood pet are
        // visibly identical in the store, and the store becomes a rainbow table target.
        var first = Registered("Bluebell");
        var second = Registered("Bluebell");

        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.AnswerHash, second.AnswerHash);
        Assert.True(KnowledgeChallenge.Verify(first, "Bluebell"));
        Assert.True(KnowledgeChallenge.Verify(second, "Bluebell"));
    }

    [Fact]
    public void TheHashNeverRoundTripsToTheAnswer()
    {
        // The hash remains a hash. This is what makes the exact-match path safe to keep in
        // a store, and it is unaffected by the readable copy alongside it.
        var registered = Registered("Bluebell");

        Assert.DoesNotContain("bluebell", registered.AnswerHash, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bluebell", registered.Salt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bluebell", registered.Question, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheReadableCopyIsKeptDeliberatelyAndExactly()
    {
        // Named in a test so it cannot be forgotten: registration DOES retain the answer in
        // the clear, because the semantic judge needs something to compare against. If this
        // ever stops being intended, this test is where the decision gets revisited rather
        // than discovered in a breach.
        var registered = KnowledgeChallenge.Register("What was the name of your first pet?", "  Bluebell  ");

        Assert.Equal("Bluebell", registered.PlainAnswer);
    }

    [Fact]
    public void SpeakerLabelsMustNotReachTheMatcher()
    {
        // The live failure. The voice agent published transcripts as "[caller] Bluebell",
        // and normalisation folded the label into the answer — "callerbluebell" — so a user
        // who said the right word out loud was refused. Speaker identity travels beside the
        // text now, never inside it, and this pins that: if a label ever leaks back into the
        // transcript, the answer stops matching and this test says so.
        var registered = Registered("Bluebell");

        Assert.True(KnowledgeChallenge.Verify(registered, "Bluebell"));
        Assert.False(KnowledgeChallenge.Verify(registered, "[caller] Bluebell"));
    }

    [Fact]
    public void ACorruptSaltFailsClosed()
    {
        var registered = Registered("Bluebell") with { Salt = "not base64!!" };
        Assert.False(KnowledgeChallenge.Verify(registered, "Bluebell"));
    }
}
