namespace WeChatBridge.Windows.Core.Delivery;

/// <summary>Why a forward did not arrive — the Detail on a failed BatchOutcome.</summary>
public enum DeliveryFailureKind
{
    /// <summary>The action names no app and none was supplied.</summary>
    NoTarget,
    /// <summary>No running process, exe candidate or AUMID resolved.</summary>
    NotInstalled,
    /// <summary>The target never owned the foreground within the timeout.</summary>
    DidNotBecomeActive,
    /// <summary>A clipboard write was refused mid-plan.</summary>
    ClipboardWriteFailed,
    /// <summary>SendInput returned fewer events than it was given.</summary>
    KeystrokeRejected,
}

/// <summary>
/// What a forward attempt ended in. <see cref="Outcome"/> is the record the
/// history keeps; <see cref="Plan"/> is what was (or would have been) pasted,
/// kept so the caller can describe the fallback accurately.
/// </summary>
public sealed record DeliveryResult(
    BatchOutcome Outcome,
    string? TargetName,
    IReadOnlyList<PastePayload> Plan,
    DeliveryFailureKind? Failure)
{
    public bool Delivered => Outcome.Kind == BatchOutcomeKind.Delivered;
}

/// <summary>
/// The forward half of the product: given a committed Ready batch, bring the
/// target app forward and paste into it. Ported from <c>ActionRunner.forward</c>
/// + <c>AutoPaste.activateAndPaste</c>.
///
/// Two macOS mechanisms have no Windows counterpart, deliberately:
///
/// • Accessibility permission. macOS gates the synthetic ⌘V on
///   <c>AXIsProcessTrusted</c> and degrades to "files on clipboard" when
///   refused. Windows SendInput/SetForegroundWindow need no permission, so the
///   not-trusted branch collapses: the clipboard fallback is written *before*
///   activation, which is also what makes every failure survivable.
///
/// • AX focus/raise of the composer field (<c>focusTextInput</c>). Driving UIA
///   into a target's text box is not ported here; the foreground settle covers
///   the common case. A target that swallows the paste is a product-level gap
///   to revisit per app, as the original did for WeSight.
///
/// Every path leaves the batch's files (or the path-only line) on the
/// clipboard — the user's fallback is always one Ctrl+V away, exactly as on
/// macOS. The engine never throws for a delivery failure; it returns a
/// <see cref="DeliveryResult"/> and records the outcome itself when an
/// <see cref="InboxReader"/> is attached.
/// </summary>
public sealed class DeliveryEngine
{
    /// <summary>The macOS waits: frontmost within 4 s, polled at 60 ms.</summary>
    public static readonly TimeSpan ForegroundTimeout = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan ForegroundPoll = TimeSpan.FromMilliseconds(60);

    /// <summary>
    /// A short settle after the app is frontmost, so the window has had a turn
    /// of its own message loop to focus its input field — the Windows reading of
    /// macOS's 350 ms pause.
    /// </summary>
    public static readonly TimeSpan ForegroundSettle = TimeSpan.FromMilliseconds(350);

    /// <summary>
    /// Between the two pastes of a prompt-plus-files plan: the target has to
    /// take the first one before the clipboard is rewritten under it.
    /// </summary>
    public static readonly TimeSpan BetweenPastes = TimeSpan.FromMilliseconds(450);

    /// <summary>
    /// After a <see cref="WindowsForwardTarget.PrePasteHotkey"/>: Doubao's new
    /// 工作任务 view needs a beat to render its composer before the paste.
    /// </summary>
    public static readonly TimeSpan PrePasteHotkeySettle = TimeSpan.FromMilliseconds(700);

    private readonly DeliveryEnvironment _env;
    private readonly InboxReader? _inbox;
    private readonly IReadOnlyDictionary<ShareAction, WindowsForwardTarget> _targets;

    /// <param name="environment">OS touch points; null wires the real Win32 ones.</param>
    /// <param name="inbox">When present, outcomes are recorded into state.json.</param>
    /// <param name="targets">Table override — tests and future exe-name fixes never touch the engine.</param>
    public DeliveryEngine(
        DeliveryEnvironment? environment = null,
        InboxReader? inbox = null,
        IReadOnlyDictionary<ShareAction, WindowsForwardTarget>? targets = null)
    {
        _env = environment ?? DeliveryEnvironment.Create();
        _inbox = inbox;
        _targets = targets ?? WindowsForwardTargets.Defaults;
    }

    /// <summary>Fixed-entry forward: the action itself names the app.</summary>
    public Task<DeliveryResult> DeliverAsync(ReadyBatch batch, string? prompt = null, CancellationToken ct = default) =>
        DeliverAsync(batch, customTarget: null, prompt, ct);

    /// <summary>Forward driven by the consumed intent — carries a custom target when 发送到自定义 was aimed.</summary>
    public Task<DeliveryResult> DeliverAsync(ReadyBatch batch, BatchIntent intent, string? prompt = null, CancellationToken ct = default) =>
        DeliverAsync(batch, WindowsForwardTargets.IntentTarget(intent), prompt, ct);

