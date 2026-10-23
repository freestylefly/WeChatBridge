using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// The helper's early warning for the app's scene prefetch: published the
/// moment staging completes — while the entry pick is still in front of the
/// user — so the WeChat title read and transcript parse finish inside the
/// decision time instead of after it. Single-slot: the newest share wins, and
/// the app matches by batch id rather than position so a hint for a cancelled
/// pick is discarded instead of applied to the wrong batch.
/// </summary>
public sealed record PrefetchHint(
    [property: JsonPropertyName("batchId")] Guid BatchId,
    [property: JsonPropertyName("files")] IReadOnlyList<string> RelativePaths)
{
    public const string FileName = "prefetch-hint.json";
    public const string EventName = "Local\\WeChatBridge.Windows.PrefetchHint";

    /// <summary>
    /// Full paths wherever the batch currently lives: Ready after commit, the
    /// staging tree while the pick is still open. Commit renames the
    /// directory, so the hint stores the relative paths and resolves both.
    /// </summary>
    public List<string> ResolvePaths(InboxPaths paths)
    {
        var staging = Path.Combine(paths.Staging, $"{BatchId:N}.staging");
        var ready = Path.Combine(paths.Ready, BatchId.ToString("D"));
        var directory = Directory.Exists(ready) ? ready : staging;
        return RelativePaths
            .Select(r => Path.Combine(directory, r.Replace('/', Path.DirectorySeparatorChar)))
            .ToList();
    }

    /// <summary>
    /// Writes the hint and signals the resident app. Best effort: the share
    /// must never fail because an optimisation did.
    /// </summary>
    public static void Publish(InboxPaths paths, StagedShare staged)
    {
        try
        {
            var hint = new PrefetchHint(
                staged.BatchId,
                staged.Manifest.Items.Select(i => i.RelativePath).ToList());
            var file = Path.Combine(paths.Root, FileName);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(hint, BatchManifest.JsonOptions));
            File.Move(temp, file, overwrite: true);
            using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            signal.Set();
        }
        catch
        {
        }
    }

    /// <summary>
    /// Reads and deletes the hint — the first consumer wins. A stale file can
    /// only waste one background prefetch, never misdirect one: callers still
    /// compare <see cref="BatchId"/>.
    /// </summary>
    public static PrefetchHint? Consume(InboxPaths paths)
    {
        var file = Path.Combine(paths.Root, FileName);
        try
        {
            if (!File.Exists(file))
                return null;
            var text = File.ReadAllText(file);
            try { File.Delete(file); } catch { }
            var hint = JsonSerializer.Deserialize<PrefetchHint>(text, BatchManifest.JsonOptions);
            return hint is { RelativePaths.Count: > 0 } ? hint : null;
        }
        catch
        {
            return null;
        }
    }
}
