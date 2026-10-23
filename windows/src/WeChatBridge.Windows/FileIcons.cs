using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows;

/// <summary>
/// The real file icons the 记录 rows show — the Windows port of macOS
/// <c>IconCache</c> (Design/Components.swift), which asks Launch Services
/// (<c>NSWorkspace.icon(forFile:)</c>) for the Finder icon of a batch's first
/// file. Here the answer comes from <c>SHGetFileInfo</c>: the shell icon for
/// the path itself, so a .zip draws the archive icon Explorer would.
///
/// Icons are cached in memory keyed by file extension — the same granularity
/// the macOS cache uses (<c>url.pathExtension</c>), which is what keeps every
/// 聊天记录.zip row identical and makes the lookup cheap after the first row.
/// </summary>
public static class FileIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new();

    /// <summary>The 32×32 icon the collapsed row shows for the batch's first file.</summary>
    public static ImageSource? LargeIcon(ReadyItem item) => Icon(item.FullPath, large: true);

    /// <summary>The 16×16 icon the expanded item list shows per file.</summary>
    public static ImageSource? SmallIcon(ReadyItem item) => Icon(item.FullPath, large: false);

    private static ImageSource? Icon(string path, bool large)
    {
        var key = (large ? "L:" : "S:") + Path.GetExtension(path).ToLowerInvariant();
        return Cache.GetOrAdd(key, _ => LoadIcon(path, large));
    }

    private static ImageSource? LoadIcon(string path, bool large)
    {
        // The real path first, so icons carried by the file itself (exe
        // payloads, .lnk targets) come back right; the file is usually still
        // on disk under Ready/.
        var hIcon = QueryIcon(path, 0, useFileAttributes: false, large);
        var extension = Path.GetExtension(path);
        if (hIcon == IntPtr.Zero && extension.Length > 0)
        {
            // Already gone from disk: an attribute-only query still answers
            // for the extension — the icon a fresh file of that type would get.
            hIcon = QueryIcon("file" + extension, FILE_ATTRIBUTE_NORMAL, useFileAttributes: true, large);
        }
        if (hIcon == IntPtr.Zero)
            hIcon = QueryIcon("file", FILE_ATTRIBUTE_NORMAL, useFileAttributes: true, large);
        if (hIcon == IntPtr.Zero)
            return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze(); // the cache hands this to any thread that asks
            return source;
        }
        finally
        {
            // CreateBitmapSourceFromHIcon copies — it does not own the handle.
            DestroyIcon(hIcon);
        }
    }

    private static IntPtr QueryIcon(string path, uint attributes, bool useFileAttributes, bool large)
    {
        var info = new SHFILEINFO();
        var flags = SHGFI_ICON | (large ? SHGFI_LARGEICON : SHGFI_SMALLICON);
        if (useFileAttributes)
            flags |= SHGFI_USEFILEATTRIBUTES;
        var result = SHGetFileInfo(path, attributes, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        return result == IntPtr.Zero ? IntPtr.Zero : info.hIcon;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x000000080;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;

        /// <summary>Field initializers keep the struct legal under CS0649; the marshaller overwrites both strings.</summary>
        public SHFILEINFO()
        {
            szDisplayName = string.Empty;
            szTypeName = string.Empty;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi,
        uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

/// <summary>
/// Binding glue between the row templates and <see cref="FileIcons"/>: a
/// <see cref="ReadyItem"/> — or the item list a <see cref="BatchRow"/> carries,
/// whose first item is the row's icon — becomes the shell icon.
/// ConverterParameter <c>"Small"</c> asks for the 16×16 detail icon; anything
/// else resolves the 32×32 row icon.
/// </summary>
public sealed class FileIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var item = value switch
        {
            ReadyItem single => single,
            IEnumerable<ReadyItem> list => list.FirstOrDefault(),
            _ => null,
        };
        if (item is null)
            return null;
        var small = parameter is string text
            && string.Equals(text, "Small", StringComparison.OrdinalIgnoreCase);
        return small ? FileIcons.SmallIcon(item) : FileIcons.LargeIcon(item);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
