using EntraGuard.Shared.Verification;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// What EntraGuard may tell Entra ID it proved.
///
/// These are exhaustive on purpose. The acr/amr pair in the response token is the entire
/// basis on which Entra decides multifactor authentication happened, so every value in
/// Microsoft's fixed vocabulary is asserted here rather than sampled.
/// </summary>
public class EamClaimsTests
{
    [Theory]
    [InlineData("face", FactorType.Inherence)]
    [InlineData("fido", FactorType.Possession)]
    [InlineData("fpt", FactorType.Inherence)]
    [InlineData("hwk", FactorType.Possession)]
    [InlineData("iris", FactorType.Inherence)]
    [InlineData("otp", FactorType.Possession)]
    [InlineData("pop", FactorType.Possession)]
    [InlineData("retina", FactorType.Inherence)]
    [InlineData("sc", FactorType.Possession)]
    [InlineData("sms", FactorType.Possession)]
    [InlineData("swk", FactorType.Possession)]
    [InlineData("tel", FactorType.Possession)]
    [InlineData("vbm", FactorType.Inherence)]
    public void Every_published_method_maps_to_its_published_type(string method, FactorType expected) =>
        Assert.Equal(expected, EamClaims.TypeOf(method));

    [Theory]
    [InlineData("TEL")]          // Entra's values are case-sensitive.
    [InlineData("telephone")]
    [InlineData("voice")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unrecognised_method_maps_to_nothing(string? method) =>
        Assert.Null(EamClaims.TypeOf(method));

    /// <summary>The ordinary case: a password first factor, a phone call second.</summary>
    [Fact]
    public void A_phone_call_satisfies_the_usual_request_after_a_password()
    {
        Assert.Equal(
            "possessionorinherence",
            EamClaims.SatisfiableAcr(["possessionorinherence"], FactorType.Possession));
        Assert.Equal("tel", EamClaims.ChooseAmr(["otp", "tel", "sms"]));
    }

    [Theory]
    [InlineData("possession")]
    [InlineData("knowledgeorpossession")]
    [InlineData("possessionorinherence")]
    [InlineData("knowledgeorpossessionorinherence")]
    public void Possession_satisfies_every_acr_that_accepts_it(string acr) =>
        Assert.Equal(acr, EamClaims.SatisfiableAcr([acr], FactorType.Possession));

    /// <summary>
    /// The case that must fail, and the reason this class is pure and exhaustively tested.
    ///
    /// A first factor that was itself possession-based — a FIDO key, a passkey — makes Entra
    /// ask for inherence. A telephone call cannot supply inherence, so the only honest answer
    /// is none. Returning "vbm" here because the call carried a voice would pass the sign-in
    /// on a biometric check that observe-mode voice cannot fail.
    /// </summary>
    [Theory]
    [InlineData("inherence")]
    [InlineData("knowledge")]
    [InlineData("knowledgeorinherence")]
    public void Possession_cannot_satisfy_an_acr_that_excludes_it(string acr) =>
        Assert.Null(EamClaims.SatisfiableAcr([acr], FactorType.Possession));

    [Fact]
    public void The_first_satisfiable_acr_wins_because_the_order_is_Entras_preference()
    {
        Assert.Equal(
            "possession",
            EamClaims.SatisfiableAcr(["inherence", "possession", "knowledgeorpossession"], FactorType.Possession));
    }

    [Theory]
    [InlineData("knowledgeorpossessionorinherence")]
    public void The_widest_acr_is_satisfied_by_any_factor(string acr)
    {
        Assert.Equal(acr, EamClaims.SatisfiableAcr([acr], FactorType.Knowledge));
        Assert.Equal(acr, EamClaims.SatisfiableAcr([acr], FactorType.Possession));
        Assert.Equal(acr, EamClaims.SatisfiableAcr([acr], FactorType.Inherence));
    }

    [Fact]
    public void An_unknown_acr_is_not_honoured_even_if_it_sounds_permissive()
    {
        Assert.Null(EamClaims.SatisfiableAcr(["anything", "mfa", "possession_or_inherence"], FactorType.Possession));
    }

    [Fact]
    public void No_requested_acr_means_nothing_may_be_asserted()
    {
        Assert.Null(EamClaims.SatisfiableAcr([], FactorType.Possession));
        Assert.Null(EamClaims.SatisfiableAcr(null, FactorType.Possession));
    }

    [Fact]
    public void The_call_asserts_telephone_possession_and_never_a_voiceprint()
    {
        // Voice runs in observe mode and cannot refuse anybody, so asserting inherence
        // would hand Entra a factor nothing enforces. If this test is what fails when
        // somebody changes CallAmr, that is the point of it.
        Assert.Equal("tel", EamClaims.CallAmr);
        Assert.Equal(FactorType.Possession, EamClaims.TypeOf(EamClaims.CallAmr));
        Assert.NotEqual("vbm", EamClaims.CallAmr);
    }

    [Fact]
    public void A_request_that_will_not_take_a_phone_call_gets_no_method()
    {
        Assert.Null(EamClaims.ChooseAmr(["face", "fido", "iris"]));
    }

    [Fact]
    public void An_absent_method_list_accepts_the_default_rather_than_failing_on_a_technicality()
    {
        Assert.Equal("tel", EamClaims.ChooseAmr(null));
        Assert.Equal("tel", EamClaims.ChooseAmr([]));
    }

    [Fact]
    public void A_method_outside_the_published_vocabulary_can_never_be_asserted()
    {
        Assert.Null(EamClaims.ChooseAmr(["callback"], "callback"));
        Assert.Null(EamClaims.ChooseAmr(null, "entraguard"));
    }
}
