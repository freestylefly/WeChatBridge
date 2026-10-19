using WeChatBridge.Windows.Core.Activation;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows.AppTests;

/// <summary>
/// The gate's decision, which is the part worth pinning: every state has to land on a
/// page, and the two easy mistakes are showing a login page to someone who already
/// has a licence and showing a purchase page to a portable stick that cannot use one.
/// <para>
/// <see cref="ActivationGate.Decide"/> is static and does no I/O precisely so this
/// needs no window, no store and no licence file to run.
/// </para>
/// </summary>
public sealed class ActivationGateTests
{
    private static GateSnapshot Snapshot(
        GateState state,
        bool isPro = false,
        bool isSignedIn = false,
        string? installId = "Zx8Kq3nT7pQ1mR4vW9yB0",
        string anchorDigest = "sha256:abcdef",
        string? nickname = null) =>
        new(state, isPro, isSignedIn, installId, anchorDigest, nickname);

    [Theory]
    [InlineData(GateState.Licensed)]
    [InlineData(GateState.Portable)]
    public void NothingIsOfferedToAMachineThatNeedsNothing(GateState state) =>
        Assert.Equal(GatePage.None, ActivationGate.Decide(Snapshot(state)));

    [Fact]
    public void AnUnlicensedMachineIsSentToSignInFirst()
    {
        // Not to the purchase page: an activation request has to attach to an account,
        // and there is no account yet.
        Assert.Equal(GatePage.Login, ActivationGate.Decide(Snapshot(GateState.Anonymous)));
    }

    [Fact]
    public void ASignedInMachineIsOfferedThePurchasePage() =>
        Assert.Equal(GatePage.Purchase, ActivationGate.Decide(Snapshot(GateState.SignedIn, isSignedIn: true)));

    [Theory]
    [InlineData(GateState.Rejected)]
    [InlineData(GateState.LicenseUnverifiable)]
    public void StatesNoLoginCanFixAreRoutedToTheSeller(GateState state) =>
        // Telling someone whose licence belongs to another machine to "scan a code"
        // wastes the first ten seconds of their attempt to fix it.
        Assert.Equal(GatePage.ContactSeller, ActivationGate.Decide(Snapshot(state)));

    [Fact]
    public void AnUnknownStateOpensNothingRatherThanGuessing()
    {
        // The gate must not invent a page from a state it does not recognise: a wrong
        // guess here is a window in front of a customer who did not ask for one.
        Assert.Equal(GatePage.None, ActivationGate.Decide(Snapshot(GateState.Unknown)));
    }

    [Fact]
    public void ALicensedMachineIsNeverOfferedAPurchasePageEvenWhenSignedOut()
    {
        // The licence comes from disk and outranks the account session: a customer who
        // bought a licence is entitled whether or not their token has aged out.
        var snapshot = Snapshot(GateState.Licensed, isPro: true, isSignedIn: false);
        Assert.Equal(GatePage.None, ActivationGate.Decide(snapshot));
        Assert.False(snapshot.CanPurchase);
    }

    [Fact]
    public void PortableIsNotTreatedAsUnlicensed()
    {
        // Portable is the free tier and is issued no licences, so it must not be
        // routed into a purchase flow it cannot complete.
        var snapshot = Snapshot(GateState.Portable, isPro: false);
        Assert.Equal(GatePage.None, ActivationGate.Decide(snapshot));
    }

    [Fact]
    public void OnlyTheStatesThatCanActuallyBuyOrFixOfferAPage()
    {
        Assert.True(Snapshot(GateState.Anonymous).CanPurchase);
        Assert.True(Snapshot(GateState.SignedIn, isSignedIn: true).CanPurchase);
        Assert.False(Snapshot(GateState.Rejected).CanPurchase);
        Assert.False(Snapshot(GateState.Licensed, isPro: true).CanPurchase);
    }
}
