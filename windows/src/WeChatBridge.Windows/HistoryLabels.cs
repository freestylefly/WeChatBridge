using System.Globalization;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows;

/// <summary>
/// How many bytes a batch occupies, in the style of macOS
/// <c>ByteCountFormatter</c> with <c>countStyle = .file</c>.
/// </summary>
public static class ByteText
{
    public static string Format(long byteCount)
    {
        var bytes = Math.Max(0, byteCount);
        if (bytes < 1024)
            return $"{bytes} B";
        var units = new[] { "KB", "MB", "GB", "TB" };
        var value = (double)bytes;
        var unit = -1;
        do
        {
            value /= 1024;
            unit++;
        } while (value >= 1024 && unit < units.Length - 1);
        return $"{value.ToString(value >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}

/// <summary>
/// Row labels for the 记录 list. Ported from WeChatBridgeCore/HistoryLabel.swift;
/// zh-CN strings are inlined pending the localization work package.
/// </summary>
public static class HistoryLabels
{
    /// <summary>How much of a filename a menu-width row can carry.</summary>
    public const int MenuNameLimit = 28;

    /// <summary>「聊天记录.zip」 or 「聊天记录.zip 等 3 个」.</summary>
    public static string Name(ReadyBatch batch, int? limit = null)
    {
        var first = batch.Items.FirstOrDefault();
        if (first is null)
            return string.Empty;
        var name = limit is { } n ? MiddleTruncate(first.DisplayName, n) : first.DisplayName;
        return batch.Items.Count > 1 ? L10n.Format($"{name} 等 {batch.Items.Count} 个") : name;
    }

    /// <summary>Group name wins, then scene, then the filename.</summary>
    public static string PaneTitle(ReadyBatch batch) =>
        Clean(batch.ChatName) ?? Clean(batch.SceneName) ?? Name(batch);

    /// <summary>Scene or files, then how much disk the batch uses.</summary>
    public static string PaneSubtitle(ReadyBatch batch)
    {
        var parts = new List<string>();
        var chatName = Clean(batch.ChatName);
        var sceneName = Clean(batch.SceneName);
        var title = PaneTitle(batch);

        if (sceneName is not null && title != sceneName)
        {
            parts.Add(sceneName);
            parts.Add(FileCount(batch));
        }
        else if (sceneName is not null)
        {
            parts.Add(FileCount(batch));
        }
        else if (chatName is not null)
        {
            if (batch.Items.Count > 1)
                parts.Add(FileCount(batch));
            else if (batch.Items.FirstOrDefault() is { } first)
                parts.Add(first.DisplayName);
        }
        else if (batch.Items.Count > 1)
        {
            parts.Add(FileCount(batch));
        }

        parts.Add(ByteText.Format(batch.ByteCount));
        return string.Join(" · ", parts.Where(p => p.Length > 0));
    }

    /// <summary>The app that received the latest attempt, without the verb.</summary>
    public static string DestinationName(ReadyBatch batch) =>
        Clean(batch.TargetName) ?? batch.Action.TargetDisplayName();

    /// <summary>Where the batch went, in the words that were on screen when it was sent.</summary>
    public static string Destination(ReadyBatch batch) =>
        Clean(batch.TargetName) is { } name ? L10n.Format($"发给 {name}") : batch.Action.EntryTitle();

    /// <summary>
    /// Whether every whitespace-separated search term hits something the row can
    /// show: title, subtitle and the batch's own metadata together.
    /// </summary>
    public static bool Matches(ReadyBatch batch, string query)
    {
        var terms = NormalizeSearch(query)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
            return true;

        var searchable = new List<string?>
        {
            PaneTitle(batch),
            PaneSubtitle(batch),
            batch.ChatName,
            batch.SceneName,
            batch.TargetName,
            batch.Action.EntryTitle(),
            batch.Action.TargetDisplayName(),
        };
        searchable.AddRange(batch.Items.Select(item => (string?)item.DisplayName));
        var haystack = NormalizeSearch(string.Join('\n', searchable.Where(s => s is not null)));
        return terms.All(haystack.Contains);
    }

    /// <summary>The heading above one day's records: 今天 / 昨天 / 9月4日 / 2025年9月4日.</summary>
    public static string SectionTitle(DateTime day, DateTime? now = null)
    {
        var local = day.Date;
        var today = (now ?? DateTime.Now).Date;
        if (local == today)
            return L10n.Text("今天");
        if (local == today.AddDays(-1))
            return L10n.Text("昨天");
        return local.Year == today.Year
            ? local.ToString(L10n.Text("M月d日"), L10n.Culture)
            : local.ToString(L10n.Text("yyyy年M月d日"), L10n.Culture);
    }

    /// <summary>The clock alone, for a row already sitting under a day heading.</summary>
    public static string ClockTime(DateTimeOffset time) =>
        time.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Counted in characters: one Chinese character is one glyph however many bytes it takes.</summary>
    public static string MiddleTruncate(string text, int limit)
    {
        if (limit <= 1 || text.Length <= limit)
            return text;
        var keep = limit - 1;
        var tail = keep / 2;
        return $"{text[..(keep - tail)]}…{text[^tail..]}";
    }

    private static string FileCount(ReadyBatch batch) =>
        batch.Items.Count == 1 ? L10n.Text("1 个文件") : L10n.Format($"{batch.Items.Count} 个文件");

    private static string? Clean(string? value)
    {
        if (value is null)
            return null;
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// Case/diacritic-insensitive folding — the same normalisation the Core
    /// <see cref="GroupName"/> applies to group titles.
    /// </summary>
    private static string NormalizeSearch(string value) => GroupName.Normalize(value);
}
