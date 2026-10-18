using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using WeChatBridge.Windows.Core.Licensing;
using WeChatBridge.Windows.Core.Redeem;
using WeChatBridge.Windows.Redeem;

namespace WeChatBridge.Windows.AppTests;

/// <summary>
/// The redemption channel end to end, through a real <see cref="HttpClient"/> and a
/// scripted handler.
/// <para>
/// The Core tests drive the flow through an interface; these drive it through the HTTP
/// stack, because two of this design's claims are only visible there: that a request
/// id survives a response that never arrives, and that a sealed channel cannot reach
/// the handler at all. Both are claims about the seam, and a fake behind the seam
/// proves neither.
/// </para>
/// <para>
/// The channel's seal is process-wide, so every test here resets it on the way in and
/// on the way out. The test assembly runs serially for the same reason.
/// </para>
/// </summary>
public sealed class RedeemChannelTests : IDisposable
{
    private const string Code = "WCB-7K3MQ-9XQ2H-4TB6P-WN3DP";
    private const string InstallId = "install-id-for-this-machine";
    private static readonly DateTimeOffset IssuedAt = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _config = Path.Combine(Path.GetTempPath(), "WcbRedeemChannel", Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ScriptedHandler _handler = new();

    public void Dispose()
    {
        HttpRedeemTransport.ResetSealForTests();
        _key.Dispose();
        if (Directory.Exists(_config))
            Directory.Delete(_config, recursive: true);
    }

    private LicenseVerifier Verifier() => new(
        LicensePublicKeys.ImportSubjectPublicKeyInfo,
        new Dictionary<int, string>
        {
            [LicenseToken.CurrentSchemaVersion] = Base64Url.Encode(_key.ExportSubjectPublicKeyInfo()),
        });

    /// <summary>Signs exactly the bytes the client will reconstruct, as the server would.</summary>
    private string Token(string subject)
    {
        var envelope = new LicenseEnvelope
        {
            Subject = subject,
            Plan = "buyout",
            IssuedAt = IssuedAt,
            Entitlements = ["EXPORT", "SCENE"],
        };
        var signature = _key.SignData(
            LicenseCanonicalPayload.From(envelope).Bytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var signed = envelope with { Signature = Base64Url.Encode(signature) };
        return Base64Url.Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(signed)));
    }

    private string Body(string status, string? token = null, string? reason = null) =>
        JsonSerializer.Serialize(new RedeemResponse(status, token ?? string.Empty, "lic-1", IssuedAt, reason));

    private RedeemService Service(HttpRedeemTransport transport) => new(
        new LicenseStore(_config),
        Verifier(),
        new PendingRedemptionStore(_config),
        transport,
        new RedeemMachineIdentity(InstallId, "device-digest"),
        clock: () => IssuedAt);

    /// <summary>
    /// The scenario the whole idempotency record exists for: the server burned the code,
    /// the response was lost, and the user's second click must ask rather than try
    /// again. The assertion that matters is that both requests carry the same
    /// <c>requestId</c> — that is what turns a retry into a lookup, and it is what stops
    /// a paid customer being told his code was spent on a machine he is sitting at.
    /// </summary>
    [Fact]
    public async Task TheRetryAfterAnUncertainOutcomeReusesTheSameRequestId()
    {
        HttpRedeemTransport.ResetSealForTests();
        var token = Token(InstallId);
        // The response never arrives. The server may or may not have burned the code;
        // from here, that is unknowable, which is the whole problem.
        _handler.Throw(new HttpRequestException("the connection was reset"));
        // The replay path: same request id, the original envelope handed back verbatim.
        _handler.Reply(HttpStatusCode.OK, Body(RedeemStatus.AlreadyRedeemed, token));
        using var transport = new HttpRedeemTransport(_handler);
        using var service = Service(transport);

        var first = await service.RedeemAsync(Code, CancellationToken.None);

        Assert.False(first.Succeeded);
        Assert.Equal(RedeemFailure.Unreachable, first.Failure);
        Assert.True(first.CanRetry);

        var second = await service.RedeemAsync(Code, CancellationToken.None);

        Assert.True(second.Succeeded, second.Detail);
        Assert.Equal(2, _handler.RequestCount);
        Assert.Equal(_handler.RequestIds[0], _handler.RequestIds[1]);
        // Both attempts carry the same code as well as the same key. Reading it back
        // out of the body rather than comparing the body to the code is the point:
        // a retry that resends the identical request under the identical requestId is
        // exactly what turns the second burn into a lookup.
        Assert.Equal(Code, RedeemCodeIn(_handler.Bodies[0]));
        Assert.Equal(Code, RedeemCodeIn(_handler.Bodies[1]));
        // The answer landed this time, so the record that made the retry possible has
        // done its job and is gone.
        Assert.False(File.Exists(new PendingRedemptionStore(_config).FilePath));
        Assert.NotNull(new LicenseStore(_config).Load());
    }

