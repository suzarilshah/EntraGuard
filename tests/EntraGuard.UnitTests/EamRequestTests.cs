using EntraGuard.Shared.Verification;
using Xunit;

namespace EntraGuard.UnitTests;

/// <summary>
/// Reading what Entra asked for, and refusing to answer anywhere it did not ask from.
/// </summary>
public class EamRequestTests
{
    /// <summary>The blob Microsoft's own reference documents.</summary>
    private const string Published = """
        {
          "id_token": {
            "acr": { "essential": true, "values": ["possessionorinherence"] },
            "amr": { "essential": true,
                     "values": ["face","fido","fpt","hwk","iris","otp","pop","retina","sc","sms","swk","tel","vbm"] }
          }
        }
        """;

    [Fact]
    public void The_published_request_shape_is_read_as_documented()
    {
        var (acr, amr) = EamClaimsRequest.Parse(Published);

        Assert.Equal(["possessionorinherence"], acr);
        Assert.Contains("tel", amr);
        Assert.Equal(13, amr.Count);
    }

    [Fact]
    public void A_published_request_resolves_to_a_telephone_possession_assertion()
    {
        var (acr, amr) = EamClaimsRequest.Parse(Published);

        Assert.Equal("possessionorinherence", EamClaims.SatisfiableAcr(acr, FactorType.Possession));
        Assert.Equal("tel", EamClaims.ChooseAmr(amr));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"id_token\":\"string not object\"}")]
    [InlineData("{\"userinfo\":{\"acr\":{\"values\":[\"possession\"]}}}")]
    public void Unreadable_or_irrelevant_input_yields_nothing_rather_than_throwing(string? claims)
    {
        var (acr, amr) = EamClaimsRequest.Parse(claims);

        Assert.Empty(acr);
        Assert.Empty(amr);
    }

    /// <summary>
    /// Nothing requested means nothing assertable — the two halves compose to a refusal
    /// rather than to a default that would let a malformed request through.
    /// </summary>
    [Fact]
    public void A_request_asking_for_no_acr_cannot_be_satisfied()
    {
        var (acr, _) = EamClaimsRequest.Parse("{\"id_token\":{}}");

        Assert.Null(EamClaims.SatisfiableAcr(acr, FactorType.Possession));
    }

    [Fact]
    public void Non_string_entries_are_dropped_rather_than_coerced()
    {
        var (acr, _) = EamClaimsRequest.Parse(
            "{\"id_token\":{\"acr\":{\"values\":[\"possession\", 42, null, {}, \"\"]}}}");

        Assert.Equal(["possession"], acr);
    }

    [Theory]
    [InlineData("https://login.microsoftonline.com/common/federation/externalauthprovider")]
    [InlineData("https://login.microsoftonline.us/common/federation/externalauthprovider")]
    [InlineData("https://login.partner.microsoftonline.cn/common/federation/externalauthprovider")]
    public void Microsofts_published_destinations_are_accepted(string uri) =>
        Assert.True(EamRequest.IsPublishedRedirect(uri));

    /// <summary>
    /// redirect_uri arrives in the request body. Honouring it unchecked would turn this
    /// service into an oracle that signs an identity assertion and posts it wherever the
    /// caller says — including to an attacker collecting tokens minted for real users.
    /// </summary>
    [Theory]
    [InlineData("https://evil.example/collect")]
    [InlineData("https://login.microsoftonline.com.evil.example/common/federation/externalauthprovider")]
    [InlineData("http://login.microsoftonline.com/common/federation/externalauthprovider")]
    [InlineData("https://login.microsoftonline.com/common/federation/externalauthprovider/")]
    [InlineData("https://login.microsoftonline.com/common/federation/EXTERNALAUTHPROVIDER")]
    [InlineData("")]
    [InlineData(null)]
    public void Anywhere_else_is_refused(string? uri) =>
        Assert.False(EamRequest.IsPublishedRedirect(uri));
}
