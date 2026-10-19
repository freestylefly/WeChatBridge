using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Licensing;
using WeChatBridge.Windows.Core.Redeem;
using WeChatBridge.Windows.Services;

namespace WeChatBridge.Windows.Redeem;

/// <summary>Which face of the redemption window is showing.</summary>
public enum RedeemStage
{
    /// <summary>Nothing has been submitted yet.</summary>
    Idle,

    /// <summary>A request is in flight.</summary>
    Working,

    /// <summary>
    /// The outcome is unknown and asking again is the right move. This is the stage
    /// that reuses the request id from disk, so it is a question and not a second burn.
    /// </summary>
    CanRetry,

    /// <summary>
    /// The server answered, and the answer was about the code rather than the wire.
    /// The user's next move is a different code or a support call, never the same
    /// button again.
    /// </summary>
    NeedsNewCode,

    /// <summary>Licence obtained and written.</summary>
    Done,

    /// <summary>Redemption is not available here, and the window says why.</summary>
    Unavailable,
}

/// <summary>
/// The redemption flow as a state machine, with no WPF types in sight.
/// <para>
/// The window is a shell around this: it renders <see cref="Stage"/> and
/// <see cref="StatusMessage"/>, and forwards clicks. Keeping the decisions here is what
/// lets "which button for which failure" be asserted without a window, and that rule is
/// the whole reason the two classes of failure are separated — a Retry on an invalid
/// code is not a slower path to success, it is the wrong button.
/// </para>
/// <para>
/// Cancellation is cooperative and terminal: <see cref="Cancel"/> stops the in-flight
/// attempt from touching the UI afterwards, so closing the window leaves no task
/// updating a closed dialog.
/// </para>
/// </summary>
public sealed class RedeemController : IDisposable
{
    private readonly RedeemService _service;
    private readonly PendingRedemptionStore _pending;
    private readonly Action _refreshPro;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _lifetime;
    private string _code = string.Empty;
    private bool _stopped;

    public RedeemController(
        RedeemService service,
        PendingRedemptionStore pending,
        Action refreshPro,
        Action<string>? log = null)
    {
        _service = service;
        _pending = pending;
        _refreshPro = refreshPro;
        _log = log;
        _lifetime = new CancellationTokenSource();
    }

    /// <summary>
    /// Builds the controller the application uses, over <see cref="LicensingBootstrap.Current"/>.
    /// <para>
    /// The install id is read from the resolution rather than from a second identity
    /// store: the licence is going to be bound to whatever that resolver settled on,
    /// and two readers of the same file could disagree about it.
    /// </para>
    /// </summary>
    internal static RedeemController Create(LicensingBootstrap licensing, Action<string>? log = null)
    {
        var pending = new PendingRedemptionStore(licensing.Layout.Config);
        var machine = new RedeemMachineIdentity(
            licensing.Resolution.InstallId,
            RedeemMachineIdentity.DigestOf(RegistryMachineFacts.CreateDefault().Read()));
        var service = new RedeemService(
            licensing.Store,
            new LicenseVerifier(),
            pending,
            new HttpRedeemTransport(),
            machine,
            log: log);
        return new RedeemController(service, pending, () => licensing.Refresh(), log);
    }

    /// <summary>Raised whenever <see cref="Stage"/> or <see cref="StatusMessage"/> changed.</summary>
    public event Action? Changed;

    public RedeemStage Stage { get; private set; } = RedeemStage.Idle;

    /// <summary>What the window shows under the input, already localised.</summary>
    public string StatusMessage { get; private set; } = string.Empty;

    /// <summary>When the server says it burned the code, for the success line.</summary>
    public DateTimeOffset? IssuedAt { get; private set; }

    /// <summary>
    /// A redemption started earlier and not yet answered. Shown, never retried on its
    /// own: "the app sent a request while I was doing nothing" is exactly what the
    /// buy-out edition's promise rules out. The user opens the window and presses the
    /// button; that is the whole recovery path.
    /// </summary>
    public bool HasUnresolvedPending => _pending.Load() is not null;

