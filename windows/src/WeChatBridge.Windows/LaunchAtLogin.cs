using Microsoft.Win32;
using System.Diagnostics;

namespace WeChatBridge.Windows;

/// <summary>
/// 开机自启 via HKCU\…\CurrentVersion\Run — the mechanism Windows provides for
/// exactly this. The registered command carries <c>--background</c> so sign-in
/// never raises a window: the process exists to consume share intents, not to
/// be looked at.
/// </summary>
internal static class LaunchAtLogin
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WeChatBridge.Windows";

    private static string Command =>
        $"\"{Process.GetCurrentProcess().MainModule?.FileName}\" --background";

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string;
    }

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
                return;
            if (enabled)
            {
                if (key.GetValue(ValueName) is not string current || current != Command)
                    key.SetValue(ValueName, Command);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // A policy-denied Run key leaves the feature off — the share still
            // works, just slower. The settings toggle reads the registry next
            // open, so a failed write self-corrects visually too.
        }
    }
}
