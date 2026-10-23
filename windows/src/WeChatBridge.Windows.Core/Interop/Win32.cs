using System.Runtime.InteropServices;

namespace WeChatBridge.Windows.Core.Interop;

/// <summary>
/// 转发链路需要的全部 Win32 入口，刻意保持最小：前台窗口读写、窗口恢复、
/// 一次键盘输入，以及一套不依赖 WPF/WinForms 的剪贴板原语。
///
/// macOS 端对应 <c>AutoPaste.swift</c>：那边合成按键需要「辅助功能」权限，
/// 只有用户能授予，拒绝后降级为「文件已在剪贴板」。Windows 的
/// <c>SendInput</c>/<c>SetForegroundWindow</c> 不需要任何权限声明——这是两端
/// 唯一不对等的地方，所以 Windows 没有 <c>notTrusted</c> 分支，但
/// <c>SetForegroundWindow</c> 受系统前台锁限制，因此带上
/// <c>AttachThreadInput</c> 作为同等的兜底手段。
/// </summary>
internal static class Win32
{
    internal const int SW_SHOW = 5;
    internal const int SW_RESTORE = 9;

    internal const uint CF_UNICODETEXT = 13;
    internal const uint CF_HDROP = 15;

    internal const uint GMEM_MOVEABLE = 0x0002;

    internal const ushort VK_CONTROL = 0x11;
    internal const ushort VK_J = 0x4A;
    internal const ushort VK_V = 0x56;
    internal const ushort VK_F24 = 0x87;