    /// <summary>The code, normalised for display as the user types.</summary>
    public string Code
    {
        get => _code;
        set
        {
            var normalized = RedeemCode.FormatForDisplay(value);
            if (string.Equals(normalized, _code, StringComparison.Ordinal))
                return;
            _code = normalized;
            // Typing a different code is the answer to every "needs a new code" state,
            // so the state goes back to Idle and the error goes away with it.
            if (Stage is RedeemStage.NeedsNewCode or RedeemStage.CanRetry)
                Stage = RedeemStage.Idle;
            Raise();
        }
    }

    public bool IsBusy => Stage == RedeemStage.Working;

    /// <summary>Whether the code box accepts typing. Disabled while a request is in flight.</summary>
    public bool CanEditCode => Stage is RedeemStage.Idle or RedeemStage.NeedsNewCode or RedeemStage.CanRetry;

    /// <summary>Whether the primary button is actionable.</summary>
    public bool CanSubmit => Stage == RedeemStage.Idle && RedeemCode.IsValid(_code);

    /// <summary>Whether the Retry button is showing. Only ever true for an unknown outcome.</summary>
    public bool CanRetry => Stage == RedeemStage.CanRetry;

    /// <summary>Whether the "give this one up" button is showing.</summary>
    public bool CanDiscard => Stage == RedeemStage.NeedsNewCode;

    /// <summary>Whether the code box and its buttons are showing at all.</summary>
    public bool ShowsCodeEditor => Stage is not (RedeemStage.Done or RedeemStage.Unavailable);

    /// <summary>
    /// Refuses to offer a redemption this build cannot complete: already Pro, a
    /// portable stick, or a channel a previous attempt sealed.
    /// </summary>
    public RedeemOutcome MarkUnavailable(string reason)
    {
        Stage = RedeemStage.Unavailable;
        StatusMessage = L10n.Text(reason);
        Raise();
        return RedeemOutcome.Failed(RedeemFailure.Rejected, false, reason);
    }

    /// <summary>Sets the status line. The message is already localised.</summary>
    public void SetStatus(string message)
    {
        StatusMessage = message;
        Raise();
    }

    /// <summary>
    /// One attempt. Returns the outcome so a caller that wants the raw detail can have
    /// it; the window reads the state instead.
    /// </summary>
    public async Task<RedeemOutcome> SubmitAsync()
    {
        if (_stopped)
            return RedeemOutcome.Failed(RedeemFailure.None, false, "closed");

        Stage = RedeemStage.Working;
        SetStatus(L10n.Text("正在兑换…请不要关闭窗口。"));

        RedeemOutcome outcome;
        try
        {
            outcome = await _service.RedeemAsync(_code, _lifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The window is gone or the user asked to stop. Nothing left to render.
            return RedeemOutcome.Failed(RedeemFailure.None, false, "cancelled");
        }
        catch (RedeemChannelSealedException error)
        {
            MarkUnavailable("兑换通道已经关闭：许可证已写入，本次不会再发起任何网络请求。");
            return RedeemOutcome.Failed(RedeemFailure.Rejected, false, error.Message);
        }

        Apply(outcome);
        return outcome;
    }

    /// <summary>
    /// Asks again about the same code. Reuses the request id written before the first
    /// attempt, which is the entire reason this is safe: the server is being asked what
    /// it already decided, not asked to decide again.
    /// </summary>
    public Task<RedeemOutcome> RetryAsync() => SubmitAsync();

    /// <summary>Abandons an unresolved attempt so a different code can be redeemed.</summary>
    public void DiscardPending()
    {
        _service.DiscardPending();
        Stage = RedeemStage.Idle;
        SetStatus(L10n.Text("已放弃上一次兑换。请输入新的兑换码。"));
    }

    /// <summary>Stops the in-flight attempt. Safe to call more than once.</summary>
    public void Cancel()
    {
        if (_stopped)
            return;
        _stopped = true;
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Raced with Dispose. Cancellation is best effort by definition.
        }
        _lifetime.Dispose();
    }

    public void Dispose()
    {
        if (_stopped)
            return;
        Cancel();
        _service.Dispose();
    }

