using System.Text.Json;
using WeChatBridge.Windows.Core.Licensing;

namespace WeChatBridge.Windows.Core.Activation;

/// <summary>Why an activation call did not finish a purchase.</summary>
public enum ActivationFailure
{
    None,

    /// <summary>The service could not be reached. Repeating is free: registration is idempotent per ticket.</summary>
    Unreachable,

    /// <summary>The seller declined. The registration stays on record so it can be looked at later.</summary>
    Rejected,

    /// <summary>The deadline passed. The registration is still pending server-side.</summary>
    TimedOut,

    /// <summary>The licence came back but does not verify against the embedded key.</summary>
    LicenseUnverifiable,

    /// <summary>The licence is bound to a different install id than this machine's.</summary>
    SubjectMismatch,

    /// <summary>The response could not be read.</summary>
    MalformedResponse,

    /// <summary>Not signed in, so there is no account to buy against.</summary>
    NotSignedIn,

    /// <summary>This machine has no install identity, so there is nothing to bind a licence to.</summary>
    NoInstallId,
}

/// <summary>What one activation call produced.</summary>
public sealed record ActivationOutcome(
    ActivationFailure Failure,
    bool CanRetry,
    string Detail,
    LicenseEnvelope? License = null,
    DateTimeOffset? IssuedAt = null)
{
    public bool Succeeded => Failure == ActivationFailure.None;

    public static ActivationOutcome Success(LicenseEnvelope license, DateTimeOffset? issuedAt, string detail) =>
        new(ActivationFailure.None, false, detail, license, issuedAt);

    public static ActivationOutcome Failed(ActivationFailure failure, bool canRetry, string detail) =>
        new(failure, canRetry, detail);
}

/// <summary>
/// The buy-out purchase: register this machine, wait for the seller to confirm a
/// payment, collect the licence, verify it offline, keep it.
/// <para>
/// <b>The order of operations in <see cref="Collect"/> is the load-bearing part.</b>
/// The licence is decoded, verified against the embedded public key, and checked
/// against this machine's install id <i>before</i> it is written to disk — in that
/// order, with no <c>await</c> between the check and the write. A licence that
/// verifies but binds to someone else's machine must never reach
/// <see cref="LicenseStore"/>, because everything downstream treats a file there as
/// a purchase; and a licence that does not verify must never be stored even once, or
/// the user is left with an app that says no and a file it cannot delete.
/// </para>
/// <para>
/// <b>No payment goes through this class.</b> The design is deliberate: the client
/// shows the seller's static payment code, the buyer scans it in WeChat, and the
/// seller checks their own 微信零账. There is no payment API to integrate, no money
/// this code can move, and no order it can lose.
/// </para>
/// </summary>
public sealed class ActivationService
{
    private readonly LicenseStore _licenses;
    private readonly LicenseVerifier _verifier;
    private readonly IActivationTransport _transport;
    private readonly AccountService _account;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _timeout;
    private readonly Action<string>? _log;

    public ActivationService(
        LicenseStore licenses,
        LicenseVerifier verifier,
        IActivationTransport transport,
        AccountService account,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? timeout = null,
        Action<string>? log = null)
    {
        _licenses = licenses ?? throw new ArgumentNullException(nameof(licenses));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _timeout = timeout ?? ActivationEndpoint.DefaultTimeout;
        _log = log;
    }

    /// <summary>
    /// Registers this machine for a purchase.
    /// <para>
    /// <paramref name="anchorDigest"/> is the hardware anchor's digest, which is
    /// evidence for the <i>seller</i> — it is how a re-install is told apart from a
    /// different machine. It is not the primary key and cannot be: MAC addresses
    /// change with the network card, and a licence that stopped working because
    /// someone moved machines would punish exactly the customer the product is
    /// supposed to serve. The binding stays on <paramref name="installId"/>.
    /// </para>
    /// </summary>
    public async Task<ActivationOutcome> RegisterAsync(
        string installId,
        string anchorDigest,
        int? amountFen,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(installId))
            return ActivationOutcome.Failed(ActivationFailure.NoInstallId, false, "本机尚未生成机器码，无法登记购买。");
        var token = RequireToken();
        if (token is null)
            return ActivationOutcome.Failed(ActivationFailure.NotSignedIn, false, "请先登录微信账号。");

