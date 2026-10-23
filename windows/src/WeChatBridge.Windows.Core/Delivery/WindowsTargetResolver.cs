using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WeChatBridge.Windows.Core.Interop;

namespace WeChatBridge.Windows.Core.Delivery;

/// <summary>
/// Turns a <see cref="WindowsForwardTarget"/> into a <see cref="ResolvedTarget"/>:
/// a running process if one exists, otherwise an installed (or PATH-resolvable)
/// executable, otherwise an AUMID — and launches/foregrounds on demand.
///
/// The macOS original asked Launch Services for the bundle URL and called
/// <c>openApplication(activates: true)</c>. Windows has no equivalent registry of
/// identity → path, hence the candidate table: process-name match first (the app
/// may already be running), then install-path probing, then a bare exe name left
/// for the shell to resolve, then <c>shell:AppsFolder</c> for packaged apps.
/// </summary>
public static class WindowsTargetResolver
{
    /// <summary>
    /// How long a cold launch may take to show a window. Longer than the
    /// engine's foreground wait: launching is the expensive part, and the wait
    /// for frontmost only starts once the window exists.
    /// </summary>
    public static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public static bool IsInstalled(WindowsForwardTarget spec)
    {
        // An explicit custom path must exist; an unrelated running app with
        // the same executable name is not evidence that this target exists.
        if (spec.ExeCandidates.Count > 0 && spec.ExeCandidates.All(candidate =>
            Path.IsPathRooted(Environment.ExpandEnvironmentVariables(candidate))))
            return FindExecutable(spec) is not null;
        return Resolve(spec) is not null;
    }

    /// <summary>Best resolution without launching anything.</summary>
    public static ResolvedTarget? Resolve(WindowsForwardTarget spec)
    {
        var running = FindRunning(spec);
        if (running is not null)
            return running;
        var exe = FindExecutable(spec);
        if (exe is not null)
            return new ResolvedTarget(spec, null, 0, exe);
        if (spec.Aumid is not null && IsRegisteredApplication(spec.Aumid))
            return new ResolvedTarget(spec, null, 0, null);
        return null;
    }

    /// <summary>
    /// Brings the target forward and refreshes what we know about it: a cold
    /// launch returns with the process id and window handle filled in, so the
    /// engine's foreground wait has something exact to compare against.
    /// </summary>
    public static async Task<ResolvedTarget> ActivateAsync(
        ResolvedTarget target,
        CancellationToken ct,
        Action<string>? log = null)
    {
        if (target.ProcessId is null && target.MainWindowHandle == 0)
            Launch(target);

        if (target.MainWindowHandle == 0)
            target = await WaitForWindow(target, ct).ConfigureAwait(false) ?? target;

        if (target.MainWindowHandle != 0)
        {
            Win32.BringToForeground(target.MainWindowHandle, out var diag);
            log?.Invoke($"bring-to-front hwnd={target.MainWindowHandle}: {diag}");
        }
        else if (target.ProcessId is { } pid)
        {
            log?.Invoke($"bring-to-front pid={pid}: {BringMainWindowForward(pid)}");
        }
        return target;
    }

