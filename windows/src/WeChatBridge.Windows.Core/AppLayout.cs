using System.Text;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// Where 微信流 keeps user data, and how this copy tells itself apart from
/// another copy on the same machine.
/// <para>
/// An installed build lives under <c>%LOCALAPPDATA%\WeChatBridge</c>. The
/// portable build writes everything beside the executable, so the removable
/// drive is the entire installation and unplugging it leaves nothing behind.
/// Portable mode is decided by a <c>portable.flag</c> marker file next to the
/// executable, which means one binary serves both modes.
/// </para>
/// <para>
/// Resolution happens on first touch, which is earlier than it looks:
/// <c>App</c>'s constructor reads <c>AppSettingsStore</c> before
/// <c>OnStartup</c> runs, so this class may only depend on
/// <see cref="AppContext.BaseDirectory"/> and the environment. Anything
/// established during startup is already too late.
/// </para>
/// </summary>
public sealed class AppLayout
{
    /// <summary>The sentinel whose presence next to the executable selects portable mode.</summary>
    public const string PortableMarkerFileName = "portable.flag";

    /// <summary>
    /// The helper lives in <c>&lt;portable&gt;\share-target</c>. Only this
    /// directory name may move resolution up a level — see <see cref="ResolvePortableRoot"/>.
    /// </summary>
    public const string ShareTargetDirectoryName = "share-target";

    private AppLayout(string? portableRoot, string dataRoot, string coordinationSuffix)
    {
        PortableRoot = portableRoot;
        DataRoot = dataRoot;
        CoordinationSuffix = coordinationSuffix;
    }

    public static AppLayout Current { get; } = Resolve(
        AppContext.BaseDirectory,
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WeChatBridge"));

    /// <summary>The portable installation directory, or <c>null</c> for an installed build.</summary>
    public string? PortableRoot { get; }

    public bool IsPortable => PortableRoot is not null;

    /// <summary>Parent of every derived root below.</summary>
    public string DataRoot { get; }

    public string Inbox => Path.Combine(DataRoot, "Inbox");

    public string Config => Path.Combine(DataRoot, "Config");

    public string Skills => Path.Combine(DataRoot, "Skills");

    public string SkillBackups => Path.Combine(DataRoot, "SkillBackups");

    /// <summary>Registration and install diagnostics live here in portable mode only.</summary>
    public string? InstallLogs => PortableRoot is null ? null : Path.Combine(DataRoot, "Logs", "Install");

    /// <summary>The Obsidian vault shipped on the removable drive, or <c>null</c> when installed.</summary>
    public string? Vault => PortableRoot is null ? null : Path.Combine(PortableRoot, "Vault");

    /// <summary>
    /// Appended to every mutex, named pipe and event name, so a portable copy
    /// and an installed copy on the same machine do not silence each other.
    /// Empty for installed builds, which keeps the names they have always had.
    /// </summary>
    /// <remarks>
    /// The suffix is derived from <see cref="PortableRoot"/> rather than a
    /// constant so two different USB drives get two different sets of names.
    /// </remarks>
    public string CoordinationSuffix { get; }

    /// <summary>
    /// The testable seam. Everything above is derived from this call, so the
    /// portable rules can be exercised against a temp directory without ever
    /// touching <see cref="Current"/>.
    /// </summary>
    /// <param name="appDirectory">
    /// Directory holding the running executable. For the share helper this is
    /// the <c>share-target</c> subdirectory, not the portable root.
    /// </param>
    /// <param name="installedDataRoot">Data root for a non-portable build.</param>
    public static AppLayout Resolve(string appDirectory, string installedDataRoot)
    {
        var root = ResolvePortableRoot(appDirectory);
        return root is null
            ? new AppLayout(null, installedDataRoot, "")
            : new AppLayout(root, Path.Combine(root, "Data"), CoordinationSuffixFor(root));
    }

    /// <summary>
    /// Finds the portable root, or <c>null</c> for an installed build.
    /// </summary>
    /// <remarks>
    /// The parent walk is deliberately gated on the directory being named
    /// <see cref="ShareTargetDirectoryName"/>. The unconditional self-then-parent
    /// probe used elsewhere (see <c>AppLogos.PathFor</c>) is harmless there —
    /// a second image lookup — but here it decides where *every* byte of user
    /// data goes. Unconditionally, a stray <c>portable.flag</c> anywhere above
    /// the installed payload would silently turn an installed build portable and
    /// strand its Inbox and Config on a drive that is not there.
    /// </remarks>
    private static string? ResolvePortableRoot(string appDirectory)
    {
        var directory = new DirectoryInfo(appDirectory);
        if (HasMarker(directory))
            return directory.FullName;
        if (directory.Name.Equals(ShareTargetDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            var parent = directory.Parent;
            if (parent is not null && HasMarker(parent))
                return parent.FullName;
        }
        return null;
    }

    private static bool HasMarker(DirectoryInfo directory) =>
        File.Exists(Path.Combine(directory.FullName, PortableMarkerFileName));

    private static string CoordinationSuffixFor(string portableRoot)
    {
        // FNV-1a over the upper-cased full path. The path is the only thing that
        // distinguishes two sticks, and it has to survive a reboot unchanged.
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        var hash = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(portableRoot.ToUpperInvariant()))
            hash = unchecked((hash ^ b) * prime);
        return "-P" + hash.ToString("X8");
    }
}
