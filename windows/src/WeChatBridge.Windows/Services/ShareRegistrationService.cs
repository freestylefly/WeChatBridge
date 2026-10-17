using System.Diagnostics;
using System.IO;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Onboarding;

namespace WeChatBridge.Windows.Services;

/// <summary>
/// Runs <c>Register-ShareTarget.ps1</c> from the Entries page, so a portable
/// user can put 微信流 into the WeChat share menu on a machine that has never
/// seen the installer.
/// </summary>
internal static class ShareRegistrationService
{
    private const string ScriptName = "Register-ShareTarget.ps1";

    public static Task<ShareRegistrationState> Current => ShareRegistrationProbe.CheckAsync();

    /// <summary>
    /// True when an installed copy of 微信流 is on this machine. Registering from
    /// the portable build replaces the installed package's registration, because
    /// both carry the same package identity — deliberate, so the WeChat menu
    /// never shows two identically named rows — but the user has to be told.
    /// </summary>
    public static bool InstalledCopyPresent
    {
        get
        {
            if (!AppLayout.Current.IsPortable)
                return false;
            try
            {
                return File.Exists(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WeChatBridge", "App", "WeChatBridge.Windows.exe"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public static string? ScriptPath
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, ScriptName);
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>
    /// Registers the sparse package against this installation.
    /// </summary>
    /// <remarks>
    /// Deliberately launched <em>without</em> elevation. The script's own design
    /// is "elevate only the certificate import, register under the original
    /// user" — a per-user package installed from an elevated process lands in
    /// the wrong hive on a standard-user machine. <c>-NonInteractive</c> and the
    /// explicit <c>System32</c> path are equally deliberate: a bare
    /// <c>powershell</c> can resolve to the 32-bit copy under SysWOW64, and
    /// without <c>-NonInteractive</c> the script's UAC prompt blocks forever
    /// behind a window nobody is watching.
    /// </remarks>
    public static Task<RegistrationResult> RegisterAsync() => Task.Run(() => Register());

    private static RegistrationResult Register()
    {
        var script = ScriptPath;
        if (script is null)
            return RegistrationResult.Failed(
                L10n.Text("找不到注册脚本，便携版可能不完整。请重新解压压缩包。"));

        var installRoot = Path.GetDirectoryName(script)!;
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(powershell)
        {
            // UseShellExecute stays false so the script runs as this user; the
            // script elevates only the certificate import itself.
            UseShellExecute = false,
            WorkingDirectory = installRoot,
        };
        foreach (var argument in new[]
        {
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy", "Bypass",
            "-File", script,
            "-InstallRoot", installRoot,
            "-SkipProcessKill",
        })
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return RegistrationResult.Failed(L10n.Text("无法启动注册脚本。"));
            // No timeout by design: the UAC prompt is user-facing, and killing the
            // script mid-import would leave a half-written certificate store.
            process.WaitForExit();
            if (process.ExitCode != 0)
                return RegistrationResult.Failed(DescribeFailure(installRoot));
            ShareRegistrationMarker.Write();
            return RegistrationResult.Succeeded();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            return RegistrationResult.Failed($"{L10n.Text("注册失败。")} {e.Message}");
        }
    }

    /// <summary>
    /// Turns the newest install log into something the user can act on. The
    /// script's own stdout is not available here: no elevation means no
    /// redirection-free capture, and the log is what it always writes.
    /// </summary>
    private static string DescribeFailure(string installRoot)
    {
        var directory = AppLayout.Current.InstallLogs ?? Path.Combine(installRoot, "Logs", "Install");
        try
        {
            var newest = new DirectoryInfo(directory)
                .GetFiles("register-sharetarget-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (newest is null)
                return L10n.Text("注册失败。请重启微信后再试一次。");
            var tail = File.ReadAllLines(newest.FullName)
                .TakeLast(6)
                .Select(line => line.Length > 200 ? line[..200] + "…" : line);
            return L10n.Text("注册失败。") + "\n\n" + string.Join('\n', tail);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return L10n.Text("注册失败。请重启微信后再试一次。");
        }
    }
}

internal sealed record RegistrationResult(bool Success, string? Message)
{
    public static RegistrationResult Succeeded() => new(true, null);

    public static RegistrationResult Failed(string message) => new(false, message);
}