    /// <summary>
    /// A running process beats a fresh install lookup: the user expects the app
    /// they already have open, and activation is cheaper than a cold start.
    /// Prefers a process that actually owns a window.
    /// </summary>
    private static ResolvedTarget? FindRunning(WindowsForwardTarget spec)
    {
        ResolvedTarget? windowless = null;
        foreach (var name in spec.ProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name));
            }
            catch
            {
                continue;
            }
            foreach (var process in processes)
            {
                using (process)
                {
                    string? path = null;
                    try { path = process.MainModule?.FileName; }
                    catch
                    {
                        // Elevated or protected process — the window handle still works.
                    }
                    var hwnd = MainWindowOf(process);
                    if (hwnd != 0)
                        return new ResolvedTarget(spec, process.Id, hwnd, path);
                    windowless ??= new ResolvedTarget(spec, process.Id, 0, path);
                }
            }
        }
        return windowless;
    }

    private static string? FindExecutable(WindowsForwardTarget spec)
    {
        foreach (var candidate in spec.ExeCandidates)
        {
            var expanded = Environment.ExpandEnvironmentVariables(candidate);
            if (Path.IsPathRooted(expanded))
            {
                if (File.Exists(expanded))
                    return Path.GetFullPath(expanded);
                continue;
            }
            // Verify both PATH and App Paths before advertising availability.
            var found = SearchPath(expanded) ?? SearchAppPaths(expanded);
            if (found is not null)
                return found;
        }
        return null;
    }

    private static string? SearchAppPaths(string fileName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + fileName);
                if (key?.GetValue(null) is string value)
                {
                    var path = Environment.ExpandEnvironmentVariables(value).Trim('"');
                    if (File.Exists(path)) return path;
                }
            }
            catch { /* Inaccessible registration is not installation evidence. */ }
        }
        return null;
    }

    /// <summary>Both MSIX and Win32 AUMIDs must actually appear in AppsFolder.</summary>
    public static bool IsRegisteredApplication(string aumid)
    {
        if (!OperatingSystem.IsWindows()) return false;
        object? shell = null, folder = null, items = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return false;
            shell = Activator.CreateInstance(type);
            folder = ((dynamic)shell!).NameSpace("shell:AppsFolder");
            if (folder is null) return false;
            items = ((dynamic)folder).Items();
            for (var i = 0; i < (int)((dynamic)items).Count; i++)
            {
                object? item = null;
                try
                {
                    item = ((dynamic)items).Item(i);
                    string? id = ((dynamic)item!).ExtendedProperty("System.AppUserModel.ID") as string;
                    if (string.Equals(id, aumid, StringComparison.OrdinalIgnoreCase)) return true;
                }
                finally { if (item is not null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item); }
            }
        }
        catch { /* Do not enable an entry whose registration cannot be verified. */ }
        finally
        {
            foreach (var value in new[] { items, folder, shell })
                if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
        return false;
    }

    private static string? SearchPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // A malformed PATH entry is the user's problem, not ours.
            }
        }
        return null;
    }

    private static void Launch(ResolvedTarget target)
    {
        try
        {
            if (target.ExePath is { } exe)
            {
                // UseShellExecute = false returns a Process with the real id,
                // which is what the foreground wait compares against.
                Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = false });
            }
            else if (target.Spec.Aumid is { } aumid)
            {
                // The documented way to launch a packaged app by AUMID.
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"shell:AppsFolder\\{aumid}",
                    UseShellExecute = true,
                });
            }
        }
        catch
        {
            // A launch that never started is reported by the caller's foreground
            // timeout — the files are already on the clipboard.
        }
    }

    /// <summary>
    /// Polls for the launched (or windowless running) process to show a window,
    /// refreshing the resolved target once it does.
    /// </summary>
    private static async Task<ResolvedTarget?> WaitForWindow(ResolvedTarget target, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + (long)LaunchTimeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (target.ProcessId is { } pid)
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    var hwnd = process is { HasExited: false } ? MainWindowOf(process) : 0;
                    if (hwnd != 0)
                        return target with { MainWindowHandle = hwnd };
                }
                catch
                {
                    // Exited between resolve and poll — the foreground wait reports it.
                    return null;
                }
            }
            // A launcher or App-execution-alias stub (C:\Windows\notepad.exe,
            // shell:AppsFolder) exits or hands the window to a different real
            // pid — Windows Notepad resolved to the stub at 38244 while the
            // window belonged to 43768 (2026-09-25 self-test). Keep scanning
            // the spec's process names and only accept a match that owns a
            // window; a windowless process here wins nothing.
            if (FindRunning(target.Spec) is { MainWindowHandle: not 0 } windowed)
                return windowed;
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>Foregrounds the main window of a known process; returns diagnostics for the delivery log.</summary>
    private static string BringMainWindowForward(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var hwnd = MainWindowOf(process);
            if (hwnd == 0)
                return "no window";
            Win32.BringToForeground(hwnd, out var diag);
            return $"hwnd={hwnd} {diag}";
        }
        catch
        {
            // The process exited mid-forward; the foreground wait reports it.
            return "process exited";
        }
    }

    /// <summary>
    /// The delivery window for a process: the largest visible titled top-level
    /// window, falling back to <c>MainWindowHandle</c>. Electron apps own
    /// several windows per process and <c>MainWindowHandle</c> is not
    /// guaranteed to be the visible one — Doubao's 52×52 helper widget won it
    /// while the real chat window stayed background (2026-09-25).
    /// </summary>
    private static nint MainWindowOf(Process process)
    {
        try
        {
            var found = Win32.FindMainWindow((uint)process.Id);
            return found != 0 ? found : process.MainWindowHandle;
        }
        catch
        {
            return 0;
        }
    }
}