    /// <summary>
    /// Once the licence is durable this build makes no further network requests, and the
    /// check happens before the handler — not after, and not by returning an error.
    /// <para>
    /// The handler count is the assertion. If the second call reached it at all, even to
    /// be refused, the promise is broken in the only way that matters: something is on
    /// the wire.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TheRedeemChannelIsSealedTheMomentTheLicenseIsPersisted()
    {
        HttpRedeemTransport.ResetSealForTests();
        _handler.Reply(HttpStatusCode.Created, Body(RedeemStatus.Redeemed, Token(InstallId)));
        using var transport = new HttpRedeemTransport(_handler);
        using var service = Service(transport);

        Assert.True((await service.RedeemAsync(Code, CancellationToken.None)).Succeeded);

        // The licence is on disk and the channel is closed, in that order.
        Assert.NotNull(new LicenseStore(_config).Load());
        Assert.True(HttpRedeemTransport.IsSealedProcessWide);
        Assert.True(transport.IsSealed);
        Assert.Equal(1, _handler.RequestCount);

        await Assert.ThrowsAsync<RedeemChannelSealedException>(
            () => service.RedeemAsync(Code, CancellationToken.None));
        await Assert.ThrowsAsync<RedeemChannelSealedException>(
            () => transport.PostAsync("{}", CancellationToken.None));

        // One request, for the rest of the process's life.
        Assert.Equal(1, _handler.RequestCount);
    }

    /// <summary>
    /// The retry button reuses the id even across a restart of the dialog, because the
    /// record it lives in is on disk rather than in memory.
    /// </summary>
    [Fact]
    public async Task TheRequestIdSurvivesLosingTheWholeProcessState()
    {
        HttpRedeemTransport.ResetSealForTests();
        _handler.Throw(new HttpRequestException("timed out"));
        using (var first = new HttpRedeemTransport(_handler))
        using (var service = Service(first))
            Assert.Equal(RedeemFailure.Unreachable, (await service.RedeemAsync(Code, CancellationToken.None)).Failure);

        var onDisk = new PendingRedemptionStore(_config).Load();
        Assert.NotNull(onDisk);

        _handler.Reply(HttpStatusCode.OK, Body(RedeemStatus.AlreadyRedeemed, Token(InstallId)));
        using var second = new HttpRedeemTransport(_handler);
        using var service2 = Service(second);
        Assert.True((await service2.RedeemAsync(Code, CancellationToken.None)).Succeeded);

        Assert.Equal(onDisk!.RequestId, _handler.RequestIds[1]);
    }

