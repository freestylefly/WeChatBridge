using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows;

/// <summary>
/// The tray menu as data: title, enabled state and which command a click runs.
/// Kept free of Win32 so the shape — and the zh-CN wording — is readable at a
/// glance. Mirrors <c>StatusItemController.menuNeedsUpdate</c> on macOS: the
/// model is rebuilt on every open because history changes underneath it.
/// </summary>
public static class TrayMenu
{
    /// <summary>Ten batches is about a screen of menu; past that, 全部记录… is the answer.</summary>
    public const int RecentLimit = 10;

    public enum Command
    {
        None,
        ToggleWindow,
        OpenHistory,
        ClearHistory,
        OpenInbox,
        Collections,
        Settings,
        About,
        Quit,
    }

    /// <summary>A null <see cref="Title"/> draws a separator.</summary>
    public sealed record Entry(
        string? Title,
        bool Enabled = true,
        Command Action = Command.None,
        IReadOnlyList<Entry>? Children = null);

    /// <summary>
    /// The whole menu, top to bottom. The window toggle leads because a tray
    /// app's first job is to reach the window; the rest follows the macOS menu
    /// (最近记录 ▸, actions, 退出). macOS keeps 检查更新… here; Windows has no
    /// updater yet, so the slot is simply absent.
    /// </summary>
    public static IReadOnlyList<Entry> Build(
        IReadOnlyList<ReadyBatch> batches,
        bool windowVisible,
        DateTimeOffset now,
        BatchCollection? current = null)
    {
        return
        [
            new Entry(windowVisible ? L10n.Text("隐藏主窗口") : L10n.Text("显示主窗口"), Action: Command.ToggleWindow),
            ..(current is null ? Array.Empty<Entry>() : [new Entry(L10n.Format($"继续收集 · {current.BatchIDs.Count} 批"), Action: Command.Collections)]),
            new Entry(L10n.Text("最近记录"), Children: BuildRecent(batches, now)),
            new Entry(null),
            new Entry(L10n.Text("打开 Inbox"), Action: Command.OpenInbox),
            new Entry(L10n.Text("设置…"), Action: Command.Settings),
            new Entry(L10n.Text("关于微信流…"), Action: Command.About),
            new Entry(null),
            new Entry(L10n.Text("退出微信流"), Action: Command.Quit),
        ];
    }

    private static IReadOnlyList<Entry> BuildRecent(IReadOnlyList<ReadyBatch> batches, DateTimeOffset now)
    {
        if (batches.Count == 0)
            return [new Entry(L10n.Text("暂无记录"), Enabled: false)];

        var entries = batches.Take(RecentLimit)
            .Select(batch => new Entry(RecentTitle(batch, now), Action: Command.OpenHistory))
            .ToList();
        entries.Add(new Entry(null));
        entries.Add(new Entry(L10n.Text("全部记录…"), Action: Command.OpenHistory));
        // Reaching here at all means there is history to discard.
        entries.Add(new Entry(L10n.Text("清空记录"), Action: Command.ClearHistory));
        return entries;
    }

    /// <summary>
    /// One 最近记录 row: 「聊天记录.zip 等 3 个 · Claude · 已送达 · 14:32」 — the
    /// macOS <c>menuTitle</c> (name · destination · timestamp) plus the status
    /// pill's wording, so the menu says what became of the share, not just what
    /// it was.
    /// </summary>
    public static string RecentTitle(ReadyBatch batch, DateTimeOffset now)
    {
        var status = new BatchRow { Batch = batch }.StatusText;
        return string.Join(" · ", new[]
        {
            HistoryLabels.Name(batch, HistoryLabels.MenuNameLimit),
            status,
            MenuTimestamp(batch.CreatedAt, now),
        }.Where(part => part.Length > 0));
    }

    /// <summary>「14:32」 today, 「9月4日 14:32」 this year, 「2025年9月4日 14:32」 before that.</summary>
    public static string MenuTimestamp(DateTimeOffset createdAt, DateTimeOffset now)
    {
        var local = createdAt.LocalDateTime;
        var today = now.LocalDateTime.Date;
        var clock = HistoryLabels.ClockTime(createdAt);
        if (local.Date == today)
            return clock;
        var zh = L10n.Culture;
        var day = local.Year == today.Year
            ? local.ToString(L10n.Text("M月d日"), zh)
            : local.ToString(L10n.Text("yyyy年M月d日"), zh);
        return $"{day} {clock}";
    }
}

