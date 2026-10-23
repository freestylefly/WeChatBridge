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
/// Windows port of <c>WeChatTitleReader</c>. On macOS the group name comes from
/// Accessibility labels or a Screen-Recording screenshot of the title bar;
/// Windows needs neither — the WeChat main window's own title bar text already
/// contains the current chat name, and <c>GetWindowText</c> reads it without
/// capturing any pixels. The privacy boundary is identical to the macOS
/// Accessibility path: window titles only, no screen capture, no injection.
/// </summary>
public sealed class WeChatWindowTitleReader
{
    /// <summary>Weixin.exe is WeChat 4.x; WeChat.exe is the 3.x line.</summary>
    private static readonly string[] WeChatProcessNames = ["weixin", "wechat"];

    private readonly Func<IReadOnlyList<string>> _windowTitles;

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
        return GroupTitleParser.Parse(titles)
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
