using System.Text;
using WeChatBridge.Windows.Core.Activation;
using WeChatBridge.Windows.Core.Licensing;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// The buy-out purchase, driven through the transport seam: register this machine,
/// watch the seller's decision arrive, collect the licence.
/// <para>
/// The load-bearing half is <see cref="ActivationService"/>'s accept order — verify,
/// then check the subject, then write, with no await between the checks and the
/// write. The tests that matter most here are the ones asserting that a bad licence
/// <b>never touches the disk</b>: a file in <c>license.json</c> is a purchase as far
/// as everything downstream is concerned, and an unverifiable or foreign envelope
/// that got written would leave the user with an app that says no and a file it
/// cannot delete.
/// </para>
/// </summary>
public sealed class ActivationServiceTests
{
    private const string InstallId = "Zx8Kq3nT7pQ1mR4vW9yB0";
    private const string AnchorDigest = "sha256:8c1fa31d";

    private static AccountView Buyer() => new(
        "acc_demo0001", "演示买家", "", "buyout", TestTime.IssuedAt, TestTime.IssuedAt, true);

    private static AccountService SignedIn(FakeActivationTransport transport, TempLicenseConfig config)
    {
        var store = new AuthTokenStore(config.Root, config.Protector);
        store.Save(new AuthTokens("mock_at_1", "mock_rt_1", TestTime.Now.AddHours(1), Buyer()));
        return new AccountService(transport, store, PollSchedule.Login, () => TestTime.Now);
    }

    private static ActivationService Service(
        TempLicenseConfig config,
        FakeActivationTransport transport,
        TestSigningKey keys,
        string? installId = InstallId,
        bool signedIn = true) =>
        new(
            new LicenseStore(config.Root),
            keys.Verifier(),
            transport,
            signedIn ? SignedIn(transport, config) : new AccountService(transport, null, PollSchedule.Login, () => TestTime.Now),
            () => TestTime.Now);

    /// <summary>
    /// The licence endpoint's body, written in the Go dialect: the envelope carries
    /// unix-second <c>iat</c>, so this exercises the timestamp converter on the real
    /// path as well as the accept order.
    /// </summary>
    private static string LicenceBody(LicenseEnvelope envelope)
    {
        var seconds = envelope.IssuedAt.ToUnixTimeSeconds();
        var entitlements = string.Join(",", envelope.Entitlements.Select(e => $"\"{e}\""));
        var envelopeJson = $"{{\"v\":{envelope.SchemaVersion},\"sub\":\"{envelope.Subject}\",\"plan\":\"{envelope.Plan}\"," +
            $"\"iat\":{seconds},\"nbf\":null,\"exp\":null,\"ent\":[{entitlements}],\"sig\":\"{envelope.Signature}\"}}";
        var token = Base64Url.Encode(Encoding.UTF8.GetBytes(envelopeJson));
        return $"{{\"token\":\"{token}\",\"envelope\":{envelopeJson}}}";
    }

    // -----------------------------------------------------------------------
    // Register
    // -----------------------------------------------------------------------

    [Fact]
    public async Task RegisterSendsTheMachineAndGetsBackAnIdToPoll()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeActivationTransport();
        transport.Reply(201, "{\"requestId\":\"act_abc123\",\"status\":\"pending\"}");
        var service = Service(config, transport, keys);

