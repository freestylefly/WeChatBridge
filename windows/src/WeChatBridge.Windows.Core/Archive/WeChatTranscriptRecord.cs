using System.Globalization;
using System.Text.RegularExpressions;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// One parsed message from the TXT inside a WeChat export, ported from
/// <c>WeChatTranscriptRecord</c> in WeChatForward.swift. The format WeChat
/// writes is line-oriented and undocumented: a <c>·sender</c> line, a
/// <c>yyyy年M月d日 HH:mm</c> timestamp line, then the body up to the next
/// header. Parsing fails closed — a body that does not start with a header is
/// not a transcript this build recognises.
/// </summary>
public sealed record WeChatTranscriptRecord(string Sender, DateTimeOffset Date, string Text)
{
    private static readonly Regex Header = new(
        @"^·([^\n]+)\n(\d{4})年(\d{1,2})月(\d{1,2})日 (\d{2}):(\d{2})\n",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public static List<WeChatTranscriptRecord> Parse(string body, TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        // CRLF endings and a BOM ahead of the first header are container noise,
        // not part of any record.
        var normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\uFEFF');
        var matches = Header.Matches(normalized);
        if (matches.Count == 0 || matches[0].Index != 0)
            throw new WeChatReadException(WeChatReadError.InvalidTranscript);

        var records = new List<WeChatTranscriptRecord>(matches.Count);
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            var date = ParseDate(match, zone);
            var start = match.Index + match.Length;
            var end = index + 1 < matches.Count ? matches[index + 1].Index : normalized.Length;
            records.Add(new WeChatTranscriptRecord(
                match.Groups[1].Value,
                date,
                normalized[start..end].Trim()));
        }
        return records;
    }

    private static DateTimeOffset ParseDate(Match match, TimeZoneInfo zone)
    {
        try
        {
            var local = new DateTime(
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture),
                0,
                DateTimeKind.Unspecified);
            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or FormatException or OverflowException)
        {
            // A header-shaped line with an impossible date is still a corrupt transcript.
            throw new WeChatReadException(WeChatReadError.InvalidTranscript, error);
        }
    }
}
