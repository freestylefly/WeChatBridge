using System.IO.Compression;
using System.Text;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// Reads the original ZIP in memory, without extracting paths or launching a
/// helper. Ported from <c>WeChatNativeArchive</c>. WeChat currently writes
/// ordinary UTF-8, deflated ZIP entries. ZIP64, encryption and unknown
/// compression methods fail closed.
/// </summary>
public static class WeChatNativeArchive
{
    /// <summary>A TXT entry worth decoding is never larger than this.</summary>
    private const int MaxTextEntryBytes = 16_777_216;

    /// <summary>Total expanded payload an archive may declare.</summary>
    private const long MaxExpandedBytes = 1_073_741_824;

    // Unix mode bits the central directory's external attributes carry.
    private const int SIfMt = 0xF000;
    private const int SIfDir = 0x4000;
    private const int SIfReg = 0x8000;

    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public sealed record Transcript(string Path, string Body, IReadOnlyList<WeChatTranscriptRecord>? Records)
    {
        public DateTimeOffset? Start => Records is { Count: > 0 } records ? records.Min(r => r.Date) : null;

        public DateTimeOffset? End => Records is { Count: > 0 } records ? records.Max(r => r.Date) : null;
    }

    /// <summary>
    /// Metadata is optional. A new text format can still be named and merged,
    /// and its original bytes remain available alongside the combined text.
    /// </summary>
    public static Transcript? GetTranscript(byte[] data, CancellationToken cancellationToken = default)
    {
        var candidates = new List<Transcript>();
        foreach (var entry in Directory(data))
        {
            if (!entry.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                || entry.Expanded > MaxTextEntryBytes)
            {
                continue;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var body = Read(entry, data, collect: true, cancellationToken);
            var text = DecodeUtf8(body);
            if (text is null)
                continue;
            List<WeChatTranscriptRecord>? records = null;
            try
            {
                records = WeChatTranscriptRecord.Parse(text);
            }
            catch (WeChatReadException)
            {
                // A TXT that does not parse is still carried as raw body.
            }
            candidates.Add(new Transcript(entry.Name, text, records));
        }
        var native = candidates.FirstOrDefault(c => LastPathComponent(c.Path) == "聊天记录.txt");
        return native ?? candidates.MaxBy(c => c.Records?.Count ?? 0);
    }

    /// <summary>
    /// Only called for the optional merge. The destination is an empty, private
    /// staging directory, and each entry is streamed with its size/CRC checked.
    /// No ZIP path or symlink is passed to an external extraction tool.
    /// </summary>
    public static List<string> Extract(byte[] data, string destination, CancellationToken cancellationToken = default)
    {
        var entries = Directory(data);
        if (System.IO.Directory.EnumerateFileSystemEntries(destination).Any())
            throw new WeChatReadException(WeChatReadError.InvalidTranscript);

        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = entry.Name.EndsWith('/') ? entry.Name[..^1] : entry.Name;
            var parts = path.Split('/');
            if (parts.Length == 0
                || parts.Any(p => p.Length == 0 || p is "." or "..")
                || path.Contains('\\')
                || path.Contains(':')
                || path.Any(c => char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Control)
                || (entry.FileType != 0
                    && entry.FileType != (entry.Name.EndsWith('/') ? SIfDir : SIfReg)))
            {
                throw new WeChatReadException(WeChatReadError.InvalidTranscript);
            }
            // Catch case/normalization aliases in parent directories too.
            for (var end = 1; end <= parts.Length; end++)
            {
                var prefix = string.Join('/', parts.Take(end));
                var key = prefix.Normalize(NormalizationForm.FormC).ToLowerInvariant();
                if (seen.TryGetValue(key, out var prior) && !string.Equals(prior, prefix, StringComparison.Ordinal))
                    throw new WeChatReadException(WeChatReadError.InvalidTranscript);
                seen[key] = prefix;
            }
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = Path.Combine(destination, entry.Name.Replace('/', Path.DirectorySeparatorChar));
            if (entry.Name.EndsWith('/'))
            {
                if (entry.Expanded != 0)
                    throw new WeChatReadException(WeChatReadError.InvalidTranscript);
                System.IO.Directory.CreateDirectory(output);
            }
            else
            {
                var parent = Path.GetDirectoryName(output);
                if (parent is not null)
                    System.IO.Directory.CreateDirectory(parent);
                // CreateNew, not Create: an entry that already exists on disk is
                // a defect in this archive, never something to overwrite.
                using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    Read(entry, data, collect: false, cancellationToken, file.Write);
                }
            }
        }
        return entries.Where(e => !e.Name.EndsWith('/')).Select(e => e.Name).ToList();
    }

    /// <summary>
    /// Checks the complete ZIP and estimates its message count when a native
    /// TXT is recognizable. Bodies, card types, dates and the selected count
    /// do not decide whether the original archive can be delivered.
    ///
    /// A future TXT format (or an attachment-only archive) has no estimate;
    /// callers can report the number selected in WeChat instead. Multiple
    /// recognizable TXT files use the largest count rather than summing
    /// attached or duplicate transcripts.
    /// </summary>
    public static int? MessageCount(byte[] data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = Directory(data);
        if (!entries.Any(e => !e.Name.EndsWith('/') && e.Expanded > 0))
            throw new WeChatReadException(WeChatReadError.InvalidTranscript);
        int? count = null;
        foreach (var entry in entries.Where(e => !e.Name.EndsWith('/')))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Large TXT files still receive the same streaming CRC check.
            // Estimating a count must not require collecting them in memory.
            var isText = entry.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                && entry.Expanded <= MaxTextEntryBytes;
            var body = Read(entry, data, collect: isText, cancellationToken);
            if (!isText)
                continue;
            var text = DecodeUtf8(body);
            if (text is null)
                continue;
            try
            {
                count = Math.Max(count ?? 0, WeChatTranscriptRecord.Parse(text).Count);
            }
            catch (WeChatReadException)
            {
                // An unrecognised TXT contributes no estimate.
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return count;
    }

    private sealed record Entry(string Name, int Method, uint Crc, long Compressed, long Expanded, long Offset, int FileType);

    private static long Number(byte[] data, long offset, int bytes)
    {
        if (offset < 0 || offset > data.Length - (long)bytes)
            throw new WeChatReadException(WeChatReadError.InvalidTranscript);
        long value = 0;
        for (var i = 0; i < bytes; i++)
            value |= (long)data[offset + i] << (i * 8);
        return value;
    }

    private static List<Entry> Directory(byte[] data)
    {
        if (data.Length < 22)
            throw new WeChatReadException(WeChatReadError.InvalidTranscript);
        var end = -1L;
        for (var i = (long)data.Length - 22; i >= Math.Max(0, data.Length - 65_557); i--)
        {
            if (Number(data, i, 4) == 0x06054b50 && i + 22 + Number(data, i + 20, 2) == data.Length)
            {
                end = i;
                break;
            }
        }
        if (end < 0
            || Number(data, end + 4, 2) != 0
            || Number(data, end + 6, 2) != 0)
        {
            throw new WeChatReadException(WeChatReadError.InvalidTranscript);
        }
        var count = Number(data, end + 10, 2);
        var size = Number(data, end + 12, 4);
        var cursor = Number(data, end + 16, 4);
        var start = cursor;
        if (count < 1 || count > 1000 || Number(data, end + 8, 2) != count || cursor + size != end)
            throw new WeChatReadException(WeChatReadError.InvalidTranscript);

        var entries = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        for (var i = 0L; i < count; i++)
        {
            if (Number(data, cursor, 4) != 0x02014b50)
                throw new WeChatReadException(WeChatReadError.InvalidTranscript);
            var flags = Number(data, cursor + 8, 2);
            var method = Number(data, cursor + 10, 2);
            var nameLength = Number(data, cursor + 28, 2);
            var extra = Number(data, cursor + 30, 2);
            var comment = Number(data, cursor + 32, 2);
            if ((flags & 1) != 0
                || method is not (0 or 8)
                || Number(data, cursor + 34, 2) != 0
                || cursor + 46 + nameLength + extra + comment > end)
            {
                throw new WeChatReadException(WeChatReadError.InvalidTranscript);
            }
            string name;
            try
            {
                name = StrictUtf8.GetString(data, (int)(cursor + 46), (int)nameLength);
            }
            catch (DecoderFallbackException error)
            {
                throw new WeChatReadException(WeChatReadError.InvalidTranscript, error);
            }
            // Swift's Set<String> dedupes canonically; NFC approximates that.
            if (name.Length == 0 || !seen.Add(name.Normalize(NormalizationForm.FormC)))
                throw new WeChatReadException(WeChatReadError.InvalidTranscript);
            var entry = new Entry(
                name,
                (int)method,
                (uint)Number(data, cursor + 16, 4),
                Number(data, cursor + 20, 4),
                Number(data, cursor + 24, 4),
                Number(data, cursor + 42, 4),
                (int)((Number(data, cursor + 38, 4) >> 16) & SIfMt));
            total += entry.Expanded;
            if (total > MaxExpandedBytes || entry.Offset >= start)
                throw new WeChatReadException(WeChatReadError.InvalidTranscript);
            entries.Add(entry);
            cursor += 46 + nameLength + extra + comment;
        }
        if (cursor != end)
            throw new WeChatReadException(WeChatReadError.InvalidTranscript);
        return entries;
    }

    private static byte[] Read(
        Entry entry,
        byte[] data,
        bool collect,
        CancellationToken cancellationToken,
        Action<byte[], int, int>? write = null)
    {
        if (collect && entry.Expanded > MaxTextEntryBytes)
            throw new WeChatReadException(WeChatReadError.InvalidTranscript);
        if (Number(data, entry.Offset, 4) != 0x04034b50
            || Number(data, entry.Offset + 8, 2) != entry.Method)
        {
            throw new WeChatReadException(WeChatReadError.InvalidTranscript);
        }
        var start = entry.Offset + 30 + Number(data, entry.Offset + 26, 2) + Number(data, entry.Offset + 28, 2);
        if (start > data.Length || entry.Compressed > data.Length - start)
            throw new WeChatReadException(WeChatReadError.InvalidTranscript);

        var collected = collect ? new MemoryStream((int)entry.Expanded) : null;
        var crc = new Crc32();
        long expanded = 0;
        if (entry.Method == 0)
        {
            if (entry.Expanded != entry.Compressed)
                throw new WeChatReadException(WeChatReadError.InvalidTranscript);
            for (var offset = 0L; offset < entry.Compressed; offset += 65_536)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(65_536, entry.Compressed - offset);
                crc.Update(data, (int)(start + offset), count);
                collected?.Write(data, (int)(start + offset), count);
                write?.Invoke(data, (int)(start + offset), count);
            }
            expanded = entry.Compressed;
        }
        else
        {
            // ZIP method 8 is a bare deflate stream — .NET's DeflateStream, not
            // ZLibStream, is the matching codec.
            using var input = new MemoryStream(data, (int)start, (int)entry.Compressed, writable: false);
            using var inflate = new DeflateStream(input, CompressionMode.Decompress);
            var buffer = new byte[65_536];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int produced;
                try
                {
                    produced = inflate.Read(buffer, 0, buffer.Length);
                }
                catch (InvalidDataException error)
                {
                    throw new WeChatReadException(WeChatReadError.InvalidTranscript, error);
                }
                crc.Update(buffer, 0, produced);
                if (produced > 0)
                {
                    collected?.Write(buffer, 0, produced);
                    write?.Invoke(buffer, 0, produced);
                }
                expanded += produced;
                if (expanded > entry.Expanded)
                    throw new WeChatReadException(WeChatReadError.InvalidTranscript);
                if (produced == 0)
                    break;
            }
        }
        if (expanded != entry.Expanded || crc.Value != entry.Crc)
            throw new WeChatReadException(WeChatReadError.InvalidTranscript);
        return collected?.ToArray() ?? [];
    }

    private static string LastPathComponent(string name)
    {
        var trimmed = name.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    private static string? DecodeUtf8(byte[] data)
    {
        try
        {
            return StrictUtf8.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>Incremental CRC-32 (IEEE 802.3), matching zlib's crc32.</summary>
    private sealed class Crc32
    {
        private static readonly uint[] Table = BuildTable();
        private uint _value = 0xFFFFFFFF;

        public void Update(byte[] buffer, int offset, int count)
        {
            for (var i = offset; i < offset + count; i++)
                _value = Table[(_value ^ buffer[i]) & 0xFF] ^ (_value >> 8);
        }

        public uint Value => _value ^ 0xFFFFFFFF;

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var bit = 0; bit < 8; bit++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                table[i] = c;
            }
            return table;
        }
    }
}
