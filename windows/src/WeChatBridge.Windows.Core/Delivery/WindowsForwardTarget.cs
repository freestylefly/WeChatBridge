namespace WeChatBridge.Windows.Core.Delivery;

/// <summary>
/// One paste destination on Windows — the counterpart of a macOS
/// <c>bundleIdentifier</c> looked up through Launch Services.
///
/// Windows has no bundle identity for unpackaged apps, so a target is a set of
/// clues resolved in order: running process names first (cheapest, and the
/// process may already be up), then installed executable paths, then an AUMID
/// for packaged apps. Every list is ordered best-guess first.
///
/// The exe names and install directories below are the macOS bundle-identifier
/// equivalents by name, not by measurement — several of these apps had not
/// shipped a stable Windows layout when this table was written. Entries are
/// injectable (<see cref="DeliveryEngine"/> takes overrides) so a correction
/// is a table edit, not a code change.
/// </summary>
public sealed record WindowsForwardTarget(
    /// <summary>Shown to the user in failures — mirrors <c>targetDisplayName</c>.</summary>
    string DisplayName,
    /// <summary>Process names without extension, e.g. <c>"Codex"</c>.</summary>
    IReadOnlyList<string> ProcessNames,
    /// <summary>
    /// Exe candidates: rooted paths (environment variables expanded), or bare
    /// file names resolved through PATH and App Paths. A bare name means
    /// "the app may be launchable even though we do not know where it lives".
    /// </summary>
    IReadOnlyList<string> ExeCandidates,
    /// <summary>
    /// Packaged-app id (<c>PackageFamilyName!AppId</c>) for MSIX targets that
    /// have no stable exe path; launched through <c>shell:AppsFolder</c>.
    /// </summary>
    string? Aumid = null,
    /// <summary>
    /// Ports the Doubao special case from ActionRunner.swift: the target can
    /// read local paths itself, so for ZIP archives the prompt and the path go
    /// as text instead of a file drop. Pending real-device verification on
    /// Windows — kept as a spec flag so it can be flipped per target.
    /// </summary>
    bool ReadsLocalArchives = false,
    /// <summary>
    /// A virtual-key code sent as Ctrl+key once the target owns the foreground,
    /// before the composer is focused — for targets whose default input mode
    /// cannot take what we paste. Doubao gets Ctrl+J (新工作任务): the 工作
    /// composer reads local files, the 对话 one cannot.
    /// </summary>
    ushort? PrePasteHotkey = null);

/// <summary>
/// A target after resolution: what the OS gave back for a
/// <see cref="WindowsForwardTarget"/>. Any field may be missing — a launched
/// app that has not opened a window yet has a process id but no handle, and a
/// packaged app launched by AUMID may expose neither.
/// </summary>
public sealed record ResolvedTarget(
    WindowsForwardTarget Spec,
    int? ProcessId,
    nint MainWindowHandle,
    string? ExePath)
{
    /// <summary>
    /// Whether <paramref name="hwnd"/>/<paramref name="processId"/> — the pair
    /// <c>GetForegroundWindow</c>/<c>GetWindowThreadProcessId</c> returned —
    /// belongs to this target. A handle match wins because a packaged app's
    /// process id is not always knowable; when nothing is known (AUMID launch),
    /// any foreground window that is not ours is accepted — best effort, since
    /// the user just watched the target open.
    /// </summary>
    public bool OwnsForeground(nint hwnd, uint processId, uint ownProcessId)
    {
        if (MainWindowHandle != 0 && hwnd == MainWindowHandle)
            return true;
        return ProcessId is { } pid ? processId == (uint)pid : processId != ownProcessId;
    }
}

