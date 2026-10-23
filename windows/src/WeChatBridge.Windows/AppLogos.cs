using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows;

/// <summary>
/// The real per-agent marks, shipped loose next to the exe so WPF can bind a
/// plain file path — the Windows counterpart of macOS's bundled
/// <c>Resources/AppLogos/*.png</c> + AppLogos.name(for:).
///
/// Files live in <c>Assets/AppLogos</c> under <see cref="AppContext.BaseDirectory"/>;
/// a missing logo resolves to null and callers fall back to a letter badge.
/// </summary>
public static class AppLogos
{
    private static readonly IReadOnlyDictionary<AgentId, string> ByAgent =
        new Dictionary<AgentId, string>
        {
            [AgentId.ChatGptCodex] = "04-chatgpt.png",
            [AgentId.Claude] = "03-claude.png",
            [AgentId.Doubao] = "01-doubao.png",
            [AgentId.QwenWork] = "02-qwen.png",
            [AgentId.WorkBuddy] = "06-workbuddy.png",
            [AgentId.WeSight] = "07-wesight.png",
        };

    /// <summary>Full path to the agent's logo, or null when the file is absent.</summary>
    public static string? PathFor(AgentId agent) =>
        ByAgent.TryGetValue(agent, out var file) ? Existing(file) : null;

    /// <summary>
    /// Logo for a share action / forward destination: the action's own agent
    /// plus the two pseudo-agents macOS also badges (Obsidian, clipboard has
    /// none).
    /// </summary>
    public static string? PathFor(ShareAction action) =>
        action == ShareAction.Obsidian
            ? Existing("05-obsidian.png")
            : AgentIds.Matching(action) is { } agent ? PathFor(agent) : null;

    /// <summary>Loose lookup by target display name for custom targets.</summary>
    public static string? PathFor(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return null;
        foreach (var (agent, file) in ByAgent)
            if (displayName.Contains(agent.DisplayName(), StringComparison.OrdinalIgnoreCase))
                return Existing(file);
        if (displayName.Contains("Obsidian", StringComparison.OrdinalIgnoreCase))
            return Existing("05-obsidian.png");
        return null;
    }

    /// <summary>
    /// The mark a picker row shows for a custom target: a bundled agent logo
    /// when the name matches one, otherwise the app's own icon pulled out of
    /// its exe (Windows bundle ids carry the exe path). The return type is
    /// <see cref="object"/> because WPF binds either a PNG path or an
    /// <see cref="ImageSource"/> — <c>Image.Source</c> converts both.
    /// </summary>
    public static object? IconFor(ForwardTarget target)
    {
        if (PathFor(target.DisplayName) is { } bundled)
            return bundled;
        if (ShellIcon(target.BundleIdentifier) is { } icon)
            return icon;
        return null;
    }

    /// <summary>
    /// The large icon the shell itself would show for a file. Only exe paths
    /// are interrogated — the bundle id of a packaged target is an AUMID, not
    /// a file, and those fall back to the letter badge.
    /// </summary>
    private static ImageSource? ShellIcon(string bundleIdentifier)
    {
        if (!bundleIdentifier.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(bundleIdentifier))
            return null;
        var info = new SHFILEINFO();
        if (SHGetFileInfo(bundleIdentifier, 0, ref info,
                (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON) == IntPtr.Zero
            || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private static string? Existing(string file)
    {
        // The share-target helper publishes one level below the install root —
        // share-target\WeChatBridge.ShareTarget.exe versus Assets\AppLogos —
        // so the parent directory is probed after the exe's own. (DirectoryInfo
        // rather than GetParent: BaseDirectory's trailing separator makes
        // GetParent return the directory itself.)
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "AppLogos", file);
        if (File.Exists(path))
            return path;
        var parent = new DirectoryInfo(AppContext.BaseDirectory).Parent;
        if (parent is null)
            return null;
        path = Path.Combine(parent.FullName, "Assets", "AppLogos", file);
        return File.Exists(path) ? path : null;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi,
        uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
