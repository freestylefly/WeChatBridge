using Microsoft.Win32;
using System.IO;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Services;

/// <summary>
/// Points the <c>obsidian://</c> protocol at the copy of Obsidian on the drive.
/// <para>
/// The handler is registered by Obsidian's <em>installer</em>, and a portable
/// build has none — so <see cref="MainViewModel.OpenInObsidian"/>'s URI would
/// go nowhere. Registering it ourselves is three HKCU values pointing at the
/// same executable the installer would have used, which is why
/// <c>Obsidian.exe "obsidian://open?…"</c> is the one invocation that always
/// works: the shell passes the URI straight through as argv[1]. (Obsidian does
/// not accept <c>Obsidian.exe "&lt;vault&gt;" "&lt;note&gt;"</c> — positional
/// arguments are a long-standing feature request, never implemented — so there
/// is no alternative to the URI.)
/// </para>
/// <para>
/// Only writes when nothing owns the protocol, so an installed Obsidian that
/// already registered itself is never redirected.
/// </para>
/// </summary>
internal static class ObsidianProtocol
{
    private const string ProtocolKeyPath = @"Software\Classes\obsidian";
    private const string CommandKeyPath = @"Software\Classes\obsidian\shell\open\command";

    /// <summary>The Obsidian shipped on the portable drive, or null.</summary>
    public static string? PortableExecutable
    {
        get
        {
            var root = AppLayout.Current.PortableRoot;
            if (root is null)
                return null;
            var executable = Path.Combine(root, "Obsidian", "Obsidian.exe");
            return File.Exists(executable) ? executable : null;
        }
    }

    /// <summary>True when some Obsidian already owns the protocol.</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ProtocolKeyPath);
            return key?.GetValue(null) is string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Registers the protocol when it is free. Returns true when the URI will
    /// resolve afterwards.
    /// </summary>
    public static bool Ensure()
    {
        var executable = PortableExecutable;
        if (executable is null || IsRegistered())
            return IsRegistered();
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ProtocolKeyPath, writable: true);
            key?.SetValue(null, "URL:obsidian");
            key?.SetValue("URL Protocol", "");
            using var command = Registry.CurrentUser.CreateSubKey(CommandKeyPath, writable: true);
            command?.SetValue(null, $"\"{executable}\" \"%1\"");
            return true;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException
            or IOException)
        {
            return false;
        }
    }

    /// <summary>Removes the registration, but only if it is ours.</summary>
    public static void RemoveIfOurs()
    {
        var executable = PortableExecutable;
        if (executable is null)
            return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(CommandKeyPath);
            if (key?.GetValue(null) is not string command
                || !command.Contains(executable, StringComparison.OrdinalIgnoreCase))
                return;
            Registry.CurrentUser.DeleteSubKeyTree(ProtocolKeyPath);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
        }
    }
}