/// <summary>
/// The notification-area icon: the always-visible entry point, port of
/// <c>StatusItemController</c>. Left click opens the window, right click opens
/// the menu — the same two gestures the macOS status item sends.
///
/// Pure Win32 rather than <c>System.Windows.Forms.NotifyIcon</c>: the project
/// is WPF-only (<c>UseWPF</c>), and turning on WinForms just to get a hidden
/// message window, an icon slot and a context menu buys nothing a
/// <see cref="HwndSource"/> + <c>Shell_NotifyIcon</c> + <c>TrackPopupMenuEx</c>
/// doesn't already do — without adding a second UI stack to the process.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    /// <summary>Stable GUID: Explorer keys icon placement/settings to it across restarts.</summary>
    private static readonly Guid IconGuid = new("0E7C4A91-6B2F-4D8E-9A51-2F3C9D5B8E71");

    /// <summary>Right-click can deliver both NIN_POPUPMENU and WM_CONTEXTMENU; one menu per gesture.</summary>
    private const long MenuDebounceMs = 400;

    private readonly MainViewModel _model;
    private readonly Func<MainWindow?> _window;
    private readonly Action _quit;
    private readonly HwndSource _source;
    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private bool _added;
    private bool _menuOpen;
    private long _menuShownAt = -MenuDebounceMs;

    private readonly Dictionary<int, Action> _commands = [];
    private int _nextCommandId;

    /// <summary>The toast action the open balloon stands in for; cleared on dismiss.</summary>
    private Action? _balloonAction;
    /// <summary>
    /// When the last balloon was raised. NIN_BALLOONSHOW and NIN_POPUPMENU share
    /// the 0x0402 low word, so a popup arriving right after a balloon request is
    /// read as the balloon's arrival notice, not a right-click.
    /// </summary>
    private long _balloonRequestedAt = -2000;

    /// <summary>
    /// Create on the UI thread (App.OnStartup). <paramref name="window"/> is a
    /// delegate so a future close-to-tray mode can hand back a new window
    /// instead of a stale reference.
    /// </summary>
    public TrayIconService(MainViewModel model, Func<MainWindow?> window, Action quit)
    {
        _model = model;
        _window = window;
        _quit = quit;

        // A real top-level window kept hidden off-screen — NOT HWND_MESSAGE:
        // message-only windows can never be foreground, and TrackPopupMenuEx
        // destroys the menu the moment it loses activation without one. That
        // was why right-click looked dead: the menu appeared and died instantly.
        _source = new HwndSource(new HwndSourceParameters("WeChatBridgeTray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            ExtendedWindowStyle = 0x00000080, // WS_EX_TOOLWINDOW: stays out of Alt-Tab/taskbar
            PositionX = -32000,
            PositionY = -32000,
        });
        _source.AddHook(WndProc);
        _hwnd = _source.Handle;

        _icon = LoadTrayIcon();
        var data = IconData(Native.NifMessage | Native.NifIcon | Native.NifTip | Native.NifGuid);
        data.uCallbackMessage = Native.WmTrayIcon;
        data.hIcon = _icon;
        data.szTip = "微信流";
        _added = Native.Shell_NotifyIcon(Native.NimAdd, ref data);
        var versioned = false;
        if (_added)
        {
            // Version 4 gives NIN_* notifications and per-event coordinates.
            var version = IconData(Native.NifVersion);
            version.uTimeoutOrVersion = Native.NotifyIconVersion4;
            versioned = Native.Shell_NotifyIcon(Native.NimSetVersion, ref version);
        }

        // The macOS toast is a floating capsule that exists whether or not a
        // window does. The in-window capsule covers the visible case; when the
        // window is hidden — the resident app's normal state — a tray balloon
        // says it instead, or a failure would go unnoticed.
        _model.ToastRequested += OnToastRequested;
        _model.DeliveryNotificationRequested += OnDeliveryNotification;

        InboxLogger.Write(new InboxPaths(), $"tray icon registered: {_added}, v4={versioned}, hwnd={_hwnd}");
    }

    // MARK: - Window procedure

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WmTrayIcon)
        {
            var notifyCode = (uint)lParam.ToInt64() & 0xFFFF;
            InboxLogger.Write(new InboxPaths(), $"tray event 0x{notifyCode:x4}");
            switch (notifyCode)
            {
                // Left click opens the window, exactly like the macOS status item.
                case Native.WmLButtonUp:
                case Native.WmLButtonDblClk:
                case Native.NinSelect:
                case Native.NinKeySelect:
                    ToggleWindow(showOnly: true);
                    handled = true;
                    break;
                // The pre-v4 contract — and in practice the only one this
                // shell honours: raw WM_RBUTTONUP arrives instead of
                // NIN_POPUPMENU (seen live in windows.log), so both paths
                // lead to the same menu.
                case Native.WmRButtonUp:
                case Native.NinPopupMenu:
                    // 0x0402 is also NIN_BALLOONSHOW: a balloon we just raised
                    // reports its arrival here. Only a right-click after the
                    // notice window is a real menu request.
                    if (Environment.TickCount64 - _balloonRequestedAt >= 1500)
                        ShowContextMenu();
                    handled = true;
                    break;
                case Native.NinBalloonUserClick:
                    // The toast's action, run as if the capsule's button was
                    // pressed — and show the window first, since the action is
                    // usually a pane navigation that a hidden window hides.
                    ToggleWindow(showOnly: true);
                    _balloonAction?.Invoke();
                    _balloonAction = null;
                    handled = true;
                    break;
                case Native.NinBalloonHide:
                case Native.NinBalloonTimeout:
                    _balloonAction = null;
                    break;
                case Native.WmContextMenu:
                    ShowContextMenu();
                    handled = true;
                    break;
            }
        }
        return IntPtr.Zero;
    }

    // MARK: - Actions

    private void ToggleWindow(bool showOnly = false)
    {
        if (_window() is not { } window)
            return;
        if (window.IsVisible && !showOnly)
            window.Hide();
        else
            window.BringToFront();
    }

    private void OpenTab(AppTab tab)
    {
        _model.Navigate(tab);
        _window()?.BringToFront();
    }

    private void Dispatch(TrayMenu.Command command)
    {
        switch (command)
        {
            case TrayMenu.Command.ToggleWindow: ToggleWindow(); break;
            case TrayMenu.Command.OpenHistory: OpenTab(AppTab.History); break;
            case TrayMenu.Command.ClearHistory: _model.DiscardAll(); break;
            case TrayMenu.Command.Collections: _model.ShowCollection(); break;
            case TrayMenu.Command.OpenInbox: _model.RevealInbox(); break;
            case TrayMenu.Command.Settings: OpenTab(AppTab.General); break;
            case TrayMenu.Command.About: OpenTab(AppTab.About); break;
            case TrayMenu.Command.Quit: _quit(); break;
        }
    }

    // MARK: - Balloon

    /// <summary>
    /// When the main window is hidden the in-window capsule cannot be seen, so
    /// the toast becomes a tray balloon. Its action — 去添加应用 and friends —
    /// rides along: a click on the balloon runs it.
    /// </summary>
    private void OnDeliveryNotification(string message, string actionTitle, Action action) =>
        ShowNotification(message, actionTitle, action, false);

    private void OnToastRequested(string message, string? actionTitle, Action? action, bool warning)
    {
        if (_window()?.IsVisible == true) return;
        ShowNotification(message, actionTitle, action, warning);
    }

    private void ShowNotification(string message, string? actionTitle, Action? action, bool warning)
    {
        if (!_added)
            return;
        _balloonAction = action;
        _balloonRequestedAt = Environment.TickCount64;
        var data = IconData(Native.NifInfo);
        data.szInfoTitle = "微信流";
        data.szInfo = actionTitle is null ? message : L10n.Format($"{message}（点击：{actionTitle}）");
        data.dwInfoFlags = warning ? Native.NiifWarning : Native.NiifInfo;
        if (!Native.Shell_NotifyIcon(Native.NimModify, ref data))
            _balloonAction = null;
    }

    // MARK: - Context menu

    /// <summary>
    /// Rebuilt on every open rather than kept in sync — the same call macOS
    /// makes: history and retention change underneath, and a menu seen for a
    /// second is the cheapest thing to rebuild.
    /// </summary>
    private void ShowContextMenu()
    {
        if (_menuOpen || Environment.TickCount64 - _menuShownAt < MenuDebounceMs)
            return;

        var menu = Native.CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        _commands.Clear();
        _nextCommandId = 1;
        var windowVisible = _window()?.IsVisible == true;
        Populate(menu, TrayMenu.Build(_model.Batches, windowVisible, DateTimeOffset.Now, _model.Collections.Ledger.Current));

        Native.GetCursorPos(out var point);
        // The foreground trick every tray menu needs: without it the popup does
        // not dismiss when the user clicks elsewhere.
        Native.SetForegroundWindow(_hwnd);
        _menuOpen = true;
        var picked = Native.TrackPopupMenuEx(
            menu,
            Native.TpmReturnCmd | Native.TpmNoNotify | Native.TpmRightButton
                | Native.TpmRightAlign | Native.TpmBottomAlign,
            point.X,
            point.Y,
            _hwnd,
            IntPtr.Zero);
        _menuOpen = false;
        _menuShownAt = Environment.TickCount64;
        // Per TrackPopupMenu docs: lets the menu finish fading before dismissal.
        Native.PostMessage(_hwnd, Native.WmNull, IntPtr.Zero, IntPtr.Zero);
        Native.DestroyMenu(menu);

        if (picked != 0 && _commands.TryGetValue(picked, out var action))
            action();
    }

    private void Populate(IntPtr menu, IReadOnlyList<TrayMenu.Entry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Title is null)
            {
                Native.AppendMenu(menu, Native.MfSeparator, UIntPtr.Zero, null);
                continue;
            }
            if (entry.Children is { } children)
            {
                var submenu = Native.CreatePopupMenu();
                Populate(submenu, children);
                Native.AppendMenu(
                    menu,
                    Native.MfPopup | (entry.Enabled ? 0 : Native.MfGrayed),
                    new UIntPtr((ulong)submenu.ToInt64()),
                    entry.Title);
                continue;
            }
            var command = entry.Action;
            var id = _nextCommandId++;
            _commands[id] = () => Dispatch(command);
            Native.AppendMenu(
                menu,
                Native.MfString | (entry.Enabled ? 0 : Native.MfGrayed),
                new UIntPtr((ulong)id),
                entry.Title);
        }
    }

    // MARK: - Icon

    /// <summary>
    /// The packaged logo first — register-dev copies
    /// packaging\SparsePackage\Assets next to the exe — then the same PNG the
    /// macOS menu-bar item embeds, then drawn geometry. Only a build problem can
    /// reach the last one.
    /// </summary>
    private static IntPtr LoadTrayIcon()
    {
        var size = Math.Max(16, Native.GetSystemMetrics(Native.SmCxSmIcon));
        var source = LoadPackagedLogo() ?? DecodeGlyph();
        var scaled = source is null ? DrawFallback(size) : ScaleToFit(source, size);
        return BitmapToIcon(scaled, size, size);
    }

    private static BitmapSource? LoadPackagedLogo()
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        foreach (var name in new[]
        {
            // The mark on transparency first — the white-tile packaging logo
            // disappears against a light taskbar and reads tiny at 16 px.
            "TrayGlyph.png",
            "Square44x44Logo.targetsize-256_altform-unplated.png",
            "Square44x44Logo.targetsize-256.png",
            "Square44x44Logo.targetsize-32.png",
            "Square44x44Logo.png",
        })
        {
            var path = Path.Combine(assets, name);
            if (!File.Exists(path))
                continue;
            try
            {
                var frame = BitmapFrame.Create(new Uri(path, UriKind.Absolute));
                frame.Freeze();
                return frame;
            }
            catch (Exception)
            {
                // A corrupt asset is worth the drawn glyph, not a dead tray icon.
            }
        }
        return null;
    }

    /// <summary>
    /// The monochrome menu-bar glyph from StatusGlyph.swift, kept as the same
    /// base64 PNG so dev builds — which have no Assets folder beside the exe —
    /// still show the app's own mark rather than a stock icon.
    /// </summary>
    private static BitmapSource? DecodeGlyph()
    {
        try
        {
            var frame = BitmapFrame.Create(new MemoryStream(Convert.FromBase64String(GlyphPng)));
            frame.Freeze();
            return frame;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Uniform-fit into a square icon: the packaged logo fills it, the 60×44 glyph letterboxes.</summary>
    private static BitmapSource ScaleToFit(BitmapSource source, int size)
    {
        var scale = Math.Min((double)size / source.PixelWidth, (double)size / source.PixelHeight);
        var width = source.PixelWidth * scale;
        var height = source.PixelHeight * scale;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var context = visual.RenderOpen())
            context.DrawImage(source, new Rect((size - width) / 2, (size - height) / 2, width, height));
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Last resort: the brand green rounded square with a white dot — a two-shape chat bubble.</summary>
    private static BitmapSource DrawFallback(int size)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var radius = size * 0.22;
            context.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(0x12, 0xA1, 0x50)),
                null,
                new Rect(0, 0, size, size),
                radius,
                radius);
            context.DrawEllipse(
                Brushes.White,
                null,
                new Point(size * 0.5, size * 0.44),
                size * 0.16,
                size * 0.16);
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Pbgra32 pixels to HICON. A 32-bpp colour bitmap carries the alpha
    /// channel; the AND mask stays all-zero so the icon engine honours it.
    /// </summary>
    private static IntPtr BitmapToIcon(BitmapSource source, int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        var colorBits = Marshal.AllocHGlobal(pixels.Length);
        Marshal.Copy(pixels, 0, colorBits, pixels.Length);
        var maskBits = IntPtr.Zero;
        var color = IntPtr.Zero;
        var mask = IntPtr.Zero;
        try
        {
            // A monochrome mask row is word-aligned: ((w + 15) / 16) * 2 bytes.
            var maskStride = ((width + 15) / 16) * 2;
            maskBits = Marshal.AllocHGlobal(maskStride * height); // zero-filled
            color = Native.CreateBitmap(width, height, 1, 32, colorBits);
            mask = Native.CreateBitmap(width, height, 1, 1, maskBits);
            var info = new Native.ICONINFO
            {
                fIcon = true,
                hbmMask = mask,
                hbmColor = color,
            };
            return Native.CreateIconIndirect(ref info);
        }
        finally
        {
            // CreateIconIndirect copies the bitmaps.
            if (color != IntPtr.Zero)
                Native.DeleteObject(color);
            if (mask != IntPtr.Zero)
                Native.DeleteObject(mask);
            Marshal.FreeHGlobal(colorBits);
            if (maskBits != IntPtr.Zero)
                Marshal.FreeHGlobal(maskBits);
        }
    }

    public void Dispose()
    {
        _model.ToastRequested -= OnToastRequested;
        _model.DeliveryNotificationRequested -= OnDeliveryNotification;
        if (_added)
        {
            var data = IconData(0);
            Native.Shell_NotifyIcon(Native.NimDelete, ref data);
            _added = false;
        }
        _source.Dispose();
        if (_icon != IntPtr.Zero)
            Native.DestroyIcon(_icon);
    }

    private Native.NOTIFYICONDATA IconData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<Native.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = flags,
        guidItem = IconGuid,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    // MARK: - Win32

    private static class Native
    {
        public const uint WmNull = 0x0000;
        public const uint WmContextMenu = 0x007B;
        public const uint WmLButtonUp = 0x0202;
        public const uint WmLButtonDblClk = 0x0203;
        public const uint WmRButtonUp = 0x0205;
        public const uint NinSelect = 0x0400;   // WM_USER + 0
        public const uint NinKeySelect = 0x0401;
        public const uint NinPopupMenu = 0x0402;  // same low word as NIN_BALLOONSHOW
        public const uint NinBalloonHide = 0x0403;
        public const uint NinBalloonTimeout = 0x0404;
        public const uint NinBalloonUserClick = 0x0405;
        public const uint WmTrayIcon = 0x8000 + 1; // WM_APP + 1

        public const uint NimAdd = 0;
        public const uint NimModify = 1;
        public const uint NimDelete = 2;
        public const uint NimSetVersion = 4;

        public const uint NifMessage = 0x1;
        public const uint NifIcon = 0x2;
        public const uint NifTip = 0x4;
        public const uint NifVersion = 0x8;
        public const uint NifInfo = 0x10;
        public const uint NifGuid = 0x20;
        public const uint NotifyIconVersion4 = 4;

        public const int NiifInfo = 0x1;
        public const int NiifWarning = 0x2;

        public const uint MfString = 0x0;
        public const uint MfGrayed = 0x1;
        public const uint MfPopup = 0x10;
        public const uint MfSeparator = 0x800;

        public const uint TpmReturnCmd = 0x100;
        public const uint TpmNoNotify = 0x80;
        public const uint TpmRightButton = 0x2;
        public const uint TpmRightAlign = 0x8;
        public const uint TpmBottomAlign = 0x20;

        public const int SmCxSmIcon = 49;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ICONINFO
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        /// <summary>Vista+ layout; <c>cbSize</c> is what the Shell version-checks.</summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public int uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public int dwState;
            public int dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public int dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("user32.dll")]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
        public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

        [DllImport("user32.dll")]
        public static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        public static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        public static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, IntPtr lpBits);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);
    }

    // The same bytes StatusGlyph.swift embeds — 60×44 monochrome template PNG.
    private const string GlyphPng = "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" +
        "yXoTrYsAAAAASUVORK5CYII=";
}
