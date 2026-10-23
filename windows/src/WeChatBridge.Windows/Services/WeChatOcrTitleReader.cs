using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using WeChatBridge.Windows.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace WeChatBridge.Windows.Services;

/// <summary>Best-effort OCR of the visible chat header only; ambiguous text falls back to manual naming.</summary>
internal static class WeChatOcrTitleReader
{
    public static async Task<GroupTitleParser.Title?> TryReadAsync()
    {
        var pixels = await Task.Run(CaptureHeader);
        if (pixels is null) return null;
        var engine = OcrEngine.TryCreateFromLanguage(new Language("zh-Hans")) ?? OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return null;
        using var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, pixels.Value.Width, pixels.Value.Height, BitmapAlphaMode.Ignore);
        using var writer = new DataWriter();
        writer.WriteBytes(pixels.Value.Bytes);
        bitmap.CopyFromBuffer(writer.DetachBuffer());
        var result = await engine.RecognizeAsync(bitmap);
        foreach (var line in result.Lines)
        {
            var text = string.Join("", line.Words.Select(w => w.Text));
            // A member count is evidence of a group header. Never assign arbitrary toolbar OCR to a direct chat.
            if (Regex.IsMatch(text, @"^.+[（(]\s*\d{1,5}\s*[)）]\s*$"))
                return GroupTitleParser.Parse([text]);
        }
        return null;
    }

    private static (byte[] Bytes, int Width, int Height)? CaptureHeader()
    {
        foreach (var name in new[] { "weixin", "wechat" })
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                var window = process.MainWindowHandle;
                if (window == 0 || IsIconic(window) || !GetWindowRect(window, out var rect)) continue;
                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                if (width < 400 || height < 240 || width > 8192 || height > 8192) continue;
                var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
                var dc = CreateCompatibleDC(0);
                var bitmap = CreateDIBSection(dc, ref info, 0, out var bits, 0, 0);
                if (dc == 0 || bitmap == 0) { if (dc != 0) DeleteDC(dc); continue; }
                var old = SelectObject(dc, bitmap);
                try
                {
                    if (!PrintWindow(window, dc, 2)) continue;
                    var startX = width / 4;
                    var cropWidth = width * 3 / 4 - 30;
                    var cropHeight = Math.Min(140, height / 4);
                    var bytes = new byte[cropWidth * cropHeight * 4];
                    for (var row = 0; row < cropHeight; row++)
                        Marshal.Copy(bits + ((row + 20) * width + startX) * 4, bytes, row * cropWidth * 4, cropWidth * 4);
                    return (bytes, cropWidth, cropHeight);
                }
                finally { SelectObject(dc, old); DeleteObject(bitmap); DeleteDC(dc); }
            }
        }
        return null;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPels, YPels;
        public uint Used, Important;
    }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
