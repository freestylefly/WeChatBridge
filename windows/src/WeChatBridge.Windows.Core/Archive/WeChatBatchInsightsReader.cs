namespace WeChatBridge.Windows.Core;

/// <summary>
/// Fills the shared <see cref="WeChatBatchInsights"/> record from a batch's
/// files, ported from <c>WeChatBatchInsightsReader</c> in
/// WeChatBatchInsights.swift. Files that are not ZIPs, or ZIPs whose text
/// cannot be parsed, contribute nothing — insights are a hint for scene
/// selection, never a gate on delivery.
/// </summary>
public static class WeChatBatchInsightsReader
{
    public static WeChatBatchInsights Read(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        var senders = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? end = null;
        foreach (var path in paths)
        {
            if (!string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
                continue;
            cancellationToken.ThrowIfCancellationRequested();
            var data = File.ReadAllBytes(path);
            WeChatNativeArchive.Transcript? transcript;
            try
            {
                transcript = WeChatNativeArchive.GetTranscript(data, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // macOS `try?` swallows a bad archive the same way: skip it.
                continue;
            }
            if (transcript?.Records is { } records)
            {
                foreach (var sender in records.Select(r => GroupName.NormalizeSender(r.Sender)))
                {
                    if (sender.Length > 0)
                        senders.Add(sender);
                }
            }
            if (transcript?.End is { } transcriptEnd)
                end = end is null || transcriptEnd > end ? transcriptEnd : end;
        }
        return new WeChatBatchInsights { Senders = senders, End = end };
    }
}