        var outcome = await service.RegisterAsync(InstallId, AnchorDigest, 3800, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Detail);
        Assert.True(outcome.IsRegistered);
        Assert.Equal("act_abc123", outcome.RequestId);
        var request = transport.Single();
        Assert.Equal("POST", request.Method);
        Assert.Equal(ActivationEndpoint.ActivationRequestPath, request.Path);
        Assert.Equal("mock_at_1", request.BearerToken);
        var body = FakeActivationTransport.BodyOf(request);
        Assert.Equal(InstallId, body.GetProperty("installId").GetString());
        Assert.Equal(AnchorDigest, body.GetProperty("anchorDigest").GetString());
        Assert.Equal(3800, body.GetProperty("amountFen").GetInt64());
    }

    [Fact]
    public async Task RegisterWithoutAnInstallIdIsRefusedBeforeAnyCall()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeActivationTransport();
        var service = Service(config, transport, keys);

        var outcome = await service.RegisterAsync("", AnchorDigest, 3800, CancellationToken.None);

        Assert.Equal(ActivationFailure.NoInstallId, outcome.Failure);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task RegisterWithoutASessionIsRefused()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeActivationTransport();
        var service = Service(config, transport, keys, signedIn: false);

        var outcome = await service.RegisterAsync(InstallId, AnchorDigest, 3800, CancellationToken.None);

        Assert.Equal(ActivationFailure.NotSignedIn, outcome.Failure);
        Assert.Empty(transport.Requests);
    }

    /// <summary>
    /// A 401 mid-purchase means the token aged out, not that the purchase failed —
    /// the user is sent back to sign in rather than told to retry a purchase that
    /// was never registered.
    /// </summary>
    [Fact]
    public async Task RegisterRefusedByTheServerReadsAsNotSignedIn()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeActivationTransport();
        transport.Reply(401, "{\"error\":{\"code\":\"unauthorized\",\"message\":\"缺少访问令牌。\",\"request_id\":\"\"}}");
        var service = Service(config, transport, keys);

        var outcome = await service.RegisterAsync(InstallId, AnchorDigest, 3800, CancellationToken.None);

        Assert.Equal(ActivationFailure.NotSignedIn, outcome.Failure);
        Assert.Contains("缺少访问令牌。", outcome.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A conflict on register means this machine already has a live request — the
    /// server's answer is "you are already in the queue", not a failure. It is
    /// deliberately not marked retryable: pressing 登记 again would only produce
    /// the same conflict, and the caller's next move is to poll, not to re-register.
    /// </summary>
    [Fact]
    public async Task RegisterConflictingWithALiveRequestIsNotAFailure()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeActivationTransport();
        transport.Reply(409, "{\"error\":{\"code\":\"conflict\",\"message\":\"该机器码已有进行中的购买登记，请联系卖家处理。\",\"request_id\":\"\"}}");
        var service = Service(config, transport, keys);

        var outcome = await service.RegisterAsync(InstallId, AnchorDigest, 3800, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Detail);
        Assert.False(outcome.IsRegistered);
        Assert.Contains("已有进行中", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RegisterAnsweredWithGarbageIsRetryable()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeActivationTransport();
        transport.Reply(201, "not json at all");
        var service = Service(config, transport, keys);

        var outcome = await service.RegisterAsync(InstallId, AnchorDigest, 3800, CancellationToken.None);

        Assert.Equal(ActivationFailure.MalformedResponse, outcome.Failure);
        Assert.True(outcome.CanRetry);
    }

    // -----------------------------------------------------------------------
    // Status
    // -----------------------------------------------------------------------

    /// <summary>
    /// Nothing in flight is not an error: the purchase page offers to register
    /// rather than showing a failure to a customer who has not bought anything yet.
    /// </summary>
    [Fact]
    public async Task StatusWithNoRegistrationAtAllIsNotAnError()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeActivationTransport();
        transport.Reply(404, "{\"error\":{\"code\":\"not_found\",\"message\":\"请求的内容不存在。\",\"request_id\":\"\"}}");
        var service = Service(config, transport, keys);

        var outcome = await service.CheckStatusAsync(CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Detail);
        Assert.True(outcome.CanRetry);
        Assert.False(outcome.Confirmed);
        var request = transport.Single();
        Assert.Equal("GET", request.Method);
        Assert.Equal(ActivationEndpoint.ActivationStatusPath, request.Path);
    }

    [Fact]
    public async Task StatusPendingNamesWhatIsBeingWaitedFor()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeActivationTransport();
        transport.Reply(200, "{\"status\":\"pending\"}");
        var service = Service(config, transport, keys);

        var outcome = await service.CheckStatusAsync(CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Detail);
        Assert.False(outcome.Confirmed);
        Assert.Contains("等待卖家确认", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusConfirmedIsTheCueToGoAndCollect()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeActivationTransport();
        transport.Reply(200, "{\"status\":\"confirmed\",\"licenseId\":\"lic_abc123\"}");
        var service = Service(config, transport, keys);

        var outcome = await service.CheckStatusAsync(CancellationToken.None);

        Assert.True(outcome.Confirmed);
        Assert.Contains("正在取许可证", outcome.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rejection is the seller's ruling, not a network hiccup: retrying it would
    /// only produce the same answer, and the detail points the user at support.
    /// </summary>
    [Fact]
    public async Task StatusRejectedIsTerminalAndPointsAtSupport()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeActivationTransport();
        transport.Reply(200, "{\"status\":\"rejected\"}");
        var service = Service(config, transport, keys);

        var outcome = await service.CheckStatusAsync(CancellationToken.None);

        Assert.Equal(ActivationFailure.Rejected, outcome.Failure);
        Assert.False(outcome.CanRetry);
        Assert.Contains("客服", outcome.Detail, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Collect — the order is the whole design
    // -----------------------------------------------------------------------

    /// <summary>
    /// Confirmed-but-not-yet-readable is a real race: the handler signs on confirm
    /// and the read can lose. The next poll resolves it, so it is retryable rather
    /// than an error.
    /// </summary>
    [Fact]
    public async Task CollectBeforeTheSignatureLandsIsRetryable()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeActivationTransport();
        transport.Reply(409, "{\"error\":{\"code\":\"conflict\",\"message\":\"授权尚未确认，暂时无法获取许可证。\",\"request_id\":\"\"}}");
        var service = Service(config, transport, keys);

        var outcome = await service.CollectAsync(InstallId, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Detail);
        Assert.True(outcome.CanRetry);
        Assert.Null(new LicenseStore(config.Root).Load());
    }

    /// <summary>
    /// The happy path, in the Go dialect end to end: the envelope arrives with
    /// unix-second timestamps, verifies against the embedded key, matches this
    /// machine's install id, and only then is written.
    /// </summary>
    [Fact]
    public async Task CollectVerifiesThenWrites()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeActivationTransport();
        transport.Reply(200, LicenceBody(keys.Issue(InstallId, issuedAt: TestTime.IssuedAt)));
        var service = Service(config, transport, keys);

        var outcome = await service.CollectAsync(InstallId, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Detail);
        Assert.NotNull(outcome.License);
        Assert.Equal(TestTime.IssuedAt, outcome.IssuedAt);
        var stored = new LicenseStore(config.Root).Load();
        Assert.NotNull(stored);
        Assert.Equal(InstallId, stored.Subject);
        Assert.Equal(TestTime.IssuedAt, stored.IssuedAt);
    }

    /// <summary>
    /// The property that keeps a bad licence out of the purchase path: an envelope
    /// that does not verify is refused <b>before</b> anything is written, so the
    /// user is never left with a file that reads as a purchase and an app that says
    /// no.
    /// </summary>
    [Fact]
    public async Task AnUnverifiableLicenceNeverTouchesTheDisk()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        using var otherKeys = new TestSigningKey();
        var transport = new FakeActivationTransport();
        // Signed by a key this build does not trust — the server is not who it says.
        transport.Reply(200, LicenceBody(otherKeys.Issue(InstallId, issuedAt: TestTime.IssuedAt)));
        var service = Service(config, transport, keys);

        var outcome = await service.CollectAsync(InstallId, CancellationToken.None);

        Assert.Equal(ActivationFailure.LicenseUnverifiable, outcome.Failure);
        Assert.Null(outcome.License);
        Assert.Null(new LicenseStore(config.Root).Load());
    }

    /// <summary>
    /// The signature is genuine, but it is for somebody else's machine — the one
    /// case where the client is certain the service is right and this machine is
    /// the odd one out. Also refused before the write, and not retryable: a reissue
    /// is the seller's to grant.
    /// </summary>
    [Fact]
    public async Task ALicenceForAnotherMachineNeverTouchesTheDisk()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeActivationTransport();
        transport.Reply(200, LicenceBody(keys.Issue("somebody-elses-install", issuedAt: TestTime.IssuedAt)));
        var service = Service(config, transport, keys);

        var outcome = await service.CollectAsync(InstallId, CancellationToken.None);

        Assert.Equal(ActivationFailure.SubjectMismatch, outcome.Failure);
        Assert.False(outcome.CanRetry);
        Assert.Null(outcome.License);
        Assert.Null(new LicenseStore(config.Root).Load());
    }

    [Fact]
    public async Task CollectAnsweredWithGarbageIsRetryable()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeActivationTransport();
        transport.Reply(200, "{\"token\":\"...\",\"envelope\":\"not an object\"}");
        var service = Service(config, transport, keys);

        var outcome = await service.CollectAsync(InstallId, CancellationToken.None);

        Assert.Equal(ActivationFailure.MalformedResponse, outcome.Failure);
        Assert.True(outcome.CanRetry);
        Assert.Null(new LicenseStore(config.Root).Load());
    }

    [Fact]
    public async Task CollectWithoutASessionIsRefused()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeActivationTransport();
        transport.Reply(200, LicenceBody(keys.Issue(InstallId, issuedAt: TestTime.IssuedAt)));
        var service = Service(config, transport, keys, signedIn: false);

        var outcome = await service.CollectAsync(InstallId, CancellationToken.None);

        Assert.Equal(ActivationFailure.NotSignedIn, outcome.Failure);
        Assert.Null(new LicenseStore(config.Root).Load());
    }
}
