using System.IO;
using System.Text.Json;

namespace WeChatBridge.Windows.Core.Activation;

/// <summary>Why a login call did not produce a session.</summary>
public enum LoginFailure
{
    None,

    /// <summary>The service could not be reached. The ticket is still good; asking again is free.</summary>
    Unreachable,

    /// <summary>The QR ticket expired before the user scanned it.</summary>
    TicketExpired,

    /// <summary>The user closed the window, or the caller cancelled.</summary>
    Cancelled,

    /// <summary>The deadline passed while waiting for the user to scan.</summary>
    TimedOut,

    /// <summary>The service answered with something this client cannot read.</summary>
    MalformedResponse,

    /// <summary>The stored refresh token is no longer accepted.</summary>
    SessionExpired,
}

/// <summary>The result of one login attempt.</summary>
public sealed record LoginOutcome(LoginFailure Failure, string Detail, AuthTokens? Tokens = null)
{
    public bool Succeeded => Failure == LoginFailure.None && Tokens is not null;

    public static LoginOutcome Success(AuthTokens tokens) =>
        new(LoginFailure.None, "签到成功。", tokens);

    public static LoginOutcome Failed(LoginFailure failure, string detail) => new(failure, detail);
}

/// <summary>
/// The WeChat login: create a QR ticket, wait for the scan, keep the tokens.
/// <para>
/// <b>What this class is responsible for.</b> Turning "the user is looking at a QR
/// code" into a bearer token, and remembering that token afterwards. It deliberately
/// knows nothing about windows, buttons or state machines — the polling loop lives
/// here because it is the part worth testing, and the UI is a rendering of it.
/// </para>
/// <para>
/// <b>What it will not do.</b> It never retries on its own. Every wait here is either
/// driven by the caller or bounded by a deadline the caller supplied, because "the app
/// sent a request while I was doing nothing" is the one failure this product's network
/// promise cannot survive.
/// </para>
/// </summary>
public sealed class AccountService : IDisposable
{
    private readonly IActivationTransport _transport;
    private readonly AuthTokenStore? _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _timeout;
    private readonly Action<string>? _log;
    private readonly PollSchedule _schedule;

    private AuthTokens? _current;

    public AccountService(
        IActivationTransport transport,
        AuthTokenStore? store = null,
        PollSchedule? schedule = null,
        Func<DateTimeOffset>? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? timeout = null,
        Action<string>? log = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _store = store;
        _schedule = schedule ?? PollSchedule.Login;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        _timeout = timeout ?? ActivationEndpoint.DefaultTimeout;
        _log = log;

        // Read the stored session once, at construction. Every later read goes
        // through Current so there is exactly one place that knows whether we are
        // signed in.
        _current = _store?.Load();
    }

    /// <summary>The live session, or null. Never reads from disk again.</summary>
    public AuthTokens? Current => _current;

    /// <summary>The access token to authorise a call with, or null when signed out.</summary>
    public string? BearerToken => _current?.AccessToken;

    public bool IsSignedIn => _current is not null && _current.IsUsableAt(_clock());

    /// <summary>Adopts a session obtained elsewhere and writes it through to disk.</summary>
    public void SignIn(AuthTokens tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        _current = tokens;
        _store?.Save(tokens);
        _log?.Invoke($"account: signed in as {tokens.Account.DisplayName}");
    }

    /// <summary>
    /// Forgets the session. Asks the server to revoke it too, best effort: a
    /// customer who clicked 退出 on a shared machine means it, and a revoke that
    /// fails must not stop the local half.
    /// </summary>
    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        var refresh = _current?.RefreshToken;
        _current = null;
        _store?.Clear();