    /// <summary>The share-action → target-spec mapping, shared by resolve and pre-resolve.</summary>
    private WindowsForwardTarget? SpecFor(ReadyBatch batch, ForwardTarget? customTarget) =>
        batch.Action == ShareAction.Custom && customTarget is not null
            ? WindowsForwardTargets.ForCustom(customTarget)
            : _targets.TryGetValue(batch.Action, out var fixedTarget) ? fixedTarget : null;

    /// <summary>
    /// Starts target resolution on a worker thread so it overlaps whatever the
    /// caller is still waiting on — the scene decision — rather than running
    /// inside <see cref="DeliverAsync"/>'s critical path. Process scans and PATH
    /// probing are pure reads, safe off the caller's thread; the result feeds
    /// <see cref="DeliverAsync(ReadyBatch, ForwardTarget?, string?, CancellationToken, Task{ResolvedTarget?}?)"/>.
    /// Null when the batch's action names no deliverable target — the caller
    /// then passes nothing and delivery does its own (failing) resolve.
    /// </summary>
    public Task<ResolvedTarget?>? BeginResolve(ReadyBatch batch, ForwardTarget? customTarget)
    {
        var spec = SpecFor(batch, customTarget);
        return spec is null ? null : Task.Run(() => _env.ResolveTarget(spec));
    }

    /// <summary>
    /// Resolve → activate → wait foreground → paste → record.
    ///
    /// Freshness (<see cref="BatchIntent.FreshnessWindow"/>) is the caller's
    /// check, made when the intent is consumed — by the time delivery runs, the
    /// request was already judged worth doing. <paramref name="preresolved"/> is
    /// an in-flight <see cref="BeginResolve"/> — awaited here instead of
    /// resolving anew, so its head start is not wasted.
    /// </summary>
    public async Task<DeliveryResult> DeliverAsync(
        ReadyBatch batch,
        ForwardTarget? customTarget,
        string? prompt = null,
        CancellationToken ct = default,
        Task<ResolvedTarget?>? preresolved = null)
    {
        var paths = batch.Items.Select(i => i.FullPath).ToArray();
        var spec = SpecFor(batch, customTarget);
        var targetName = customTarget?.DisplayName ?? spec?.DisplayName ?? batch.Action.TargetDisplayName();

        if (spec is null)
        {
            return Fail(batch, DeliveryFailureKind.NoTarget,
                "这条转发没有指定目标 App。", targetName, [new PastePayload.Files(paths)]);
        }

        // The files, or their paths as text for an app — a terminal — that
        // cannot take a pasted file. The custom target's checkbox owns this;
        // ReadsLocalArchives ports Doubao's agent-mode shortcut from macOS.
        var pathOnly = customTarget?.PastesPathOnly
            ?? (spec.ReadsLocalArchives && paths.Any(p => p.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)));

        var plan = PastePlan.Make(paths, pathOnly, prompt);

        // Written before activation, not after failure: whatever happens next —
        // a refused launch, a window that never comes forward, a Ctrl+V that
        // goes nowhere — the user is one manual paste away from their files.
        WriteManualPayload(plan, paths);

        var resolved = preresolved is not null
            ? await preresolved.ConfigureAwait(false)
            : _env.ResolveTarget(spec);
        _env.Log?.Invoke($"[deliver {batch.Id:N}] resolve {targetName}{(preresolved is not null ? " (pre)" : "")}: {(resolved is null ? "null" : $"pid={resolved.ProcessId?.ToString() ?? "null"} hwnd={resolved.MainWindowHandle} exe={resolved.ExePath ?? "null"} aumid={resolved.Spec.Aumid ?? "null"}")}");
        if (resolved is null)
        {
            return Fail(batch, DeliveryFailureKind.NotInstalled,
                $"这台电脑上没有找到 {targetName}。", targetName, plan);
        }

        resolved = await _env.ActivateAsync(resolved, ct).ConfigureAwait(false);
        _env.Log?.Invoke($"[deliver {batch.Id:N}] after activate: pid={resolved.ProcessId?.ToString() ?? "null"} hwnd={resolved.MainWindowHandle} fg={_env.ForegroundWindow()}");
        if (!await WaitUntilForeground(resolved, ct).ConfigureAwait(false))
        {
            return Fail(batch, DeliveryFailureKind.DidNotBecomeActive,
                $"{targetName} 没有切到前台，已经放弃自动粘贴。", targetName, plan);
        }

        await _env.DelayAsync(_env.ForegroundSettle ?? ForegroundSettle, ct).ConfigureAwait(false);

        // A target that needs a mode switch gets its hotkey first — Doubao's
        // Ctrl+J opens a new 工作任务 whose composer reads local files; the
        // 对话 composer cannot. Best effort: if the chord is dropped, the paste
        // still lands wherever the window was left.
        if (spec.PrePasteHotkey is { } hotkey)
        {
            var sent = _env.SendCtrlKey?.Invoke(hotkey);
            _env.Log?.Invoke($"[deliver {batch.Id:N}] pre-paste hotkey vk=0x{hotkey:X2} sent={sent?.ToString() ?? "skipped"}");
            if (sent == true)
                await _env.DelayAsync(PrePasteHotkeySettle, ct).ConfigureAwait(false);
        }