    private void Apply(RedeemOutcome outcome)
    {
        if (_stopped)
            return;

        if (outcome.Succeeded)
        {
            Stage = RedeemStage.Done;
            IssuedAt = outcome.RedeemedAt ?? outcome.License?.IssuedAt;
            // The licence is on disk and the resolver's cached answer is stale; every
            // pane in this process reads it.
            _refreshPro();
            _log?.Invoke("redeem finished: the licence was written and the channel sealed");
            // Formatted here rather than left as "{0:...}" in the key: L10n.Format
            // interpolates its arguments into the format template, and the localisation
            // test formats every placeholder with a DateTime, so a plain slot keeps the
            // key honest in both languages.
            var stamp = (IssuedAt ?? DateTimeOffset.UnixEpoch).ToString("yyyy-MM-dd HH:mm", L10n.Culture);
            SetStatus(L10n.Format($"兑换成功。签发时间：{stamp}。本机 Pro 功能已解锁，此后不会再发起任何网络请求。"));
            return;
        }

        // Two buttons, not one button with a longer wait. Retryable means the wire may
        // have swallowed the answer; non-retryable means the server has ruled on this
        // code and ruling again changes nothing.
        Stage = outcome.CanRetry ? RedeemStage.CanRetry : RedeemStage.NeedsNewCode;
        StatusMessage = MessageFor(outcome.Failure);
        _log?.Invoke($"redeem failed: {outcome.Failure} ({outcome.Detail})");
        Raise();
    }

    /// <summary>
    /// The localised wording for each failure, with no machine-readable detail in it.
    /// <para>
    /// The detail goes to the log and to nobody's screen. Half of what the service can
    /// produce is an HTTP status line or a framework exception message, and a paid
    /// customer reading "The remote name could not be resolved" learns nothing they can
    /// act on — while a support ticket carrying the same string in the log tells us
    /// everything.
    /// </para>
    /// </summary>
    public static string MessageFor(RedeemFailure failure) => failure switch
    {
        RedeemFailure.MalformedCode => L10n.Text("兑换码格式不对。正确格式是 WCB-XXXXX-XXXXX-XXXXX-XXXXX，可以直接从订单邮件里复制。"),
        RedeemFailure.InvalidCode => L10n.Text("兑换码无效。请核对订单邮件里的那串字符，注意 0 与 O、1 与 I 的区别。"),
        RedeemFailure.AlreadyRedeemedElsewhere => L10n.Text("这个兑换码已经在另一台设备上使用过了。请联系客服解绑，再换一个新码。"),
        RedeemFailure.Expired => L10n.Text("兑换码已过期（有效期 90 天）。请联系客服延长有效期或更换。"),
        RedeemFailure.Revoked => L10n.Text("兑换码已作废。请联系客服。"),
        RedeemFailure.ProductMismatch => L10n.Text("兑换码不属于本产品。请确认买的是买断版。"),
        RedeemFailure.RateLimited => L10n.Text("尝试次数过多。请过一会儿再来。"),
        RedeemFailure.Unreachable => L10n.Text("没能联系上兑换服务。这不表示兑换失败：点「重试」会带上同一个请求编号再问一次，不会重复发证。"),
        RedeemFailure.NoInstallId => L10n.Text("这台机器还没有建立设备身份。请重启微信流后再试。"),
        RedeemFailure.PendingMismatch => L10n.Text("上一次兑换还没有结果。请先用那个兑换码继续，或点「放弃」换一个。"),
        RedeemFailure.MalformedResponse => L10n.Text("服务端返回的内容无法解析。请联系客服，并说明这发生在兑换的最后一步。"),
        RedeemFailure.LicenseUnverifiable => L10n.Text("拿到的许可证签名校验没有通过。这属于我们的问题，请联系客服。"),
        RedeemFailure.SubjectMismatch => L10n.Text("拿到的许可证不属于这台机器。请联系客服。"),
        _ => L10n.Text("兑换失败，请联系客服。"),
    };

    private void Raise()
    {
        if (_stopped)
            return;
        Changed?.Invoke();
    }
}
