using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace WeChatBridge.Windows;

internal static class CollectionPlacement
{
    internal static Rect WorkArea(Window window, bool atCursor = false)
    {
        var transform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var inverse = transform;
        inverse.Invert();
        Point point;
        if (atCursor && GetCursorPos(out var cursor)) point = new(cursor.X, cursor.Y);
        else point = inverse.Transform(new Point(window.Left + window.ActualWidth / 2, window.Top + window.ActualHeight / 2));
        var monitor = MonitorFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y }, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return SystemParameters.WorkArea;
        var start = transform.Transform(new Point(info.Work.Left, info.Work.Top));
        var end = transform.Transform(new Point(info.Work.Right, info.Work.Bottom));
        return new Rect(start, end);
    }
    internal static bool CursorOnRight(Window window, Rect area)
    {
        if (!GetCursorPos(out var cursor)) return false;
        var transform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return transform.Transform(new Point(cursor.X, cursor.Y)).X > area.Left + area.Width / 2;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
}
