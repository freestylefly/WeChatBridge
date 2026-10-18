using System.Text;
using System.Text.Json;
using WeChatBridge.Windows.Core.Licensing;
using WeChatBridge.Windows.Core.Redeem;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// C-1 (契约登记册 §11.7): the Go issuer writes <c>iat</c>/<c>nbf</c>/<c>exp</c> as
/// unix seconds — <c>license.go:91-93</c> pins them as JSON numbers, because the
/// canonical payload both sides sign is built from unix seconds. The two old-chain
/// readers (<c>LicenseStore.JsonOptions</c> and <c>RedeemService</c>'s options) did
/// not know that, and the default reader refuses a JSON number for a
/// <see cref="DateTimeOffset"/>.
/// <para>
/// The symptoms were two, and both cost a paying customer: <c>LicenseStore.Load</c>
/// swallowed the <see cref="JsonException"/> and returned null, so an activated
/// licence read back as "no licence at all"; and <c>RedeemService.Accept</c> threw
/// on a perfectly good envelope. Neither had ever surfaced because the chain had
/// never been run end to end against a real key.
/// </para>
/// <para>
/// Everything below is written in the Go dialect by hand — numbers for timestamps,
/// short field names — because a test that generated the fixture with the code
/// under test would pass with or without the converter.
/// </para>
/// </summary>
public sealed class UnixTimestampContractTests
{
    private const string InstallId = "install-id-for-this-machine";
    private const string Code = "WCB-7K3MQ-9XQ2H-4TB6P-WN3DP";

    /// <summary>
    /// An envelope exactly as the Go issuer puts it on the wire: the short pinned
    /// field names, <c>iat</c> as a unix-second number, <c>nbf</c>/<c>exp</c> null
    /// for a perpetual licence, and the signature the test key produced over the
    /// canonical bytes.
    /// </summary>
    private static string GoStyleEnvelopeJson(LicenseEnvelope envelope)
    {
        var entitlements = string.Join(",", envelope.Entitlements.Select(entitlement => $"\"{entitlement}\""));
        return $"{{" +
            $"\"v\":{envelope.SchemaVersion}," +
            $"\"sub\":\"{envelope.Subject}\"," +
            $"\"plan\":\"{envelope.Plan}\"," +
            $"\"iat\":{envelope.IssuedAt.ToUnixTimeSeconds()}," +
            $"\"nbf\":{(envelope.NotBefore is { } notBefore ? notBefore.ToUnixTimeSeconds().ToString() : "null")}," +
            $"\"exp\":{(envelope.ExpiresAt is { } expires ? expires.ToUnixTimeSeconds().ToString() : "null")}," +
            $"\"ent\":[{entitlements}]," +
            $"\"sig\":\"{envelope.Signature}\"}}";
    }

    /// <summary>
    /// The file on disk is the Go dialect, so reading it must not decide the
    /// customer never bought anything. This is the "activated licence reads back as
    /// no licence" symptom, pinned.
    /// </summary>
    [Fact]
    public void TheGoIssuersUnixSecondEnvelopeLoadsFromDisk()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var issued = keys.Issue(InstallId, issuedAt: TestTime.IssuedAt);

        File.WriteAllText(config.Paths.LicenseFile, GoStyleEnvelopeJson(issued));

        var loaded = new LicenseStore(config.Root).Load();

        Assert.NotNull(loaded);
        Assert.Equal(InstallId, loaded.Subject);
        Assert.Equal("buyout", loaded.Plan);
        Assert.Equal(TestTime.IssuedAt, loaded.IssuedAt);
        Assert.Null(loaded.NotBefore);
        Assert.Null(loaded.ExpiresAt);
        Assert.Equal(issued.Signature, loaded.Signature);
        // Reading it back is only half the job: the bytes that were signed have to
        // survive the round trip too, or the next launch refuses a paid licence.
        Assert.True(
            keys.Verifier().Verify(loaded, TestTime.Now, LicenseRuntimeOptions.Installed.ClockSkewTolerance).IsValid);
    }

    /// <summary>
    /// The same dialect inside a redemption response: the token carries the
    /// envelope, and the envelope's timestamps are numbers. Before C-1 this was
    /// <see cref="RedeemFailure.MalformedResponse"/> on a licence the server had
    /// genuinely signed for this machine.
    /// </summary>
    [Fact]
    public async Task ARedeemResponseCarryingAUnixSecondEnvelopeIsAccepted()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var envelope = keys.Issue(InstallId, issuedAt: TestTime.IssuedAt);
        var token = Base64Url.Encode(Encoding.UTF8.GetBytes(GoStyleEnvelopeJson(envelope)));

        var transport = new FakeRedeemTransport();
        // redeemedAt as a number as well: the whole response speaks the Go dialect.
        var body = $"{{\"status\":\"{RedeemStatus.Redeemed}\",\"licenseToken\":\"{token}\"," +
            $"\"licenseId\":\"lic-1\",\"redeemedAt\":{TestTime.Now.ToUnixTimeSeconds()},\"reason\":null}}";
        transport.Reply(201, body);

        using var service = new RedeemService(
            new LicenseStore(config.Root),
            keys.Verifier(),
            new PendingRedemptionStore(config.Root),
            transport,
            new RedeemMachineIdentity(InstallId, "device-digest"),
            clock: () => TestTime.Now);

        var outcome = await service.RedeemAsync(Code, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Detail);
        Assert.Equal(TestTime.Now, outcome.RedeemedAt);
        var stored = new LicenseStore(config.Root).Load();
        Assert.NotNull(stored);
        Assert.Equal(TestTime.IssuedAt, stored.IssuedAt);
        Assert.Equal(InstallId, stored.Subject);
    }

    /// <summary>
    /// The other direction still works: an envelope written by this build, with
    /// RFC 3339 strings, must stay readable. The converter is additive — it exists
    /// so both dialects parse, not to migrate the file format.
    /// </summary>
    [Fact]
    public void AnEnvelopeThisBuildWroteStillLoads()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var store = new LicenseStore(config.Root);
        store.Save(keys.Issue(InstallId, issuedAt: TestTime.IssuedAt));

        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(TestTime.IssuedAt, loaded.IssuedAt);
        Assert.True(
            keys.Verifier().Verify(loaded, TestTime.Now, LicenseRuntimeOptions.Installed.ClockSkewTolerance).IsValid);
    }
}
