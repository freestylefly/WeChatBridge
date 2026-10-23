using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// What became of a batch, as far as the user is concerned. One record per batch,
/// overwritten as the story moves on; <see cref="Detail"/> says why a failure did
/// not arrive. Ported from <c>BatchOutcome</c> in BatchState.swift.
/// </summary>
public sealed record BatchOutcome(BatchOutcomeKind Kind, string? Detail, DateTimeOffset At);

[JsonConverter(typeof(BatchOutcomeKindConverter))]
public enum BatchOutcomeKind
{
    /// <summary>Activated the target app and pasted for the user.</summary>
    Delivered,
    /// <summary>Written to the clipboard and nothing else.</summary>
    Copied,
    /// <summary>A forward that did not happen; the files are on the clipboard.</summary>
    Failed,
    /// <summary>Found too late to carry out; nothing was done.</summary>
    Expired,
}

public static class BatchOutcomeKinds
{
    public static string RawValue(this BatchOutcomeKind kind) => kind switch
    {
        BatchOutcomeKind.Delivered => "delivered",
        BatchOutcomeKind.Copied => "copied",
        BatchOutcomeKind.Failed => "failed",
        BatchOutcomeKind.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

public sealed class BatchOutcomeKindConverter : JsonConverter<BatchOutcomeKind>
{
    public override BatchOutcomeKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.GetString() switch
        {
            "delivered" => BatchOutcomeKind.Delivered,
            "copied" => BatchOutcomeKind.Copied,
            "failed" => BatchOutcomeKind.Failed,
            // macOS compatibility: the retired shelf outcome reads as expired.
            "expired" or "shelved" => BatchOutcomeKind.Expired,
            var raw => throw new JsonException($"Unknown batch outcome: {raw}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, BatchOutcomeKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.RawValue());
}

/// <summary>
/// The app's own notes about a batch: <c>Ready/&lt;batch-id&gt;/state.json</c>.
/// Written by the app — or by the share helper inside the staging tree, where it
/// commits atomically with the batch. Never written into Ready by the helper:
/// the split is what lets the app record an outcome without racing a commit.
/// Ported from <c>BatchState</c> in BatchState.swift.
/// </summary>
public sealed record BatchState
{
    public const string FileName = "state.json";
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    /// <summary>Null only while a forward has been noticed but not yet carried out.</summary>
    public BatchOutcome? Outcome { get; init; }
    /// <summary>Which share-menu entry produced this batch (outlives intent.json).</summary>
    public ShareAction? Action { get; init; }
    /// <summary>The app a 发送到自定义 batch was pointed at, as named on screen.</summary>
    public string? TargetName { get; init; }
    /// <summary>Snapshot of the group title recognised when the batch arrived.</summary>
    public string? ChatName { get; init; }
    /// <summary>Scene id + name snapshot; renaming a scene must not rewrite history.</summary>
    public string? SceneID { get; init; }
    public string? SceneName { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UnannouncedShare { get; init; }

    /// <summary>
    /// The state a batch gets the first time the app sees it. A clipboard batch is
    /// already done — the helper wrote the clipboard before exiting — so it
    /// stamps <see cref="BatchOutcomeKind.Copied"/> at the batch's own creation
    /// time rather than leaving a "request that never ran".
    /// </summary>
    public static BatchState Initial(
        BatchManifest manifest,
        ShareAction? requestedAction = null,
        string? targetName = null)
    {
        var action = requestedAction
            ?? ShareActions.FromRawValue(manifest.Action)
            ?? ShareAction.Clipboard;
        return new BatchState
        {
            Outcome = action == ShareAction.Clipboard
                ? new BatchOutcome(BatchOutcomeKind.Copied, null, manifest.CreatedAt)
                : null,
            Action = action,
            TargetName = targetName,
        };
    }

    /// <summary>
    /// <paramref name="targetName"/> defaults to "leave it alone": most callers
    /// have no opinion about the destination and must not erase a custom name.
    /// </summary>
    public BatchState WithOutcome(BatchOutcome? outcome, string? targetName = null) =>
        this with { Outcome = outcome, TargetName = targetName ?? TargetName };

    /// <summary>Writes scene/group metadata without clearing existing values.</summary>
    public BatchState WithContext(string? chatName = null, string? sceneId = null, string? sceneName = null) =>
        this with
        {
            ChatName = chatName ?? ChatName,
            SceneID = sceneId ?? SceneID,
            SceneName = sceneName ?? SceneName,
        };
}
