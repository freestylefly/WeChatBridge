using System.Text.Json;
using VisualBasicFileIO = Microsoft.VisualBasic.FileIO;

namespace WeChatBridge.Windows.Core;

public sealed record ReadyItem(
    Guid Id,
    Guid BatchId,
    string DisplayName,
    string FullPath,
    long ByteCount,
    string? ContentType,
    DateTimeOffset CreatedAt,
    ShareAction Action);

public sealed record ReadyBatch(
    Guid Id,
    string Directory,
    DateTimeOffset CreatedAt,
    ShareAction Action,
    IReadOnlyList<ReadyItem> Items,
    BatchOutcome? Outcome,
    string? TargetName,
    string? ChatName,
    string? SceneID,
    string? SceneName,
    /// <summary>
    /// True only when this load wrote state.json — the durable signal that no run
    /// of the app has ever processed this batch. Merely new to this process is not.
    /// </summary>
    bool IsFirstSeen)
{
    public long ByteCount => Items.Sum(i => i.ByteCount);
}

/// <summary>A share the helper could not complete, left on disk for the app to say out loud.</summary>
public sealed record ShareFailure(DateTimeOffset At, ShareAction Action, string Message)
{
    /// <summary>Best effort: a failed share must not fail a second time over its own report.</summary>
    public static void Record(ShareFailure failure, InboxPaths paths)
    {
        try
        {
            var directory = Path.Combine(paths.Root, "Failures");
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, $"{Guid.NewGuid():D}.json"),
                JsonSerializer.Serialize(failure, BatchManifest.JsonOptions));
        }
        catch
        {
        }
    }
}

/// <summary>What consuming a batch's intent.json found.</summary>
public sealed record ConsumedIntent(ConsumedIntentKind Kind, BatchIntent? Intent)
{
    public static readonly ConsumedIntent None = new(ConsumedIntentKind.None, null);
    public static ConsumedIntent Ready(BatchIntent intent) => new(ConsumedIntentKind.Ready, intent);
    public static ConsumedIntent Expired(BatchIntent intent) => new(ConsumedIntentKind.Expired, intent);
}

public enum ConsumedIntentKind { None, Ready, Expired }

/// <summary>
/// The app's half of the inbox protocol: read Ready, never touch Staging.
/// Ported from InboxReader.swift. The app is the only process that writes into
/// Ready after the helper's commit rename, so state writes need no coordination.
/// </summary>
public sealed class InboxReader
{
    /// <summary>How a removed batch leaves the container.</summary>
    public enum Removal
    {
        /// <summary>Product behaviour: recoverable from the Recycle Bin.</summary>
        Trash,
        /// <summary>Tests use this; fixtures must not enter the user's bin.</summary>
        Delete,
    }

    private readonly InboxPaths _paths;
    private readonly Removal _removal;

    public InboxReader(InboxPaths? paths = null, Removal removal = Removal.Trash)
    {
        _paths = paths ?? new InboxPaths();
        _removal = removal;
    }

    /// <summary>
    /// Newest first. A batch whose manifest is missing, unreadable or written by a
    /// newer schema is skipped rather than partially shown. Reading is also what
    /// initialises a batch: one without state.json gets it written here.
    /// </summary>
    public List<ReadyBatch> LoadBatches() => Batches(initializing: true);

    public ReadyBatch? BatchAt(string directory) => Batch(directory, initializing: true);

    private List<ReadyBatch> Batches(bool initializing)
    {
        if (!Directory.Exists(_paths.Ready))
            return [];
        return Directory.EnumerateDirectories(_paths.Ready)
            .Select(d => Batch(d, initializing))
            .Where(b => b is not null)
            .Cast<ReadyBatch>()
            .OrderByDescending(b => b.CreatedAt)
            .ToList();
    }

    private ReadyBatch? Batch(string directory, bool initializing)
    {
        var manifest = ManifestAt(directory);
        if (manifest is null)
            return null;

        var stored = StateAt(directory);
        // Batches written before manifests carried action recover the request
        // from intent.json, still on disk at this point.
        var requested = stored is null ? PeekIntent(directory) : null;
        var state = stored ?? BatchState.Initial(manifest, requested?.Action, requested?.TargetDisplayName);
        var firstSeen = stored is null || state.UnannouncedShare;
        if (initializing && firstSeen)
        {
            state = state with { UnannouncedShare = false };
            TryWriteState(directory, state);
        }

        var action = state.Action
            ?? ShareActions.FromRawValue(manifest.Action)
            ?? ShareAction.Clipboard;
        var items = manifest.Items
            .Select(item => (item, path: Path.Combine(directory, item.RelativePath.Replace('/', Path.DirectorySeparatorChar))))
            .Where(x => File.Exists(x.path))
            .Select(x => new ReadyItem(
                x.item.Id,
                manifest.BatchId,
                x.item.DisplayName,
                x.path,
                x.item.ByteCount,
                x.item.ContentType,
                manifest.CreatedAt,
                action))
            .ToList();
        if (items.Count == 0)
            return null;

        return new ReadyBatch(
            manifest.BatchId,
            directory,
            manifest.CreatedAt,
            action,
            items,
            state.Outcome,
            state.TargetName,
            state.ChatName,
            state.SceneID,
            state.SceneName,
            firstSeen && initializing);
    }