        if (refresh is null)
            return;
        try
        {
            var json = JsonSerializer.Serialize(new { refreshToken = refresh }, ActivationJson.Options);
            await SendAsync(ActivationEndpoint.SessionPath, "DELETE", json, bearer: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ApiTransportException error)
        {
            // The local session is already gone, which is what the user asked for.
            _log?.Invoke($"account: sign-out revoke did not reach the service ({error.Message}); the local session is gone anyway.");
        }
        catch (ApiException)
        {
            // Same reasoning: the server may refuse to revoke a token it has
            // already forgotten, and that is not worth blocking a sign-out over.
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Asks the service for a QR ticket. Nothing is sent but the install id, which
    /// is a random number this product generated — it ties the ticket to a machine
    /// so a stray scan cannot be redeemed elsewhere.
    /// </summary>
    public async Task<SessionTicket> BeginLoginAsync(string? installId, string? deviceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installId);
        var json = JsonSerializer.Serialize(new { installId, deviceId }, ActivationJson.Options);
        var reply = await SendAsync(ActivationEndpoint.SessionPath, json, bearer: null, cancellationToken).ConfigureAwait(false);

        if (!reply.IsSuccess)
        {
            var failure = ApiFailure.TryParse(reply.Body);
            throw new ApiException(reply.StatusCode,
                failure?.Message ?? $"创建登录会话失败（HTTP {reply.StatusCode}）。",
                failure?.Code ?? ApiErrorCodes.Internal,
                failure?.RequestId ?? "");
        }

        try
        {
            using var document = JsonDocument.Parse(reply.Body);
            var root = document.RootElement;
            return new SessionTicket(
                root.GetProperty("ticket").GetString() ?? throw new JsonException("ticket is null"),
                root.TryGetProperty("authorizeUrl", out var authorize) ? authorize.GetString() ?? "" : "",
                root.TryGetProperty("qrImageUrl", out var qr) ? qr.GetString() ?? "" : "",
                root.TryGetProperty("expiresAt", out var expires) ? expires.GetDateTimeOffset() : _clock());
        }
        catch (JsonException error)
        {
            throw new ApiException(reply.StatusCode, "登录服务返回了无法识别的内容。", ApiErrorCodes.Internal, "", error);
        }
    }

    /// <summary>
    /// Fetches the QR image the user is about to scan.
    /// <para>
    /// Through the transport rather than by handing a URL to the image control: a
    /// window that loads an <c>http</c> URI itself gets a fetch with no deadline and
    /// no audit trail, and the privacy wording says every byte goes through one
    /// place. The path is whatever the server put in <c>qrImageUrl</c>, and the
    /// leading slash is stripped so a relative path resolves against the base address
    /// rather than replacing its last segment.
    /// </para>
    /// </summary>
    public async Task<byte[]> FetchQrAsync(SessionTicket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var path = ticket.QrImagePath.TrimStart('/');
        if (path.Length == 0)
            path = ActivationEndpoint.QrImagePath(ticket.Ticket).TrimStart('/');

        using var deadline = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await _transport.GetBytesAsync(path, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ApiTimeoutException($"登录服务在 {_timeout.TotalSeconds:0} 秒内没有返回二维码。", error);
        }
    }

    /// <summary>One poll of a ticket. Never polls by itself.</summary>
    public async Task<SessionPoll> PollAsync(string ticket, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(ticket);
        var reply = await SendAsync(ActivationEndpoint.SessionTicketPath(ticket), null, bearer: null, cancellationToken)
            .ConfigureAwait(false);

        if (reply.StatusCode == 404 || reply.StatusCode == 410)
            return new SessionPoll(AuthStatus.Expired, null, null);

        if (!reply.IsSuccess)
        {
            var failure = ApiFailure.TryParse(reply.Body);
            throw new ApiException(reply.StatusCode,
                failure?.Message ?? $"查询登录状态失败（HTTP {reply.StatusCode}）。",
                failure?.Code ?? ApiErrorCodes.Internal,
                failure?.RequestId ?? "");
        }

        try
        {
            using var document = JsonDocument.Parse(reply.Body);
            var root = document.RootElement;
            var status = root.GetProperty("status").GetString() ?? AuthStatus.Pending;
            DateTimeOffset? expires = root.TryGetProperty("expiresAt", out var e) && e.ValueKind != JsonValueKind.Null
                ? e.GetDateTimeOffset()
                : null;

            if (!string.Equals(status, AuthStatus.Authorized, StringComparison.Ordinal))
                return new SessionPoll(status, expires, null);

            var tokens = ReadTokens(root);
            return new SessionPoll(AuthStatus.Authorized, expires, tokens);
        }
        catch (JsonException error)
        {
            throw new ApiException(reply.StatusCode, "登录服务返回了无法识别的内容。", ApiErrorCodes.Internal, "", error);
        }
    }

    /// <summary>
    /// Polls until the user scans, the ticket expires, or the deadline passes.
    /// <para>
    /// The delay before each attempt comes from <see cref="PollSchedule"/>, so a
    /// user who walks away leaves the app going quiet rather than going loud. The
    /// injected <c>delay</c> makes that cadence a unit test instead of a sleep.
    /// </para>
    /// </summary>
    public async Task<LoginOutcome> WaitForScanAsync(
        SessionTicket ticket,
        TimeSpan budget,
        Action<SessionPoll>? onPoll = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var deadlineAt = _clock().Add(budget);
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SessionPoll poll;
            try
            {
                poll = await PollAsync(ticket.Ticket, cancellationToken).ConfigureAwait(false);
            }
            catch (ApiTransportException error)
            {
                // A failed poll is not a failed login: the ticket is untouched and the
                // next attempt is free. Give up only when the ticket itself is dead.
                _log?.Invoke($"account: poll {attempt} did not reach the service ({error.Message})");
                if (_clock() >= deadlineAt)
                    return LoginOutcome.Failed(LoginFailure.Unreachable, "没能联系上登录服务，请检查网络后重试。");
                await BackoffAsync(attempt++, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (onPoll is not null)
                onPoll(poll);

            if (poll.IsAuthorized)
            {
                SignIn(poll.Tokens!);
                return LoginOutcome.Success(poll.Tokens!);
            }

            if (poll.IsExpired)
                return LoginOutcome.Failed(LoginFailure.TicketExpired, "二维码已过期，请刷新后重新扫码。");

            if (_clock() >= deadlineAt)
                return LoginOutcome.Failed(LoginFailure.TimedOut, "等待扫码超时，请刷新后重新扫码。");

            await BackoffAsync(attempt++, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Trades a refresh token for a new session. Called when a stored access token
    /// has aged out, which is the common case rather than the exceptional one: it is
    /// how a customer goes months without scanning.
    /// </summary>
    public async Task<LoginOutcome> RefreshAsync(CancellationToken cancellationToken)
    {
        var refresh = _current?.RefreshToken;
        if (refresh is null)
            return LoginOutcome.Failed(LoginFailure.SessionExpired, "尚未登录。");

        var json = JsonSerializer.Serialize(new { refreshToken = refresh }, ActivationJson.Options);
        ApiReply reply;
        try
        {
            reply = await SendAsync(ActivationEndpoint.RefreshPath, json, bearer: null, cancellationToken).ConfigureAwait(false);
        }
        catch (ApiTransportException error)
        {
            return LoginOutcome.Failed(LoginFailure.Unreachable, $"没能联系上登录服务：{error.Message}");
        }

        if (!reply.IsSuccess)
        {
            // The server keeps its own view of whether a refresh token is good; if it
            // says no, the honest thing is to make the user scan again rather than
            // retry with a token that will never work.
            SignOutLocally();
            return LoginOutcome.Failed(LoginFailure.SessionExpired, "登录已过期，请重新扫码登录。");
        }

        try
        {
            using var document = JsonDocument.Parse(reply.Body);
            var tokens = ReadTokens(document.RootElement);
            SignIn(tokens);
            return LoginOutcome.Success(tokens);
        }
        catch (JsonException)
        {
            SignOutLocally();
            return LoginOutcome.Failed(LoginFailure.MalformedResponse, "登录服务返回了无法识别的内容，请重新扫码登录。");
        }
    }

    /// <summary>Drops the session without talking to the service.</summary>
    public void SignOutLocally()
    {
        _current = null;
        _store?.Clear();
    }

    public void Dispose() => _transport.Dispose();

    private Task BackoffAsync(int attempt, CancellationToken cancellationToken) =>
        _delay(_schedule.DelayBefore(attempt), cancellationToken);

    private async Task<ApiReply> SendAsync(string path, string? json, string? bearer, CancellationToken cancellationToken) =>
        await SendAsync(path, json is null ? "GET" : "POST", json, bearer, cancellationToken).ConfigureAwait(false);

    private async Task<ApiReply> SendAsync(string path, string verb, string? json, string? bearer, CancellationToken cancellationToken)
    {
        var request = new ApiRequest(verb, path, json, bearer);

        // The client itself carries no timeout — HttpClient raises the same
        // TaskCanceledException for a timeout and for a user cancel, and after the
        // fact no catch clause can tell them apart. The deadline is a linked token
        // here instead, where the two are still distinguishable.
        using var deadline = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await _transport.SendAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new ApiTimeoutException($"登录服务在 {_timeout.TotalSeconds:0} 秒内没有响应。", error);
        }
    }

    private static AuthTokens ReadTokens(JsonElement root)
    {
        var access = root.GetProperty("accessToken").GetString() ?? throw new JsonException("accessToken is null");
        var refresh = root.GetProperty("refreshToken").GetString() ?? throw new JsonException("refreshToken is null");
        var expires = root.GetProperty("expiresAt").GetDateTimeOffset();

        var accountElement = root.GetProperty("account");
        var account = new AccountView(
            accountElement.GetProperty("id").GetString() ?? "",
            accountElement.TryGetProperty("nickname", out var n) ? n.GetString() ?? "" : "",
            accountElement.TryGetProperty("avatarUrl", out var a) ? a.GetString() ?? "" : "",
            accountElement.TryGetProperty("plan", out var p) ? p.GetString() ?? "" : "",
            accountElement.TryGetProperty("createdAt", out var c) ? c.GetDateTimeOffset() : default,
            accountElement.TryGetProperty("lastLoginAt", out var l) && l.ValueKind != JsonValueKind.Null
                ? l.GetDateTimeOffset()
                : null,
            accountElement.TryGetProperty("wechatBound", out var w) && w.ValueKind == JsonValueKind.True);

        return new AuthTokens(access, refresh, expires, account);
    }
}

/// <summary>
/// The service answered, and the answer was a refusal. Carries the server's own
/// Chinese message, because that text is written for the user and duplicating it
/// here would be a second thing to keep in step.
/// </summary>
public sealed class ApiException : Exception
{
    public ApiException(int statusCode, string message, string code, string requestId, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        Code = code;
        RequestId = requestId;
    }

    public int StatusCode { get; }

    /// <summary>One of <see cref="ApiErrorCodes"/>.</summary>
    public string Code { get; }

    /// <summary>The service's correlation id — the first thing support should ask for.</summary>
    public string RequestId { get; }

    /// <summary>
    /// Whether repeating the call could plausibly work. A 401 is not one of them:
    /// the token is the problem, and asking again with the same token is how a user
    /// ends up believing they are being rate-limited.
    /// </summary>
    public bool IsRetryable => StatusCode is 408 or 425 or 429 || StatusCode >= 500;
}