        // Point the keyboard at the composer before the first paste. Without
        // this a Ctrl+V into an unfocused chat window lands nowhere — the
        // keystroke is accepted but nothing arrives (observed live 2026-09-25).
        try
        {
            var focused = _env.FocusTextInput?.Invoke(_env.ForegroundWindow());
            _env.Log?.Invoke($"[deliver {batch.Id:N}] focus composer: {focused?.ToString() ?? "skipped"}");
        }
        catch (Exception error)
        {
            _env.Log?.Invoke($"聚焦输入框失败，继续粘贴：{error.Message}");
        }

        for (var index = 0; index < plan.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            if (index > 0)
                await _env.DelayAsync(_env.BetweenPastes ?? BetweenPastes, ct).ConfigureAwait(false);
            if (!WritePayload(plan[index]))
            {
                return Fail(batch, DeliveryFailureKind.ClipboardWriteFailed,
                    "系统没有接受这次剪贴板写入。", targetName, plan);
            }
            if (!_env.SendCtrlV())
            {
                return Fail(batch, DeliveryFailureKind.KeystrokeRejected,
                    "系统没有接受这次按键事件。", targetName, plan);
            }
            _env.Log?.Invoke($"[deliver {batch.Id:N}] pasted {index + 1}/{plan.Count} fg={_env.ForegroundWindow()}");
        }

        var delivered = new BatchOutcome(BatchOutcomeKind.Delivered, null, _env.Now());
        Record(delivered, batch.Id, targetName);
        return new DeliveryResult(delivered, targetName, plan, null);
    }

    /// <summary>
    /// Polls <c>GetForegroundWindow</c> until the target owns it — the wait is
    /// not decoration: launch returns long before the app has a window or a
    /// focused text field, and pressing Ctrl+V at that moment types into nothing.
    /// </summary>
    private async Task<bool> WaitUntilForeground(ResolvedTarget target, CancellationToken ct)
    {
        var ownPid = _env.ThisProcessId();
        var timeout = _env.ForegroundTimeout ?? ForegroundTimeout;
        var poll = _env.ForegroundPoll ?? ForegroundPoll;
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        nint lastHwnd = -1;
        uint lastPid = 0;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var hwnd = _env.ForegroundWindow();
            if (hwnd != 0 && target.OwnsForeground(hwnd, _env.WindowProcessId(hwnd), ownPid))
                return true;
            if (hwnd != lastHwnd)
            {
                lastHwnd = hwnd;
                lastPid = hwnd == 0 ? 0 : _env.WindowProcessId(hwnd);
                _env.Log?.Invoke($"[wait-foreground] hwnd={hwnd} pid={lastPid} (want hwnd={target.MainWindowHandle} pid={target.ProcessId?.ToString() ?? "any≠self"})");
            }
            await _env.DelayAsync(poll, ct).ConfigureAwait(false);
        }
        _env.Log?.Invoke($"[wait-foreground] timed out; last hwnd={lastHwnd} pid={lastPid}");
        return false;
    }

    /// <summary>
    /// Every failure lands here, and every failure ends the same way: the files
    /// are on the clipboard and the history says what went wrong.
    /// </summary>
    private DeliveryResult Fail(
        ReadyBatch batch,
        DeliveryFailureKind kind,
        string message,
        string? targetName,
        IReadOnlyList<PastePayload> plan)
    {
        // Whatever the forward was about to paste stays pasteable — including a
        // mid-plan failure that left a half-rewritten clipboard behind.
        WriteManualPayload(plan, batch.Items.Select(i => i.FullPath).ToArray());
        var outcome = new BatchOutcome(BatchOutcomeKind.Failed, message, _env.Now());
        Record(outcome, batch.Id, targetName);
        return new DeliveryResult(outcome, targetName, plan, kind);
    }

    /// <summary>
    /// What a manual Ctrl+V should produce: the files, or the one line a
    /// terminal would have received — never the bare prompt.
    /// </summary>
    private bool WriteManualPayload(IReadOnlyList<PastePayload> plan, IReadOnlyList<string> paths) =>
        WritePayload(PastePlan.ManualPayload(plan) ?? new PastePayload.Files(paths));

    private bool WritePayload(PastePayload payload) => payload switch
    {
        PastePayload.Files files => _env.WriteFileDropList(files.Paths),
        PastePayload.Text text => _env.WriteClipboardText(text.Value),
        _ => false,
    };

    /// <summary>Records into state.json when an inbox is attached; never lets recording fail a delivery.</summary>
    private void Record(BatchOutcome outcome, Guid batchId, string? targetName)
    {
        if (_inbox is null)
            return;
        try
        {
            _inbox.RecordOutcome(outcome, batchId, targetName);
        }
        catch (Exception error)
        {
            _env.Log?.Invoke($"记录投递结果失败：{error.Message}");
        }
    }
}