        var json = JsonSerializer.Serialize(
            new { installId, anchorDigest, amountFen }, ActivationJson.Options);
        var reply = await SendAsync(ActivationEndpoint.ActivationRequestPath, json, token, cancellationToken)
            .ConfigureAwait(false);

        if (!reply.IsSuccess)
            return Classify(reply, "登记购买失败，请稍后重试。");

        try
        {
            using var document = JsonDocument.Parse(reply.Body);
            var requestId = document.RootElement.GetProperty("requestId").GetString() ?? "";
            var status = document.RootElement.TryGetProperty("status", out var s) ? s.GetString() ?? "" : ActivationState.Pending;
            _log?.Invoke($"activation: registered {requestId} for install {installId}");
            return ActivationOutcome.Failed(ActivationFailure.None, true, $"已登记，等待卖家确认（单号 {requestId}）。");
        }
        catch (JsonException)
        {
            return ActivationOutcome.Failed(ActivationFailure.MalformedResponse, true, "登记结果无法识别，请稍后重试。");
        }
    }

    /// <summary>One status poll. Never polls by itself.</summary>
    public async Task<ActivationOutcome> CheckStatusAsync(CancellationToken cancellationToken)
    {
        var token = RequireToken();
        if (token is null)
            return ActivationOutcome.Failed(ActivationFailure.NotSignedIn, false, "请先登录微信账号。");

        var reply = await SendAsync(ActivationEndpoint.ActivationStatusPath, null, token, cancellationToken)
            .ConfigureAwait(false);

        if (reply.StatusCode == 404)
        {
            // No registration at all. The mock and the real handler agree that this
            // is a "not found", and for a user it means the same thing: nothing is
            // in flight, so the purchase page should offer to register.
            return ActivationOutcome.Failed(ActivationFailure.None, true, "还没有购买登记。");
        }

        if (!reply.IsSuccess)
            return Classify(reply, "查询授权状态失败，请稍后重试。");

        try
        {
            using var document = JsonDocument.Parse(reply.Body);
            var root = document.RootElement;
            var status = root.GetProperty("status").GetString() ?? ActivationState.Pending;
            var licenseId = root.TryGetProperty("licenseId", out var l) && l.ValueKind == JsonValueKind.String
                ? l.GetString()
                : null;

            if (string.Equals(status, ActivationState.Confirmed, StringComparison.Ordinal))
                return ActivationOutcome.Failed(ActivationFailure.None, true, "卖家已确认，正在取许可证…");
            if (string.Equals(status, ActivationState.Rejected, StringComparison.Ordinal))
                return ActivationOutcome.Failed(ActivationFailure.Rejected, false,
                    "卖家拒绝了本次登记。如已付款，请联系客服。");

            return ActivationOutcome.Failed(ActivationFailure.None, true, $"等待卖家确认（许可证 {licenseId ?? "待签发"}）。");
        }
        catch (JsonException)
        {
            return ActivationOutcome.Failed(ActivationFailure.MalformedResponse, true, "授权状态无法识别，请稍后重试。");
        }
    }

    /// <summary>
    /// Collects the licence the seller issued, verifies it, and writes it to disk.
    /// <para>
    /// A 409 here means confirmed-but-not-yet-readable, which is a real state: the
    /// handler signs on confirm and the read can lose that race. It is treated as
    /// retryable rather than as an error, because the next poll resolves it.
    /// </para>
    /// </summary>
    public async Task<ActivationOutcome> CollectAsync(string? installId, CancellationToken cancellationToken)
    {
        var token = RequireToken();
        if (token is null)
            return ActivationOutcome.Failed(ActivationFailure.NotSignedIn, false, "请先登录微信账号。");

        var reply = await SendAsync(ActivationEndpoint.ActivationLicencePath, null, token, cancellationToken)
            .ConfigureAwait(false);

        if (reply.StatusCode == 409)
            return ActivationOutcome.Failed(ActivationFailure.None, true, "许可证正在签发，请稍候。");
        if (!reply.IsSuccess)
            return Classify(reply, "获取许可证失败，请稍后重试。");

        LicenseEnvelope? envelope;
        try
        {
            using var document = JsonDocument.Parse(reply.Body);
            envelope = document.RootElement.GetProperty("envelope").Deserialize<LicenseEnvelope>(ActivationJson.Options);
        }
        catch (JsonException)
        {
            return ActivationOutcome.Failed(ActivationFailure.MalformedResponse, true, "许可证内容无法识别，请稍后重试。");
        }

        if (envelope is null)
            return ActivationOutcome.Failed(ActivationFailure.MalformedResponse, true, "许可证内容无法识别，请稍后重试。");

        return Accept(envelope, installId);
    }

    /// <summary>
    /// Verify, then write. No <c>await</c> between the two, and no ordering other
    /// than this one — see the class comment for why.
    /// </summary>
    private ActivationOutcome Accept(LicenseEnvelope envelope, string? installId)
    {
        var now = _clock();
        var verification = _verifier.Verify(envelope, now, LicenseRuntimeOptions.Installed.ClockSkewTolerance);
        if (!verification.IsValid)
        {
            _log?.Invoke($"activation: licence refused — {verification.Failure}");
            return ActivationOutcome.Failed(ActivationFailure.LicenseUnverifiable, true,
                "许可证验签未通过，请联系客服并提供单号。");
        }

        if (!string.IsNullOrWhiteSpace(installId) &&
            !string.Equals(envelope.Subject, installId, StringComparison.Ordinal))
        {
            // Reached only when the signature is genuine, which means the seller
            // signed for a different machine. Worth saying out loud: it is the one
            // case where the client is certain the service is right and the local
            // machine is the odd one out.
            _log?.Invoke($"activation: licence subject {envelope.Subject} is not this machine ({installId})");
            return ActivationOutcome.Failed(ActivationFailure.SubjectMismatch, false,
                "这张许可证属于另一台机器，请联系客服换机重签。");
        }

        _licenses.Save(envelope);
        _log?.Invoke($"activation: licence {envelope.Subject} written");
        return ActivationOutcome.Success(envelope, envelope.IssuedAt, "授权成功，感谢购买。");
    }

    private string? RequireToken()
    {
        var token = _account.BearerToken;
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static ActivationOutcome Classify(ApiReply reply, string fallback)
    {
        var failure = ApiFailure.TryParse(reply.Body);
        var code = failure?.Code ?? ApiErrorCodes.Internal;
        var retryable = reply.StatusCode is 408 or 425 or 429 || reply.StatusCode >= 500;
        var detail = failure?.Message ?? $"{fallback}（HTTP {reply.StatusCode}）";

        var kind = code switch
        {
            ApiErrorCodes.Unauthorized => ActivationFailure.NotSignedIn,
            ApiErrorCodes.Conflict => ActivationFailure.None,
            _ => retryable ? ActivationFailure.Unreachable : ActivationFailure.MalformedResponse,
        };
        return ActivationOutcome.Failed(kind, retryable, detail);
    }

    private async Task<ApiReply> SendAsync(string path, string? json, string bearer, CancellationToken cancellationToken)
    {
        var verb = json is null ? "GET" : "POST";

        // No client timeout: HttpClient's own raises the same TaskCanceledException
        // as the user pressing cancel, and no catch clause can tell them apart
        // afterwards. The deadline lives here, where the two still can.
        using var deadline = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var request = new ApiRequest(verb, path, json, bearer);
        return await _transport.SendAsync(request, linked.Token).ConfigureAwait(false);
    }
}