    /// <summary>
    /// The client must never carry its own timeout: its cancellation and a user pressing
    /// Cancel raise the same exception, and the flow tells them apart by which token
    /// fired. A timeout configured on the client would make every network timeout look
    /// like the user giving up.
    /// </summary>
    [Fact]
    public void TheClientNeverCarriesATimeoutOfItsOwn()
    {
        HttpRedeemTransport.ResetSealForTests();
        using var transport = new HttpRedeemTransport(_handler);

        Assert.Equal(Timeout.InfiniteTimeSpan, transport.Client.Timeout);
    }

    [Fact]
    public async Task TheOnlyRequestGoesToTheReservedPlaceholderEndpoint()
    {
        HttpRedeemTransport.ResetSealForTests();
        _handler.Reply(HttpStatusCode.Created, Body(RedeemStatus.Redeemed, Token(InstallId)));
        using var transport = new HttpRedeemTransport(_handler);
        using var service = Service(transport);

        await service.RedeemAsync(Code, CancellationToken.None);

        var uri = Assert.Single(_handler.Uris);
        Assert.NotNull(uri);
        Assert.Equal(new Uri(RedeemEndpoint.AbsoluteUrl), uri);
        // .invalid is an RFC 2606 reserved TLD, so a build that still carries the
        // placeholder fails loudly at the first redemption instead of quietly reaching
        // a host somebody else owns. The host is the label under it, not the TLD.
        Assert.Equal("api.wechatbridge.invalid", uri.Host);
        Assert.EndsWith(".invalid", uri.Host, StringComparison.Ordinal);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("/v1/license/redeem", uri.AbsolutePath);
        Assert.Equal("POST", Assert.Single(_handler.Methods)?.Method);
    }

    /// <summary>
    /// The <c>redeemCode</c> field of a recorded request body.
    /// <para>
    /// The handler stores the body as it went over the wire, which is the whole JSON
    /// document rather than the code, so the field is read back out of it. Comparing
    /// the raw body to a code would only ever pass if the client stopped sending the
    /// other four fields.
    /// </para>
    /// </summary>
    private static string RedeemCodeIn(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("redeemCode").GetString() ?? string.Empty;

    /// <summary>Disposing the transport tears the pool down without sealing the promise.</summary>
    [Fact]
    public void DisposingIsNotSealing()
    {
        HttpRedeemTransport.ResetSealForTests();
        var transport = new HttpRedeemTransport(_handler);

        transport.Dispose();

        // Closing the dialog is not redeeming anything; a later attempt from the same
        // process must still be allowed to reach the network.
        Assert.False(HttpRedeemTransport.IsSealedProcessWide);

        using var later = new HttpRedeemTransport(_handler);
        Assert.False(later.IsSealed);
    }

    /// <summary>
    /// A scripted handler that records what it was asked to send. It is a real
    /// <see cref="HttpMessageHandler"/> rather than an interface fake, because the seal
    /// is enforced above this class in the pipeline and a test that substituted it out
    /// would be testing the substitute.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _script = new();
        private readonly List<string> _requestIds = [];
        private readonly List<string> _bodies = [];
        private readonly List<Uri?> _uris = [];
        private readonly List<HttpMethod?> _methods = [];

        public int RequestCount { get; private set; }

        public IReadOnlyList<string> RequestIds => _requestIds;

        public IReadOnlyList<string> Bodies => _bodies;

        public IReadOnlyList<Uri?> Uris => _uris;

        public IReadOnlyList<HttpMethod?> Methods => _methods;

        public void Reply(HttpStatusCode status, string body) =>
            _script.Enqueue(() => new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });

        public void Throw(Exception failure) => _script.Enqueue(() => throw failure);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            _bodies.Add(body);
            _requestIds.Add(JsonDocument.Parse(body).RootElement.GetProperty("requestId").GetString() ?? string.Empty);
            _uris.Add(request.RequestUri);
            _methods.Add(request.Method);
            cancellationToken.ThrowIfCancellationRequested();

            var next = _script.Count > 0 ? _script.Dequeue() : () => new HttpResponseMessage(HttpStatusCode.InternalServerError);
            return next();
        }
    }
}