    /// <summary>Bytes of payload on disk — what settings reports as 占用.</summary>
    public long TotalByteCount() => Batches(initializing: false).Sum(b => b.ByteCount);

    public void RecordOutcome(BatchOutcome outcome, Guid batchId, string? targetName = null) =>
        MutateState(batchId, s => s.WithOutcome(outcome, targetName));

    public void RecordContext(Guid batchId, string? chatName = null, string? sceneId = null, string? sceneName = null) =>
        MutateState(batchId, s => s.WithContext(chatName, sceneId, sceneName));

    public void ClearSceneContext(Guid batchId) =>
        MutateState(batchId, state => state with { SceneID = null, SceneName = null });

    public void RecordChatName(Guid batchId, string? chatName) =>
        MutateState(batchId, state => state with { ChatName = chatName });

    public void RecordAction(Guid batchId, ShareAction action) =>
        MutateState(batchId, state => state with { Action = action });

    public BatchState? StateFor(Guid batchId) => StateAt(DirectoryFor(batchId));

    /// <summary>Moves a whole batch to the Recycle Bin — the archive may be the only copy.</summary>
    public void Discard(Guid batchId) => TrashOrRemove(DirectoryFor(batchId));

    /// <summary>Collection deletion must fail safely on volumes without a recycle bin.</summary>
    public void RecycleCollectionBatch(Guid batchId)
    {
        var path = DirectoryFor(batchId);
        if (_removal == Removal.Delete) { Directory.Delete(path, recursive: true); return; }
        VisualBasicFileIO.FileSystem.DeleteDirectory(path, VisualBasicFileIO.UIOption.OnlyErrorDialogs,
            VisualBasicFileIO.RecycleOption.SendToRecycleBin, VisualBasicFileIO.UICancelOption.ThrowException);
    }

    /// <summary>Removes one item and rewrites the manifest; the batch goes once its last item does.</summary>
    public void Discard(ReadyItem item)
    {
        var directory = DirectoryFor(item.BatchId);
        var manifest = ManifestAt(directory)
            ?? throw new InboxValidationException("manifest 无法读取。");
        var remaining = manifest.Items.Where(i => i.Id != item.Id).ToList();
        if (remaining.Count == 0)
        {
            TrashOrRemove(directory);
            return;
        }
        TrashOrRemove(item.FullPath);
        var updated = manifest with { Items = remaining };
        File.WriteAllText(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(updated, BatchManifest.JsonOptions));
    }

    public void DiscardAll()
    {
        foreach (var batch in Batches(initializing: false))
            Discard(batch.Id);
    }

    /// <summary>Ages out finished history. A non-positive window means keep forever.</summary>
    public int PruneHistory(TimeSpan olderThan, DateTimeOffset? now = null, IReadOnlySet<Guid>? protectedBatchIDs = null)
    {
        if (olderThan <= TimeSpan.Zero)
            return 0;
        var reference = now ?? DateTimeOffset.UtcNow;
        var removed = 0;
        foreach (var batch in Batches(initializing: false))
        {
            if (protectedBatchIDs?.Contains(batch.Id) == true || reference - batch.CreatedAt <= olderThan)
                continue;
            try { Discard(batch.Id); removed++; } catch { }
        }
        return removed + PruneUnreadable(olderThan, reference, protectedBatchIDs);
    }

