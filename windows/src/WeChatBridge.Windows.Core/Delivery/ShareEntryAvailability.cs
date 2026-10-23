namespace WeChatBridge.Windows.Core.Delivery;

/// <summary>Checks destinations without launching them or touching the clipboard.</summary>
public static class ShareEntryAvailability
{
    public static bool CanEnable(ShareAction action, IReadOnlyList<ForwardTarget> customTargets,
        Func<WindowsForwardTarget, bool>? isInstalled = null)
    {
        isInstalled ??= WindowsTargetResolver.IsInstalled;
        if (action == ShareAction.Custom)
            return customTargets.Any(target => isInstalled(WindowsForwardTargets.ForCustom(target)));
        var spec = action == ShareAction.Obsidian
            ? new WindowsForwardTarget("Obsidian", ["Obsidian"],
                [@"%LOCALAPPDATA%\Obsidian\Obsidian.exe", @"%LOCALAPPDATA%\Programs\Obsidian\Obsidian.exe",
                 @"%ProgramFiles%\Obsidian\Obsidian.exe", "Obsidian.exe"])
            : WindowsForwardTargets.For(action);
        return spec is null || isInstalled(spec);
    }
}
