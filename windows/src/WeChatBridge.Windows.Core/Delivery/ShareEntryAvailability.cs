using System.IO;

namespace WeChatBridge.Windows.Core.Delivery;

/// <summary>Checks destinations without launching them or touching the clipboard.</summary>
public static class ShareEntryAvailability
{
    /// <summary>
    /// Where Obsidian may be. The first three are the installer's defaults; the
    /// fourth is a bare PATH probe. The portable copy on the removable drive is
    /// not in this table because <c>%WECHATBRIDGE_PORTABLE%</c> is not what the
    /// app resolves from — <see cref="AppLayout"/> is, and it is a different
    /// root on every machine.
    /// </summary>
    private static readonly string[] ObsidianExecutables =
    [
        @"%LOCALAPPDATA%\Obsidian\Obsidian.exe",
        @"%LOCALAPPDATA%\Programs\Obsidian\Obsidian.exe",
        @"%ProgramFiles%\Obsidian\Obsidian.exe",
        "Obsidian.exe",
    ];

    public static bool CanEnable(ShareAction action, IReadOnlyList<ForwardTarget> customTargets,
        Func<WindowsForwardTarget, bool>? isInstalled = null)
    {
        isInstalled ??= WindowsTargetResolver.IsInstalled;
        if (action == ShareAction.Custom)
            return customTargets.Any(target => isInstalled(WindowsForwardTargets.ForCustom(target)));
        var spec = action == ShareAction.Obsidian
            ? new WindowsForwardTarget("Obsidian", ObsidianCandidates(), ObsidianExecutables)
            : WindowsForwardTargets.For(action);
        return spec is null || isInstalled(spec);
    }

    /// <summary>
    /// Adds the drive's own Obsidian when this is a portable build, so the entry
    /// is not reported as 「未安装」 on a stick that ships one.
    /// </summary>
    private static string[] ObsidianCandidates()
    {
        if (AppLayout.Current.PortableRoot is not { } root)
            return [];
        var executable = Path.Combine(root, "Obsidian", "Obsidian.exe");
        return File.Exists(executable) ? [executable] : [];
    }
}