/// <summary>
/// The fixed ShareAction → Windows target table, plus the custom-target mapping.
/// The table is data, not code: <see cref="DeliveryEngine"/> accepts an override
/// dictionary so tests — and future corrections to the guessed exe names —
/// never touch the engine.
/// </summary>
public static class WindowsForwardTargets
{
    public static IReadOnlyDictionary<ShareAction, WindowsForwardTarget> Defaults { get; } =
        new Dictionary<ShareAction, WindowsForwardTarget>
        {
            // Verified on device 2026-09-25: the Windows Codex desktop app is the
            // MSIX package OpenAI.Codex (the ChatGPT desktop app). Its main-window
            // process is ChatGPT.exe inside the package; the AUMID's publisher
            // suffix 2p2nqsd0c76g0 is OpenAI's hash and stable across machines.
            // Codex.exe in the package is the CLI helper, not the chat window.
            [ShareAction.Codex] = new(
                "Codex",
                ["ChatGPT", "Codex"],
                ["ChatGPT.exe"],
                Aumid: "OpenAI.Codex_2p2nqsd0c76g0!App"),
            // Verified against Anthropic's deployment docs: Claude desktop on
            // Windows ships only as MSIX — package Claude, stable publisher hash
            // pzs8sxrjxfjjc, binary app\Claude.exe inside protected WindowsApps.
            // AUMID is the only reliable handle; no user-level exe path exists.
            [ShareAction.Claude] = new(
                "Claude",
                ["Claude"],
                ["Claude.exe"],
                Aumid: "Claude_pzs8sxrjxfjjc!Claude"),
            // Verified on device 2026-09-25: classic win32 install under
            // %LOCALAPPDATA%\Doubao\Application; the shell-facing AUMID is the
            // app-registered "Doubao.ChatApp" (not PFN!AppId — win32 apps can
            // declare their own), which also launches through shell:AppsFolder.
            [ShareAction.Doubao] = new(
                "豆包",
                ["Doubao"],
                [
                    @"%LOCALAPPDATA%\Doubao\Application\Doubao.exe",
                    "Doubao.exe",
                ],
                Aumid: "Doubao.ChatApp",
                ReadsLocalArchives: true,
                PrePasteHotkey: 0x4A /* Ctrl+J → 新工作任务 */),
            // Verified on device: the Windows build of 千问 installs as Qianwen
            // (exe Qianwen.exe), a plain win32 app with no AUMID. Its Windows
            // client cannot take a pasted ZIP (the macOS one could), so — like
            // Doubao — it gets the archive path as text to read itself.
            [ShareAction.Qwen] = new(
                "千问",
                ["Qianwen", "QwenWork"],
                [
                    @"%LOCALAPPDATA%\Programs\Qianwen\Qianwen.exe",
                    "Qianwen.exe",
                    "QwenWork.exe",
                ],
                ReadsLocalArchives: true),
            // Verified on device: machine-wide install under Program Files;
            // WorkBuddy.WorkBuddy is its registered shell AppID.
            [ShareAction.WorkBuddy] = new(
                "WorkBuddy",
                ["WorkBuddy"],
                [
                    @"C:\Program Files\WorkBuddy\WorkBuddy.exe",
                    @"%LOCALAPPDATA%\Programs\WorkBuddy\WorkBuddy.exe",
                    "WorkBuddy.exe",
                ],
                Aumid: "WorkBuddy.WorkBuddy"),
            [ShareAction.DeepSeekHarness] = new(
                "DeepSeek Harness",
                ["DeepSeek Harness", "DeepSeekHarness"],
                [@"%LOCALAPPDATA%\Programs\DeepSeek Harness\DeepSeek Harness.exe",
                 "DeepSeek Harness.exe", "DeepSeekHarness.exe"]),
            [ShareAction.WeSight] = new(
                "WeSight",
                ["WeSight"],
                [
                    @"%LOCALAPPDATA%\Programs\WeSight\WeSight.exe",
                    @"%LOCALAPPDATA%\WeSight\WeSight.exe",
                    "WeSight.exe",
                ]),
        };

    /// <summary>
    /// The spec behind a fixed share-menu entry. Obsidian and Clipboard return
    /// null deliberately: one is a vault write, not a paste, and the other is
    /// finished by the helper before the app ever runs.
    /// </summary>
    public static WindowsForwardTarget? For(ShareAction action) =>
        Defaults.TryGetValue(action, out var target) ? target : null;

    /// <summary>
    /// Rebuilds the custom target an intent carries — the two flat fields on
    /// <see cref="BatchIntent"/>, the way <c>BatchIntent.target</c> does on macOS.
    /// </summary>
    public static ForwardTarget? IntentTarget(BatchIntent? intent) =>
        intent?.TargetBundleIdentifier is { } id
            ? new ForwardTarget(id, intent.TargetDisplayName ?? id, intent.RequestedAt)
            : null;

    /// <summary>
    /// Turns a user's custom target into a spec. <c>BundleIdentifier</c> on
    /// Windows stores whatever identifies the app: an AUMID
    /// (<c>Family!App</c>), a full exe path, or a bare executable name.
    /// </summary>
    public static WindowsForwardTarget ForCustom(ForwardTarget target)
    {
        var id = target.BundleIdentifier;
        if (id.Contains('!'))
            return new WindowsForwardTarget(target.DisplayName, [], [], Aumid: id);

        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(id))
        {
            var stem = Path.GetFileNameWithoutExtension(id);
            return new WindowsForwardTarget(target.DisplayName, [stem], [id]);
        }

        return new WindowsForwardTarget(target.DisplayName, [id], [$"{id}.exe"], Aumid: id);
    }
}