    /// <summary>
    /// Debris no ReadyBatch can be built from — crash-truncated manifests or batches
    /// whose files were deleted by hand. A manifest with a newer schema is left alone.
    /// </summary>
    private int PruneUnreadable(TimeSpan olderThan, DateTimeOffset now, IReadOnlySet<Guid>? protectedBatchIDs)
    {
        if (!Directory.Exists(_paths.Ready))
            return 0;
        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(_paths.Ready))
        {
            if ((Guid.TryParse(Path.GetFileName(directory), out var protectedId) && protectedBatchIDs?.Contains(protectedId) == true)
                || Batch(directory, initializing: false) is not null)
                continue;
            var manifest = ManifestAt(directory, allowNewerSchema: true);
            if (manifest is { SchemaVersion: > BatchManifest.CurrentSchemaVersion })
                continue;
            var created = manifest?.CreatedAt
                ?? new DateTimeOffset(File.GetCreationTimeUtc(directory), TimeSpan.Zero);
            if (now - created <= olderThan)
                continue;
            try { TrashOrRemove(directory); removed++; } catch { }
        }
        return removed;
    }

    /// <summary>Every message the helper left behind, oldest first, read and deleted in one gesture.</summary>
    public List<ShareFailure> ConsumeFailures()
    {
        var directory = Path.Combine(_paths.Root, "Failures");
        if (!Directory.Exists(directory))
            return [];
        var failures = new List<ShareFailure>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            string? text = null;
            try { text = File.ReadAllText(file); } catch { }
            try { File.Delete(file); } catch { }
            if (text is null)
                continue;
            try
            {
                if (JsonSerializer.Deserialize<ShareFailure>(text, BatchManifest.JsonOptions) is { } failure)
                    failures.Add(failure);
            }
            catch
            {
                // Unreadable reports are deleted too — a stale byte sequence would
                // otherwise be re-examined on every scan.
            }
        }
        return failures.OrderBy(f => f.At).ToList();
    }

    /// <summary>
    /// Reads and immediately consumes the batch's one-shot request. Consumption is
    /// the deletion: a rescan, relaunch or second window can never replay a forward.
    /// </summary>
    public ConsumedIntent ConsumeIntent(Guid batchId, DateTimeOffset? now = null)
    {
        var path = Path.Combine(DirectoryFor(batchId), BatchIntent.FileName);
        if (!File.Exists(path))
            return ConsumedIntent.None;
        string text;
        try
        {
            text = File.ReadAllText(path);
            File.Delete(path);
        }
        catch
        {
            return ConsumedIntent.None;
        }
        BatchIntent? intent = null;
        try { intent = JsonSerializer.Deserialize<BatchIntent>(text, BatchManifest.JsonOptions); }
        catch { }
        if (intent is null || intent.SchemaVersion > BatchIntent.CurrentSchemaVersion)
            return ConsumedIntent.None;
        return intent.IsFresh(now) ? ConsumedIntent.Ready(intent) : ConsumedIntent.Expired(intent);
    }

    /// <summary>The request read without deleting it — used by the first batch load.</summary>
    private BatchIntent? PeekIntent(string directory)
    {
        var path = Path.Combine(directory, BatchIntent.FileName);
        try
        {
            if (!File.Exists(path))
                return null;
            var intent = JsonSerializer.Deserialize<BatchIntent>(File.ReadAllText(path), BatchManifest.JsonOptions);
            return intent is { SchemaVersion: <= BatchIntent.CurrentSchemaVersion } ? intent : null;
        }
        catch
        {
            return null;
        }
    }

    private string DirectoryFor(Guid batchId) => Path.Combine(_paths.Ready, batchId.ToString("D"));

    private BatchManifest? ManifestAt(string directory, bool allowNewerSchema = false)
    {
        try
        {
            var path = Path.Combine(directory, "manifest.json");
            if (!File.Exists(path))
                return null;
            var manifest = JsonSerializer.Deserialize<BatchManifest>(File.ReadAllText(path), BatchManifest.JsonOptions);
            if (manifest is null || (!allowNewerSchema && manifest.SchemaVersion > BatchManifest.CurrentSchemaVersion))
                return null;
            return manifest;
        }
        catch
        {
            return null;
        }
    }

    private BatchState? StateAt(string directory)
    {
        try
        {
            var path = Path.Combine(directory, BatchState.FileName);
            if (!File.Exists(path))
                return null;
            var state = JsonSerializer.Deserialize<BatchState>(File.ReadAllText(path), BatchManifest.JsonOptions);
            return state is { SchemaVersion: <= BatchState.CurrentSchemaVersion } ? state : null;
        }
        catch
        {
            return null;
        }
    }

    private void TryWriteState(string directory, BatchState state)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(directory, BatchState.FileName),
                JsonSerializer.Serialize(state, BatchManifest.JsonOptions));
        }
        catch
        {
            // Best effort: a refused write re-derives the same state on next read.
        }
    }

    /// <summary>Read-modify-write against the file, not a held ReadyBatch, so updates cannot lose each other.</summary>
    private void MutateState(Guid batchId, Func<BatchState, BatchState> transform)
    {
        var directory = DirectoryFor(batchId);
        var manifest = ManifestAt(directory)
            ?? throw new InboxValidationException("manifest 无法读取。");
        var current = StateAt(directory) ?? BatchState.Initial(manifest);
        var updated = transform(current);
        File.WriteAllText(
            Path.Combine(directory, BatchState.FileName),
            JsonSerializer.Serialize(updated, BatchManifest.JsonOptions));
    }

    private void TrashOrRemove(string path)
    {
        if (_removal == Removal.Delete)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else
                File.Delete(path);
            return;
        }
        try
        {
            if (Directory.Exists(path))
                VisualBasicFileIO.FileSystem.DeleteDirectory(
                    path,
                    VisualBasicFileIO.UIOption.OnlyErrorDialogs,
                    VisualBasicFileIO.RecycleOption.SendToRecycleBin);
            else
                VisualBasicFileIO.FileSystem.DeleteFile(
                    path,
                    VisualBasicFileIO.UIOption.OnlyErrorDialogs,
                    VisualBasicFileIO.RecycleOption.SendToRecycleBin);
        }
        catch
        {
            // A volume without a bin must not leave the user unable to clear history.
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }
    }
}
