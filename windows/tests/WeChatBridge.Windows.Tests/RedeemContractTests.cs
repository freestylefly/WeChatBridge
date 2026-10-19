using WeChatBridge.Windows.Core.Redeem;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// The wire contract's own invariants, and the placeholders that have to be replaced
/// before this edition ships.
/// </summary>
public sealed class RedeemContractTests
{
    [Fact]
    public void TheEndpointIsAReservedDomainThatCannotResolve()
    {
        // RFC 2606 reserves .invalid precisely so a build left with the placeholder
        // fails loudly at the first redemption instead of quietly reaching somebody
        // else's server. That is the behaviour being bought here, and a test that
        // asserts it keeps a well-meaning "fix" from swapping in a real host before the
        // contract has been agreed with the server.
        Assert.StartsWith("https://", RedeemEndpoint.AbsoluteUrl, StringComparison.Ordinal);
        Assert.Contains(".invalid/", RedeemEndpoint.AbsoluteUrl, StringComparison.Ordinal);
        Assert.Equal("v1/license/redeem", RedeemEndpoint.Path);
        Assert.NotEmpty(RedeemEndpoint.ProductId);
        Assert.True(RedeemEndpoint.DefaultTimeout > TimeSpan.Zero);
        Assert.True(RedeemEndpoint.DefaultTimeout < TimeSpan.FromMinutes(5));
    }

    [Theory]
    [InlineData(408, true)]
    [InlineData(425, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(200, false)]
    [InlineData(201, false)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(409, false)]
    [InlineData(410, false)]
    [InlineData(422, false)]
    public void RetryableMeansTheWireMayHaveSwallowedTheAnswer(int statusCode, bool retryable)
    {
        Assert.Equal(retryable, RedeemClassification.IsRetryableStatus(statusCode));
        Assert.False(RedeemClassification.IsSuccessStatus(statusCode) && retryable);
    }

    [Theory]
    [InlineData("redeemed", true)]
    [InlineData("alreadyRedeemed", true)]
    [InlineData("pending", false)]
    [InlineData("rejected", false)]
    [InlineData("", false)]
    public void OnlyTheTwoKnownStatusesHandOverALicence(string status, bool grants) =>
        Assert.Equal(grants, new RedeemResponse(status, "token", "id", null, null).GrantsLicense);

    [Fact]
    public void AnUnknownRejectionReasonIsRefusedRatherThanAccepted()
    {
        // A server that grows a new rejection must not have this client treat it as
        // something it understands, and certainly must not write licence.json for it.
        Assert.Equal(RedeemFailure.Rejected, RedeemClassification.FromReason("somethingNew"));
        Assert.Equal(RedeemFailure.Rejected, RedeemClassification.FromReason(null));
    }
}
