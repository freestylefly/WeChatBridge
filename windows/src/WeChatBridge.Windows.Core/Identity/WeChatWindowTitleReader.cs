using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WeChatBridge.Windows.Core;

public enum WeChatTitleReaderErrorKind
{
    /// <summary>No running WeChat process, or none with a usable window.</summary>
    NoWindow,
    /// <summary>A window was found but its title carried no chat name.</summary>
    EmptyTitle,
}

public sealed class WeChatTitleReaderException(WeChatTitleReaderErrorKind kind, string message)
    : Exception(message)
{
    public WeChatTitleReaderErrorKind Kind { get; } = kind;
}

/// <summary>
/// Windows port of <c>WeChatTitleReader</c>: reads the chat name from WeChat's
/// top-level window titles with <c>GetWindowText</c>, without capturing any
/// pixels. The privacy boundary is identical to the macOS Accessibility path:
/// window titles only, no screen capture, no injection.
///
/// WeChat 4.x (Weixin) titles its main window 「微信」 whatever chat is open, so
/// a title that is only the app name is not a chat name. It is dropped here, so
/// the caller falls through to the OCR read instead of filing every group as
/// 「微信」 (issue #41).
/// </summary>
public sealed class WeChatWindowTitleReader
{
    /// <summary>Weixin.exe is WeChat 4.x; WeChat.exe is the 3.x line.</summary>
    private static readonly string[] WeChatProcessNames = ["weixin", "wechat"];

    /// <summary>What WeChat calls its own windows; never a chat name.</summary>
    private static readonly HashSet<string> AppNames =
        new(StringComparer.OrdinalIgnoreCase) { "微信", "Weixin", "WeChat" };

    private readonly Func<IReadOnlyList<string>> _windowTitles;

    /// <summary>True for a title that names the app rather than a chat.</summary>
    public static bool IsAppName(string title) => AppNames.Contains(title.Trim());

    /// <param name="windowTitles">Injectable so tests parse titles without a
    /// running WeChat. Null reads the real windows via user32.</param>
    public WeChatWindowTitleReader(Func<IReadOnlyList<string>>? windowTitles = null) =>
        _windowTitles = windowTitles ?? ReadWeChatWindowTitles;

    /// <summary>
    /// The current chat as <see cref="GroupTitleParser.Title"/>, parsed from the
    /// window title lines so the trailing "（成员数）" is stripped exactly the
    /// way macOS strips its OCR lines.
    /// </summary>
    public GroupTitleParser.Title Read()
    {
        var titles = _windowTitles();
        if (titles.Count == 0)
            throw new WeChatTitleReaderException(
                WeChatTitleReaderErrorKind.NoWindow, "没有找到可见的微信聊天窗口。");
        return GroupTitleParser.Parse(titles.Where(title => !IsAppName(title)))
            ?? throw new WeChatTitleReaderException(
                WeChatTitleReaderErrorKind.EmptyTitle, "没有识别出微信群名。");
    }

    /// <summary>Best-effort read; a missing WeChat means "no group name", not a failure.</summary>
    public GroupTitleParser.Title? TryRead()
    {
        try
        {
            return Read();
        }
        catch (WeChatTitleReaderException)
        {
            return null;
        }
    }

    /// <summary>
    /// Visible top-level window titles belonging to a WeChat process, biggest
    /// window first. Mirrors the macOS fallback picking the largest layer-0
    /// window: the main window is the one whose title carries the chat name,
    /// and sub-windows (search, settings) sort behind it.
    /// </summary>
    public static IReadOnlyList<string> ReadWeChatWindowTitles()
    {
        var pids = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (WeChatProcessNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase))
                    pids.Add(process.Id);
            }
        }
        if (pids.Count == 0)
            return [];

        var found = new List<(string Title, long Area)>();
        NativeMethods.EnumWindows((handle, _) =>
        {
            if (!NativeMethods.IsWindowVisible(handle))
                return true;
            NativeMethods.GetWindowThreadProcessId(handle, out var pid);
            if (!pids.Contains(unchecked((int)pid)))
                return true;
            // Same sanity floor as macOS (320×240): tray-only helper windows are
            // smaller than any real chat window.
            if (!NativeMethods.GetWindowRect(handle, out var rect)
                || rect.Right - rect.Left < 320
                || rect.Bottom - rect.Top < 240)
                return true;
            var length = NativeMethods.GetWindowTextLength(handle);
            if (length <= 0)
                return true;
            var buffer = new StringBuilder(length + 1);
            NativeMethods.GetWindowText(handle, buffer, buffer.Capacity);
            var title = buffer.ToString().Trim();
            if (title.Length > 0)
            {
                found.Add((title,
                    (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top)));
            }
            return true;
        }, IntPtr.Zero);

        return found
            .OrderByDescending(entry => entry.Area)
            .Select(entry => entry.Title)
            .ToList();
    }

    private static class NativeMethods
    {
        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }
}
