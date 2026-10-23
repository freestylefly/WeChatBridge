using System.Runtime.InteropServices;
using System.Text;
using WeChatBridge.Windows.Core.Interop;

namespace WeChatBridge.Windows.Core.Delivery;

/// <summary>
/// Puts committed batch files on the Windows clipboard — the counterpart of
/// <c>FilePasteboard.swift</c>.
///
/// Writes go through raw Win32 (<c>CF_HDROP</c>/<c>CF_UNICODETEXT</c>) rather
/// than <c>System.Windows.Clipboard</c>: this assembly targets plain
/// <c>net10.0</c> and has no WPF reference. The one rule that carries over from
/// the share helper (AGENTS.md: 剪贴板必须 STA 线程) is kept — every write runs
/// on a dedicated STA thread even though the raw API would technically open on
/// any thread, so the OLE clipboard layer behaves identically to the WPF path.
///
/// Both halves of the product write the clipboard for different reasons: the
/// share helper writes it the moment the copy lands, and the delivery engine
/// rewrites it immediately before an automated paste — and again on every
/// failure — so what a manual Ctrl+V produces is always this batch's files.
/// </summary>
public static class WindowsClipboard
{
    /// <summary>Offsets into the DROPFILES header: pFiles, then pt(8), fNC, fWide.</summary>
    private const int DropFilesSize = 20;

    /// <summary>Clipboard owners come and go; a brief retry beats an instant refusal.</summary>
    private const int OpenAttempts = 10;

    /// <summary>Real files on the clipboard, as an Explorer copy would leave them.</summary>
    public static bool WriteFileDropList(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return false;
        // One HGLOBAL the clipboard takes ownership of on success.
        var handle = BuildDropFiles(paths);
        if (handle == 0)
            return false;
        if (!WriteOnSta(Win32.CF_HDROP, handle))
        {
            Win32.GlobalFree(handle);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Plain text and nothing else: a prompt, or a quoted-paths line with a
    /// prompt folded into it — the target that asked for paths
    /// (<see cref="ForwardTarget.PastesPathOnly"/>) is one that would take the
    /// file over the text if both were offered, then do nothing with it.
    /// </summary>
    public static bool WriteText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        var bytes = Encoding.Unicode.GetBytes(text + '\0');
        var handle = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, (nuint)bytes.Length);
        if (handle == 0)
            return false;
        var pointer = Win32.GlobalLock(handle);
        if (pointer == 0)
        {
            Win32.GlobalFree(handle);
            return false;
        }
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        Win32.GlobalUnlock(handle);
        if (!WriteOnSta(Win32.CF_UNICODETEXT, handle))
        {
            Win32.GlobalFree(handle);
            return false;
        }
        return true;
    }

    /// <summary>One payload of a <see cref="PastePlan"/>.</summary>
    public static bool Write(PastePayload payload) => payload switch
    {
        PastePayload.Files files => WriteFileDropList(files.Paths),
        PastePayload.Text text => WriteText(text.Value),
        _ => false,
    };

    /// <summary>
    /// What dropping the same files on a terminal window would have typed.
    ///
    /// Double quotes rather than macOS's POSIX single quotes: cmd and
    /// PowerShell are the terminals here, and <c>"…"</c> is the one quoting
    /// both read the same way. Windows file names cannot contain a literal
    /// <c>"</c>, so no escaping pass is needed. Each path ends in a space so
    /// the user can carry on typing the command.
    /// </summary>
    public static string ShellLine(IReadOnlyList<string> paths) =>
        string.Concat(paths.Select(p => Quote(p) + " "));

    public static string Quote(string path) => $"\"{path}\"";

    /// <summary>DROPFILES header + UTF-16 paths, each NUL-terminated, plus a final NUL.</summary>
    private static nint BuildDropFiles(IReadOnlyList<string> paths)
    {
        var charCount = paths.Sum(p => p.Length + 1) + 1;
        var byteCount = DropFilesSize + charCount * 2;
        var handle = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, (nuint)byteCount);
        if (handle == 0)
            return 0;
        var pointer = Win32.GlobalLock(handle);
        if (pointer == 0)
        {
            Win32.GlobalFree(handle);
            return 0;
        }
        try
        {
            // Zero-filled by GlobalAlloc's default zero init is not guaranteed —
            // write every field explicitly.
            Marshal.WriteInt32(pointer, 0, DropFilesSize);        // pFiles
            Marshal.WriteInt64(pointer, 4, 0);                    // pt
            Marshal.WriteInt32(pointer, 12, 0);                   // fNC
            Marshal.WriteInt32(pointer, 16, 1);                   // fWide: names are Unicode
            var offset = DropFilesSize;
            foreach (var path in paths)
            {
                var bytes = Encoding.Unicode.GetBytes(path + '\0');
                Marshal.Copy(bytes, 0, pointer + offset, bytes.Length);
                offset += bytes.Length;
            }
            Marshal.WriteInt16(pointer, offset, 0);               // list-terminating NUL
            return handle;
        }
        finally
        {
            Win32.GlobalUnlock(handle);
        }
    }

    /// <summary>
    /// Open → empty → set → close on a short-lived STA thread, mirroring the
    /// share helper's pattern. The caller keeps ownership of
    /// <paramref name="data"/> unless <c>SetClipboardData</c> accepts it.
    /// </summary>
    private static bool WriteOnSta(uint format, nint data)
    {
        var succeeded = false;
        var thread = new Thread(() =>
        {
            var opened = false;
            for (var attempt = 0; attempt < OpenAttempts && !opened; attempt++)
            {
                opened = Win32.OpenClipboard(0);
                if (!opened)
                    Thread.Sleep(50);
            }
            if (!opened)
                return;
            try
            {
                Win32.EmptyClipboard();
                succeeded = Win32.SetClipboardData(format, data) != 0;
            }
            finally
            {
                Win32.CloseClipboard();
            }
        });
        // The guard is for CA1416 (this assembly targets plain net10.0):
        // delivery only ever runs on Windows, where the STA requirement
        // (AGENTS.md: 剪贴板必须 STA 线程) is what this thread exists for.
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return succeeded;
    }
}
