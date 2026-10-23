using System.Runtime.InteropServices;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// Raises <see cref="SceneDigitPressed"/> for each Ctrl+Alt+Digit (1–9) press.
/// Behind an interface because the real implementation needs a thread with a
/// message loop — tests substitute a fake and drive
/// <see cref="SceneShortcutController.HandleSceneDigit"/> directly.
/// </summary>
public interface ISceneHotkeySource : IDisposable
{
    /// <summary>The digit row key that was pressed, 1–9.</summary>
    event EventHandler<int>? SceneDigitPressed;

    void Start();
}

/// <summary>
/// Fixed global shortcuts for the first nine enabled scenes.
/// Ported from <c>SceneShortcutController</c> (macOS ⌃⌥1–9 via NSEvent;
/// Windows Ctrl+Alt+Digit via RegisterHotKey).
///
/// The shortcut chooses the scene for the <em>next</em> share instead of
/// running the whole export itself, so the same gesture works whatever path
/// the files arrive through, and the choice expires after
/// <see cref="NextForwardSceneStore.SelectionWindow"/>.
/// </summary>
public sealed class SceneShortcutController : IDisposable
{
    private readonly Func<IReadOnlyList<WeChatScene>> _enabledScenes;
    private readonly NextForwardSceneStore _pending;
    private readonly Action<string>? _notify;
    private readonly ISceneHotkeySource? _hotkeys;

    /// <param name="enabledScenes">Read at press time, like the macOS version
    /// reading <c>preferences.scenes.enabledScenes</c> — a scene disabled after
    /// startup must stop answering its digit.</param>
    /// <param name="pending">Where the pick waits for the next share.</param>
    /// <param name="hotkeys">Null is legal: the controller still works when
    /// something else (tests, a future tray menu) calls
    /// <see cref="HandleSceneDigit"/>.</param>
    /// <param name="notify">Toast text hook, e.g. 下次转发使用场景「…」.</param>
    public SceneShortcutController(
        Func<IReadOnlyList<WeChatScene>> enabledScenes,
        NextForwardSceneStore pending,
        ISceneHotkeySource? hotkeys = null,
        Action<string>? notify = null)
    {
        _enabledScenes = enabledScenes;
        _pending = pending;
        _hotkeys = hotkeys;
        _notify = notify;
        if (_hotkeys is not null)
        {
            _hotkeys.SceneDigitPressed += (_, digit) => HandleSceneDigit(digit);
            _hotkeys.Start();
        }
    }

    /// <summary>The scene picked for the next forward, or ignored.</summary>
    public void HandleSceneDigit(int digit)
    {
        if (digit is < 1 or > 9)
            return;
        var scenes = _enabledScenes();
        var index = digit - 1;
        if (index >= scenes.Count)
            return;
        var scene = scenes[index];
        _pending.SelectForNextForward(scene.Id);
        _notify?.Invoke($"下次转发使用场景「{scene.Name}」");
    }

    public void Dispose() => _hotkeys?.Dispose();
}

/// <summary>
/// Ctrl+Alt+1…9 through <c>RegisterHotKey</c>. A dedicated thread owns the
/// message loop: hotkeys registered against <c>NULL</c> post
/// <c>WM_HOTKEY</c> to the registering thread's queue, so no window is needed.
/// <c>MOD_NOREPEAT</c> keeps a held key from queueing repeats — the macOS
/// version ignores <c>isARepeat</c> the same way.
/// </summary>
public sealed class WindowsSceneHotkeySource : ISceneHotkeySource
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;
    private const int WmQuit = 0x0012;

    private readonly TaskCompletionSource<uint> _threadId =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;

    public WindowsSceneHotkeySource()
    {
        _thread = new Thread(RunLoop) { IsBackground = true, Name = "WeChatBridge.SceneHotkeys" };
    }

    public event EventHandler<int>? SceneDigitPressed;

    public void Start() => _thread.Start();

    private void RunLoop()
    {
        _threadId.SetResult(NativeMethods.GetCurrentThreadId());
        var registered = new List<int>();
        try
        {
            for (var digit = 1; digit <= 9; digit++)
            {
                // VK 0x31–0x39 are the digit row keys 1–9.
                if (NativeMethods.RegisterHotKey(
                        IntPtr.Zero, digit, ModControl | ModAlt | ModNoRepeat, (uint)(0x30 + digit)))
                    registered.Add(digit);
            }
            while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.Message == WmHotkey)
                    SceneDigitPressed?.Invoke(this, unchecked((int)message.WParam));
            }
        }
        finally
        {
            foreach (var id in registered)
                NativeMethods.UnregisterHotKey(IntPtr.Zero, id);
        }
    }

    public void Dispose()
    {
        if (!_threadId.Task.IsCompleted)
            return;
        // Quit ends GetMessage; the loop's finally unregisters the hotkeys.
        NativeMethods.PostThreadMessage(_threadId.Task.Result, WmQuit, UIntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostThreadMessage(uint idThread, uint msg, UIntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)]
        internal struct MSG
        {
            public IntPtr HWnd;
            public int Message;
            public UIntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public int PtX;
            public int PtY;
        }
    }
}
