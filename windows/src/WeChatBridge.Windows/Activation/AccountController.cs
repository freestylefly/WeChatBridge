using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Activation;

namespace WeChatBridge.Windows.Activation;

/// <summary>What the login window is currently showing.</summary>
public enum LoginStage
{
    /// <summary>Nothing has been asked for yet.</summary>
    Idle,

    /// <summary>Asking the service for a QR ticket.</summary>
    Requesting,

    /// <summary>Showing the code and waiting for the phone.</summary>
    WaitingScan,

    /// <summary>Signed in.</summary>
    Done,

    /// <summary>Something went wrong and the user has a button to press.</summary>
    Failed,
}

/// <summary>
/// The login flow as a state machine, with no WPF type in sight.
/// <para>
/// Shaped after <c>RedeemController</c>: the window owns no logic, subscribes to
/// <see cref="Changed"/>, and renders whatever this says. The alternative — an
/// <c>async void</c> click handler that awaits three calls and then pokes at named
/// controls — makes the interesting part (what happens when the poll fails, or the
/// ticket expires mid-wait) untestable and unresumable.
/// </para>
/// <para>
/// <b>Cancellation is terminal.</b> Closing the window cancels the wait and that
/// <see cref="CancellationTokenSource"/> is never reused, so a second attempt starts
/// from a clean ticket rather than inheriting a half-finished one.
/// </para>
/// </summary>
public sealed class AccountController : IDisposable
{
    private readonly AccountService _account;
    private readonly string? _installId;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    private byte[]? _qrPng;

    public AccountController(AccountService account, string? installId, Action<string>? log = null)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _installId = installId;
        _log = log;
    }

    public event Action? Changed;

    public LoginStage Stage { get; private set; } = LoginStage.Idle;

    /// <summary>One line, in the user's language, saying what is happening.</summary>
    public string StatusMessage { get; private set; } = L10n.Text("点击下方按钮，用微信扫码登录。");

    /// <summary>The PNG bytes for the QR code, or null before one exists.</summary>
    public byte[]? QrPng => _qrPng;

    /// <summary>The URL the QR encodes. Shown under the code, so a user who cannot scan can still log in.</summary>
    public string AuthorizeUrl { get; private set; } = string.Empty;

    /// <summary>Seconds left on the ticket, for the countdown under the code.</summary>
    public int SecondsRemaining { get; private set; }

    public bool IsBusy => Stage is LoginStage.Requesting;

    /// <summary>The window is waiting on a phone, so the only useful button is 换一个.</summary>
    public bool IsWaiting => Stage == LoginStage.WaitingScan;

    public bool CanStart => !IsBusy && Stage is not LoginStage.WaitingScan;

    public bool CanRetry => Stage == LoginStage.Failed;

    public bool IsSignedIn => _account.IsSignedIn;

    /// <summary>
    /// Asks for a QR ticket, fetches the image, and waits for the scan.
    /// <para>
    /// One method rather than three, because the order is not the caller's to choose:
    /// there is nothing to show without the ticket and nothing to wait for without
    /// the image, and a caller that could interleave them would eventually ask for a
    /// ticket twice and show the user two codes.
    /// </para>
    /// </summary>
    public async Task StartAsync()
    {
        if (_disposed)
            return;

        _qrPng = null;
        AuthorizeUrl = string.Empty;
        SecondsRemaining = 0;
        Stage = LoginStage.Requesting;
        StatusMessage = L10n.Text("正在获取二维码…");
        Raise();

        try
        {
            var ticket = await _account
                .BeginLoginAsync(_installId, Environment.MachineName, _lifetime.Token)
                .ConfigureAwait(true);

            _qrPng = await _account.FetchQrAsync(ticket, _lifetime.Token).ConfigureAwait(true);
            AuthorizeUrl = ticket.AuthorizeUrl;
            Stage = LoginStage.WaitingScan;
            StatusMessage = L10n.Text("请用微信扫描二维码。");
            UpdateCountdown(ticket.ExpiresAt);
            Raise();

            var outcome = await _account
                .WaitForScanAsync(ticket, Remaining(ticket.ExpiresAt), OnPolled, _lifetime.Token)
                .ConfigureAwait(true);

            Stage = outcome.Succeeded ? LoginStage.Done : LoginStage.Failed;
            StatusMessage = outcome.Succeeded
                ? L10n.Format($"已登录：{outcome.Tokens!.Account.DisplayName}")
                : outcome.Detail;
            Raise();
        }
        catch (OperationCanceledException)
        {
            // The window closed. There is nobody left to tell.
        }
        catch (ApiTimeoutException error)
        {
            Fail(error.Message);
        }
        catch (ApiTransportException error)
        {
            Fail(L10n.Format($"没能联系上登录服务：{error.Message}"));
        }
        catch (ApiException error)
        {
            Fail(error.Message);
        }
    }

    /// <summary>Discards the current ticket and starts again. Same as <see cref="StartAsync"/>.</summary>
    public Task RetryAsync() => StartAsync();

    /// <summary>
    /// Ends the wait. Cooperative and final — the token is not reset, so a later
    /// call gets a fresh one.
    /// </summary>
    public void Cancel()
    {
        if (!_lifetime.IsCancellationRequested)
            _lifetime.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Cancel();
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Refreshes the countdown on every poll tick. Called from the poll loop, so it
    /// is the only thing that updates between polls.
    /// </summary>
    private void OnPolled(SessionPoll poll)
    {
        if (poll.IsPending && poll.ExpiresAt is { } expires)
        {
            UpdateCountdown(expires);
            Raise();
        }
    }

    private void UpdateCountdown(DateTimeOffset expiresAt) =>
        SecondsRemaining = (int)Math.Max(0, Math.Round((expiresAt - DateTimeOffset.UtcNow).TotalSeconds));

    private TimeSpan Remaining(DateTimeOffset expiresAt)
    {
        var left = expiresAt - DateTimeOffset.UtcNow;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private void Fail(string message)
    {
        _log?.Invoke($"account: {message}");
        Stage = LoginStage.Failed;
        StatusMessage = message;
        Raise();
    }

    private void Raise() => Changed?.Invoke();
}
