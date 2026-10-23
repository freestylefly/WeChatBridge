using WeChatBridge.Windows.Core;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// What the user asked for when they picked an entry in the share menu.
/// Ported from <c>WeChatBridgeCore/ShareAction.swift</c>: the JSON raw values are
/// identical on both platforms because they are written into manifest.json and
/// intent.json, which either side may have produced.
///
/// On Windows each entry is one <c>&lt;Application&gt;</c> in the sparse package
/// manifest (verified 2026-09-25: WeChat enumerates every shareTarget entry of a
/// multi-application package). The helper tells entries apart by its own
/// Application Id, the <see cref="ShareEntryId"/> suffix.
/// </summary>
[JsonConverter(typeof(ShareActionConverter))]
public enum ShareAction
{
    Codex,
    Claude,
    Doubao,
    Qwen,
    WorkBuddy,
    WeSight,
    DeepSeekHarness,
    Obsidian,
    Folder,
    Collect,
    Clipboard,
    Custom,
    /// <summary>
    /// The single entry Windows exposes to WeChat: 「微信流」. Picking it does
    /// not name a destination — the app asks through its own entry picker and
    /// then forwards as the chosen action, which is why every per-app
    /// Application was folded into this one.
    /// </summary>
    Hub,
}

public static class ShareActions
{
    public static bool WritesClipboardOnReceipt(ShareAction entry, BatchIntent? intent)
        => (intent?.Action ?? entry) != ShareAction.Collect;
    /// <summary>The string both platforms write into JSON.</summary>
    public static string RawValue(this ShareAction action) => action switch
    {
        ShareAction.Codex => "codex",
        ShareAction.Claude => "claude",
        ShareAction.Doubao => "doubao",
        ShareAction.Qwen => "qwen",
        ShareAction.WorkBuddy => "workBuddy",
        ShareAction.WeSight => "weSight",
        ShareAction.DeepSeekHarness => "deepSeekHarness",
        ShareAction.Folder => "folder",
        ShareAction.Collect => "collect",
        ShareAction.Obsidian => "obsidian",
        ShareAction.Clipboard => "clipboard",
        ShareAction.Custom => "custom",
        ShareAction.Hub => "hub",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public static ShareAction? FromRawValue(string raw) => raw switch
    {
        "codex" => ShareAction.Codex,
        "claude" => ShareAction.Claude,
        "doubao" => ShareAction.Doubao,
        "qwen" => ShareAction.Qwen,
        "workBuddy" => ShareAction.WorkBuddy,
        "weSight" => ShareAction.WeSight,
        "deepSeekHarness" => ShareAction.DeepSeekHarness,
        "folder" => ShareAction.Folder,
        "collect" => ShareAction.Collect,
        "obsidian" => ShareAction.Obsidian,
        "clipboard" => ShareAction.Clipboard,
        // macOS compatibility: the retired shelf entry decodes as clipboard.
        "shelf" => ShareAction.Clipboard,
        "custom" => ShareAction.Custom,
        "hub" => ShareAction.Hub,
        _ => null,
    };

    /// <summary>
    /// The suffix of the sparse-package <c>&lt;Application Id&gt;</c> carrying this
    /// entry, e.g. <c>Share.Codex</c>. The full application user model id is
    /// <c>{PackageFamilyName}!{ShareEntryId}</c>; the helper resolves which entry
    /// was invoked from its own AUMID.
    /// </summary>
    public static string ShareEntryId(this ShareAction action) => action switch
    {
        ShareAction.Codex => "Share.Codex",
        ShareAction.Claude => "Share.Claude",
        ShareAction.Doubao => "Share.Doubao",
        ShareAction.Qwen => "Share.Qwen",
        ShareAction.WorkBuddy => "Share.WorkBuddy",
        ShareAction.WeSight => "Share.WeSight",
        ShareAction.DeepSeekHarness => "Share.DeepSeekHarness",
        ShareAction.Folder => "Share.Folder",
        ShareAction.Collect => "Share.Collection",
        ShareAction.Obsidian => "Share.Obsidian",
        ShareAction.Clipboard => "Share.Clipboard",
        ShareAction.Custom => "Share.Custom",
        ShareAction.Hub => "Share.Hub",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>
    /// Whether the app still has something to do once the files are durable.
    /// 复制到剪贴板 is finished by the helper itself; a late-discovered intent
    /// could only mislead the history.
    /// </summary>
    public static bool NeedsIntent(this ShareAction action) => action != ShareAction.Clipboard;

    /// <summary>Names the destination so failures can say what did not happen.</summary>
    public static string TargetDisplayName(this ShareAction action) => action switch
    {
        ShareAction.Codex => "Codex",
        ShareAction.Claude => "Claude",
        ShareAction.Doubao => L10n.Text("豆包"),
        ShareAction.Qwen => L10n.Text("千问"),
        ShareAction.WorkBuddy => "WorkBuddy",
        ShareAction.WeSight => "WeSight",
        ShareAction.DeepSeekHarness => "DeepSeek Harness",
        ShareAction.Folder => L10n.Text("文件夹"),
        ShareAction.Collect => L10n.Text("微信流收集"),
        ShareAction.Obsidian => "Obsidian",
        ShareAction.Clipboard => L10n.Text("剪贴板"),
        ShareAction.Custom => L10n.Text("所选应用"),
        ShareAction.Hub => "微信流",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>The entry as worded in the share menu — history reuses these words.</summary>
    public static string EntryTitle(this ShareAction action) => action switch
    {
        ShareAction.Codex => L10n.Text("发给 Codex"),
        ShareAction.Claude => L10n.Text("发给 Claude"),
        ShareAction.Doubao => L10n.Text("发给豆包"),
        ShareAction.Qwen => L10n.Text("发给千问"),
        ShareAction.WorkBuddy => L10n.Text("发给 WorkBuddy"),
        ShareAction.WeSight => L10n.Text("发给 WeSight"),
        ShareAction.DeepSeekHarness => L10n.Text("发给 DeepSeek Harness"),
        ShareAction.Folder => L10n.Text("沉淀到文件夹"),
        ShareAction.Collect => L10n.Text("分批收集到微信流"),
        ShareAction.Obsidian => L10n.Text("沉淀到 Obsidian"),
        ShareAction.Clipboard => L10n.Text("复制到剪贴板"),
        ShareAction.Custom => L10n.Text("发送到自定义"),
        ShareAction.Hub => "微信流",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>Every destination the 入口 pane offers — Hub excluded on purpose: it is the share-sheet façade, not something the user enables.</summary>
    public static IReadOnlyList<ShareAction> All { get; } =
        Enum.GetValues<ShareAction>().Where(a => a != ShareAction.Hub).ToArray();
}

public sealed class ShareActionConverter : JsonConverter<ShareAction>
{
    public override ShareAction Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.GetString();
        return raw is not null && ShareActions.FromRawValue(raw) is { } action
            ? action
            : throw new JsonException($"Unknown share action: {raw}");
    }

    public override void Write(Utf8JsonWriter writer, ShareAction value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.RawValue());
}

/// <summary>
/// A one-shot request attached to a committed batch (intent.json). Consumed the
/// moment the app acts so a rescan or relaunch can never replay a forward.
/// Ported from <c>BatchIntent</c> in ShareAction.swift, including the 90-second
/// freshness window and the flat target fields.
/// </summary>
public sealed record BatchIntent
{
    public const string FileName = "intent.json";
    public const int CurrentSchemaVersion = 1;
    public static readonly TimeSpan FreshnessWindow = TimeSpan.FromSeconds(90);

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public ShareAction Action { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    /// <summary>Which target 「发送到自定义」 was pointed at; null for every other entry.</summary>
    public string? TargetBundleIdentifier { get; init; }
    public string? TargetDisplayName { get; init; }

    public bool IsFresh(DateTimeOffset? now = null) =>
        (now ?? DateTimeOffset.UtcNow) - RequestedAt < FreshnessWindow;
}
