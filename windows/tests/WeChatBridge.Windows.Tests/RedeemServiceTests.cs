using System.Text;
using System.Text.Json;
using WeChatBridge.Windows.Core.Licensing;
using WeChatBridge.Windows.Core.Redeem;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// A stand-in for the one HTTP call this product is allowed to make: it records every
/// body it was given, and answers whatever the test scripted.
/// <para>
/// The seal behaviour is reproduced faithfully rather than conveniently — the sealed
/// check comes first, before the attempt counter moves — because "throws before it
/// touches the handler" is the property the seal test is about, and a fake that
/// checked afterwards would let the test pass for the wrong reason.
/// </para>
/// </summary>
internal sealed class FakeRedeemTransport : IRedeemTransport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Queue<Func<RedeemRequest, RedeemHttpReply>> _script = new();
    private readonly List<RedeemRequest> _requests = [];

    /// <summary>Every request the transport was asked to send, in order.</summary>
    public IReadOnlyList<RedeemRequest> Requests
    {
        get { lock (_requests) return _requests.ToArray(); }
    }

    /// <summary>How many times the transport was reached at all.</summary>
    public int PostCount { get; private set; }

    public bool IsSealed { get; private set; }

    public bool Disposed { get; private set; }

    public void Reply(int statusCode, string body) =>
        _script.Enqueue(_ => new RedeemHttpReply(statusCode, body));

    public void Throw(Func<RedeemRequest, Exception> failure) => _script.Enqueue(request => throw failure(request));

    public Task<RedeemHttpReply> PostAsync(string json, CancellationToken cancellationToken)
    {
        if (IsSealed)
            throw new RedeemChannelSealedException();
        if (Disposed)
            throw new ObjectDisposedException(nameof(FakeRedeemTransport));

        PostCount++;
        cancellationToken.ThrowIfCancellationRequested();
        var request = JsonSerializer.Deserialize<RedeemRequest>(json, JsonOptions)
            ?? throw new InvalidOperationException("the service sent a body this fake could not read");
        lock (_requests)
            _requests.Add(request);

        var next = _script.Count > 0 ? _script.Dequeue() : _ => new RedeemHttpReply(500, "{}");
        return Task.FromResult(next(request));
    }

    public void Seal()
    {
        IsSealed = true;
        Disposed = true;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// The redemption flow, driven through the transport seam: the idempotency record,
/// the retry that reuses it, the split between "ask again" and "enter a different
/// code", and the instant the channel closes.
/// </summary>
public sealed class RedeemServiceTests
{
    private const string Code = "WCB-7K3MQ-9XQ2H-4TB6P-WN3DP";
    private const string InstallId = "install-id-for-this-machine";

    private static RedeemService Service(
        TempLicenseConfig config,
        TestSigningKey keys,
        FakeRedeemTransport transport,
        string? installId = InstallId,
        Action<string>? log = null) =>
        new(
            new LicenseStore(config.Root),
            keys.Verifier(),
            new PendingRedemptionStore(config.Root),
            transport,
            new RedeemMachineIdentity(installId, "device-digest"),
            log: log);

    /// <summary>A well-formed response carrying a licence this client can verify.</summary>
    private static string Issuing(TestSigningKey keys, string status = RedeemStatus.Redeemed, string subject = InstallId)
    {
        var envelope = keys.Issue(subject, issuedAt: TestTime.IssuedAt);
        var token = Base64Url.Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope)));
        return JsonSerializer.Serialize(new RedeemResponse(status, token, "lic-1", TestTime.Now, null));
    }

    [Fact]
    public async Task AGoodCodeWritesTheLicenseAndClearsThePendingRecord()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeRedeemTransport();
        transport.Reply(201, Issuing(keys));
        using var service = Service(config, keys, transport);

        var outcome = await service.RedeemAsync(Code, CancellationToken.None);

        Assert.True(outcome.Succeeded, outcome.Detail);
        Assert.Equal(RedeemFailure.None, outcome.Failure);
        Assert.Equal(TestTime.Now, outcome.RedeemedAt);
        Assert.NotNull(new LicenseStore(config.Root).Load());
        Assert.False(File.Exists(new PendingRedemptionStore(config.Root).FilePath));

        var sent = Assert.Single(transport.Requests);
        Assert.Equal(Code, sent.RedeemCode);
        Assert.Equal(InstallId, sent.InstallId);
        Assert.Equal("device-digest", sent.DeviceDigest);
        Assert.Equal(RedeemEndpoint.ProductId, sent.ProductId);
        Assert.NotEqual(RedeemCode.NewRequestId(), sent.RequestId);
        Assert.Contains(".invalid", RedeemEndpoint.AbsoluteUrl, StringComparison.Ordinal);
    }

    /// <summary>
    /// The failure this whole design exists for: the server burned the code, the 200
    /// went missing, and the user's next click must be a question rather than another
    /// attempt. Same request id, same outcome — not a 409 telling a paying customer
    /// he redeemed on a machine he is sitting at.
    /// </summary>
    [Fact]
    public async Task TheRetryAfterAnUncertainOutcomeReusesTheSameRequestId()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeRedeemTransport();
        // The first response never arrives: the server may or may not have burned the
        // code, and the client cannot tell.
        transport.Throw(_ => new RedeemTransportException("the connection was reset"));
        transport.Reply(200, Issuing(keys, RedeemStatus.AlreadyRedeemed));
        using var service = Service(config, keys, transport);

        var first = await service.RedeemAsync(Code, CancellationToken.None);

        Assert.False(first.Succeeded);
        Assert.Equal(RedeemFailure.Unreachable, first.Failure);
        Assert.True(first.CanRetry);
        // The record is on disk before anything was sent, which is what the retry has
        // to read back.
        var pending = new PendingRedemptionStore(config.Root).Load();
        Assert.NotNull(pending);

        var second = await service.RedeemAsync(Code, CancellationToken.None);

        Assert.True(second.Succeeded, second.Detail);
        Assert.Equal(2, transport.Requests.Count);
        Assert.Equal(transport.Requests[0].RequestId, transport.Requests[1].RequestId);
        Assert.Equal(pending!.RequestId, transport.Requests[1].RequestId);
        // The server echoed the same code too, or the burn would not have matched.
        Assert.Equal(Code, transport.Requests[1].RedeemCode);
        Assert.NotNull(new LicenseStore(config.Root).Load());
    }

    [Fact]
    public async Task ThePendingRecordNeverHoldsTheCodeItself()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeRedeemTransport();
        transport.Throw(_ => new RedeemTransportException("no route to host"));
        using var service = Service(config, keys, transport);

        await service.RedeemAsync(Code, CancellationToken.None);

        var text = File.ReadAllText(new PendingRedemptionStore(config.Root).FilePath);
        Assert.DoesNotContain("7K3M", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Code, text, StringComparison.Ordinal);
        Assert.Contains(RedeemCode.DigestOf(Code), text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecondCodeIsRefusedWhileOneIsUnresolved()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeRedeemTransport();
        transport.Throw(_ => new RedeemTransportException("timed out"));
        using var service = Service(config, keys, transport);
        await service.RedeemAsync(Code, CancellationToken.None);

        var outcome = await service.RedeemAsync("WCB-ZZZZZ-ZZZZZ-ZZZZZ-ZZZZZ", CancellationToken.None);

        // Starting over would abandon a code the server may already have burned.
        Assert.Equal(RedeemFailure.PendingMismatch, outcome.Failure);
        Assert.False(outcome.CanRetry);
        Assert.Equal(1, transport.PostCount);

        service.DiscardPending();
        Assert.Null(service.Pending);
    }

    [Theory]
    [InlineData(408)]
    [InlineData(425)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task StatusesThatLeaveTheOutcomeUnknownAreRetryable(int statusCode)
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeRedeemTransport();
        transport.Reply(statusCode, "{}");
        using var service = Service(config, keys, transport);

        var outcome = await service.RedeemAsync(Code, CancellationToken.None);

        Assert.Equal(RedeemFailure.Unreachable, outcome.Failure);
        Assert.True(outcome.CanRetry);
        Assert.Null(new LicenseStore(config.Root).Load());
    }

    [Theory]
    [InlineData(RedeemReason.InvalidCode, RedeemFailure.InvalidCode)]
    [InlineData(RedeemReason.AlreadyRedeemedElsewhere, RedeemFailure.AlreadyRedeemedElsewhere)]
    [InlineData(RedeemReason.Expired, RedeemFailure.Expired)]
    [InlineData(RedeemReason.Revoked, RedeemFailure.Revoked)]
    [InlineData(RedeemReason.ProductMismatch, RedeemFailure.ProductMismatch)]
    public async Task ACodeTheServerRuledOnIsNeverOfferedAsRetryable(string reason, RedeemFailure expected)
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeRedeemTransport();
        transport.Reply(409, JsonSerializer.Serialize(new RedeemResponse("rejected", string.Empty, string.Empty, null, reason)));
        using var service = Service(config, keys, transport);

        var outcome = await service.RedeemAsync(Code, CancellationToken.None);

        Assert.Equal(expected, outcome.Failure);
        // The user's next move is a different code or support, never the same button.
        Assert.False(outcome.CanRetry);
        Assert.Null(new LicenseStore(config.Root).Load());
        Assert.False(File.Exists(new PendingRedemptionStore(config.Root).FilePath));
    }

    [Fact]
    public async Task ALicenseThatDoesNotVerifyIsNeverWritten()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        using var attacker = new TestSigningKey();
        var transport = new FakeRedeemTransport();
        // Correctly shaped, correctly signed, by a key this build does not hold.
        transport.Reply(201, Issuing(attacker));
        var log = new List<string>();
        using var service = Service(config, keys, transport, log: log.Add);

        var outcome = await service.RedeemAsync(Code, CancellationToken.None);

        Assert.Equal(RedeemFailure.LicenseUnverifiable, outcome.Failure);
        Assert.Null(new LicenseStore(config.Root).Load());
        // Nothing was sealed: the licence never landed, so the promise has not been
        // kept and there is nothing to be quiet about.
        Assert.False(transport.IsSealed);
        Assert.NotEmpty(log);
    }

    [Fact]
    public async Task ALicenseForAnotherMachineIsNeverWritten()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeRedeemTransport();
        transport.Reply(201, Issuing(keys, RedeemStatus.Redeemed, "some-other-install"));
        using var service = Service(config, keys, transport);

        var outcome = await service.RedeemAsync(Code, CancellationToken.None);

        Assert.Equal(RedeemFailure.SubjectMismatch, outcome.Failure);
        Assert.Null(new LicenseStore(config.Root).Load());
    }

    [Fact]
    public async Task AMalformedCodeNeverReachesTheNetwork()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeRedeemTransport();
        using var service = Service(config, keys, transport);

        var outcome = await service.RedeemAsync("not-a-code", CancellationToken.None);

        Assert.Equal(RedeemFailure.MalformedCode, outcome.Failure);
        Assert.Equal(0, transport.PostCount);
        Assert.False(File.Exists(new PendingRedemptionStore(config.Root).FilePath));
    }

    [Fact]
    public async Task AMachineWithoutAnIdentityCannotRedeem()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeRedeemTransport();
        using var service = Service(config, keys, transport, installId: null);

        var outcome = await service.RedeemAsync(Code, CancellationToken.None);

        Assert.Equal(RedeemFailure.NoInstallId, outcome.Failure);
        Assert.Equal(0, transport.PostCount);
    }

    [Fact]
    public async Task TheChannelIsSealedByTheLicenseBeingPersistedAndNothingEverReachesItAgain()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeRedeemTransport();
        transport.Reply(201, Issuing(keys));
        using var service = Service(config, keys, transport);

        Assert.True((await service.RedeemAsync(Code, CancellationToken.None)).Succeeded);
        Assert.True(transport.IsSealed);
        Assert.NotNull(new LicenseStore(config.Root).Load());

        await Assert.ThrowsAsync<RedeemChannelSealedException>(
            () => service.RedeemAsync(Code, CancellationToken.None));

        // One request, for the whole life of the licence.
        Assert.Equal(1, transport.PostCount);
    }

    /// <summary>
    /// A user cancelling is not an uncertain outcome and must not be dressed up as one:
    /// it propagates rather than becoming a Retry button.
    /// </summary>
    [Fact]
    public async Task ACancelledAttemptIsNotAnOutcome()
    {
        using var config = new TempLicenseConfig();
        using var keys = new TestSigningKey();
        var transport = new FakeRedeemTransport();
        transport.Throw(_ => new RedeemTransportException("never reached"));
        using var service = Service(config, keys, transport);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.RedeemAsync(Code, cancellation.Token));
    }
}
