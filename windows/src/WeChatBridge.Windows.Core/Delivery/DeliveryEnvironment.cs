using WeChatBridge.Windows.Core.Interop;

namespace WeChatBridge.Windows.Core.Delivery;

/// <summary>
/// Every OS touch the delivery engine needs, as injectable delegates.
///
/// This is the seam the macOS port drew between <c>AutoPaste</c> (AppKit/AX,
/// untestable) and <c>PastePlan</c> (pure, tested). Here it is wider — the whole
/// engine sits behind it — because tests must never activate a real window or
/// own the real clipboard. <see cref="Create"/> wires the production delegates;
/// a test substitutes any subset.
/// </summary>
public sealed record DeliveryEnvironment
{
    /// <summary>Find the target app without launching it; null means not installed.</summary>
    public required Func<WindowsForwardTarget, ResolvedTarget?> ResolveTarget { get; init; }

    /// <summary>Launch and/or bring the target forward; returns the refreshed target.</summary>
    public required Func<ResolvedTarget, CancellationToken, Task<ResolvedTarget>> ActivateAsync { get; init; }

    /// <summary><c>GetForegroundWindow</c>; 0 when the call returns nothing.</summary>
    public required Func<nint> ForegroundWindow { get; init; }

    /// <summary>The owning process id of a window handle.</summary>
    public required Func<nint, uint> WindowProcessId { get; init; }

    /// <summary>CF_HDROP write on an STA thread; false when the write was refused.</summary>
    public required Func<IReadOnlyList<string>, bool> WriteFileDropList { get; init; }

    /// <summary>CF_UNICODETEXT write on an STA thread; false when refused.</summary>
    public required Func<string, bool> WriteClipboardText { get; init; }

    /// <summary>One physical Ctrl+V through <c>SendInput</c>; false when rejected.</summary>
    public required Func<bool> SendCtrlV { get; init; }

    /// <summary>
    /// One Ctrl+key chord for a target's mode-switch hotkey — sent before the
    /// composer is focused. Null skips <see cref="WindowsForwardTarget.PrePasteHotkey"/>.
    /// </summary>
    public Func<ushort, bool>? SendCtrlKey { get; init; }

    /// <summary>
    /// Points the keyboard at the target's composer before pasting — the port of
    /// macOS's <c>focusTextInput</c>. Best effort: a paste into the unfocused
    /// message list silently does nothing, so this is attempted but never
    /// required. Null skips the step.
    /// </summary>
    public Func<nint, bool>? FocusTextInput { get; init; }

    /// <summary>This process's id — the "not ours" marker for AUMID targets with no known pid.</summary>
    public Func<uint> ThisProcessId { get; init; } = () => (uint)Environment.ProcessId;

    /// <summary>Delay between waits; tests substitute an instant no-op.</summary>
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } =
        (span, ct) => Task.Delay(span, ct);

    /// <summary>The clock for outcome timestamps; tests freeze it.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Timing overrides for the waits (null = the engine's macOS-mirrored
    /// defaults). Tests shorten <see cref="ForegroundTimeout"/> so a fake that
    /// never comes to the front does not spin for the real four seconds.
    /// </summary>
    public TimeSpan? ForegroundTimeout { get; init; }
    public TimeSpan? ForegroundPoll { get; init; }
    public TimeSpan? ForegroundSettle { get; init; }
    public TimeSpan? BetweenPastes { get; init; }

    /// <summary>Best-effort diagnostics sink; never throws, never required.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>The production wiring — everything above backed by Win32.</summary>
    public static DeliveryEnvironment Create(Action<string>? log = null) => new()
    {
        Log = log,
        ResolveTarget = WindowsTargetResolver.Resolve,
        ActivateAsync = (target, ct) => WindowsTargetResolver.ActivateAsync(target, ct, log),
        ForegroundWindow = Win32.GetForegroundWindow,
        WindowProcessId = hwnd =>
        {
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            return pid;
        },
        WriteFileDropList = WindowsClipboard.WriteFileDropList,
        WriteClipboardText = WindowsClipboard.WriteText,
        SendCtrlV = Win32.SendCtrlV,
        SendCtrlKey = Win32.SendCtrlKey,
        FocusTextInput = Win32.FocusTextInput,
    };
}