    internal const uint INPUT_KEYBOARD = 1;
    internal const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint hWnd);

    /// <summary>Returns the owning thread id; the process id is delivered via <paramref name="processId"/>.</summary>
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    /// <summary>
    /// Borrows the foreground thread's input queue for one activation.
    /// <c>SetForegroundWindow</c> rejects calls from a process that is not the
    /// foreground process; sharing the input queue is the documented way to be
    /// allowed through, and is exactly what the shell does for its own activations.
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyClipboard();

    /// <summary>On success the clipboard owns <paramref name="hMem"/> — do not free it afterwards.</summary>
    [DllImport("user32.dll")]
    internal static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseClipboard();

    [DllImport("kernel32.dll")]
    internal static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll")]
    internal static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll")]
    internal static extern nint GlobalFree(nint hMem);

    internal delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    internal static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(nint hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetFocus(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// Best-effort port of macOS's <c>focusTextInput</c>: point the keyboard at
    /// the control most likely to be the composer so the pasted Ctrl+V lands in
    /// the input box rather than the message list. Native edit classes win;
    /// for Electron/Chromium targets the render widget is focused — Chromium
    /// restores DOM focus to the last focused element, typically the composer.
    /// </summary>
    internal static bool FocusTextInput(nint hwnd)
    {
        if (hwnd == 0)
            return false;
        nint edit = 0, chromium = 0;
        long chromiumArea = -1;
        EnumChildWindows(hwnd, (child, _) =>
        {
            var name = new System.Text.StringBuilder(64);
            GetClassName(child, name, name.Capacity);
            var cls = name.ToString();
            var isEdit = cls.StartsWith("Edit", StringComparison.Ordinal)
                || cls.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)
                || cls.StartsWith("RICHEDIT", StringComparison.OrdinalIgnoreCase);
            if (edit == 0 && isEdit)
                edit = child;
            else if (cls is "Chrome_RenderWidgetHostHWND" or "MozillaWindowClass")
            {
                if (GetWindowRect(child, out var rect))
                {
                    var area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                    if (area > chromiumArea)
                    {
                        chromiumArea = area;
                        chromium = child;
                    }
                }
                else if (chromium == 0)
                    chromium = child;
            }
            return true;
        }, 0);
        var target = edit != 0 ? edit : chromium;
        return target != 0 && SetFocus(target);
    }

    /// <summary>
    /// The window a delivery should aim at for <paramref name="processId"/>: the
    /// largest *visible* top-level window with a title. Process.MainWindowHandle
    /// is whatever the process touched first — Electron apps routinely own a
    /// 50px helper widget that wins it (Doubao's real 1200×800 window lost to a
    /// 52×52 widget on 2026-09-25), and pasting into that goes nowhere.
    /// </summary>
    internal static nint FindMainWindow(uint processId)
    {
        nint best = 0;
        long bestArea = 0;
        var bestTitled = false;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != processId || !IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var rect))
                return true;
            var titled = GetWindowTextLength(hwnd) > 0;
            var area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
            // A titled window beats an untitled one outright; among equals, larger wins.
            if ((titled && !bestTitled) || (titled == bestTitled && area > bestArea))
            {
                best = hwnd;
                bestArea = area;
                bestTitled = titled;
            }
            return true;
        }, 0);
        return best;
    }

    /// <summary>
    /// One Ctrl+V. The macOS original posted ⌘V through the HID event tap so it
    /// arrived like a physical press; <c>SendInput</c> is the same promise on
    /// Windows — it lands in the real input stream, not a message queue the
    /// target might not pump.
    /// </summary>
    internal static bool SendCtrlV() => SendCtrlKey(VK_V);

    /// <summary>One Ctrl+key chord — e.g. Doubao's Ctrl+J for a new 工作任务.</summary>
    internal static bool SendCtrlKey(ushort virtualKey)
    {
        return SendInputs(
            Key(VK_CONTROL, keyUp: false),
            Key(virtualKey, keyUp: false),
            Key(virtualKey, keyUp: true),
            Key(VK_CONTROL, keyUp: true));
    }

    /// <summary>
    /// Restores the window if it is minimized, then asks for the foreground.
    /// Mirrors <c>restoreWindows</c> in AutoPaste.swift but far simpler: Windows
    /// only ever raises the window we are handed, never enumerates and unminimizes
    /// a whole app's window list — the same 「只动用户正看着的那一个窗口」 rule.
    /// </summary>
    internal static bool BringToForeground(nint hwnd) =>
        BringToForeground(hwnd, out _);

    internal static bool BringToForeground(nint hwnd, out string diagnostic)
    {
        if (hwnd == 0)
        {
            diagnostic = "hwnd=0";
            return false;
        }
        ShowWindow(hwnd, IsIconic(hwnd) ? SW_RESTORE : SW_SHOW);
        var foreground = GetForegroundWindow();
        var foregroundThread = GetWindowThreadProcessId(foreground, out var foregroundPid);
        var current = GetCurrentThreadId();
        var attached = foregroundThread != 0
            && foregroundThread != current
            && AttachThreadInput(current, foregroundThread, true);
        try
        {
            var result = SetForegroundWindow(hwnd);
            var unlocked = false;
            if (!result)
            {
                // SetForegroundWindow is denied while the system foreground
                // lock is armed — observed live with attach=True set=False
                // against a WorkBuddy foreground (2026-09-25). One synthetic
                // F24 press arms the "caller generated input" permission; F24
                // is unbound everywhere so nothing ever reacts to it.
                SendInputs(Key(VK_F24, keyUp: false), Key(VK_F24, keyUp: true));
                unlocked = true;
                result = SetForegroundWindow(hwnd);
            }
            diagnostic = $"set={result} attach={attached} unlock={unlocked} fgHwnd={foreground} fgPid={foregroundPid} fgThread={foregroundThread}";
            return result;
        }
        finally
        {
            if (attached)
                AttachThreadInput(current, foregroundThread, false);
        }
    }

    private static bool SendInputs(params INPUT[] inputs) =>
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;

    private static INPUT Key(ushort virtualKey, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wVk = virtualKey,
                wScan = 0,
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = 0,
            },
        },
    };

    // The union must carry MOUSEINPUT even though only keyboard input is sent:
    // SendInput sizes INPUT by its largest member (32 bytes on x64), and a
    // struct declared with only KEYBDINPUT would be handed a wrong cbSize.
    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
