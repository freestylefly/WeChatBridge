namespace WeChatBridge.Windows.Core;

/// <summary>Persistent collections, frozen delivery snapshots, and recoverable batch removal.</summary>
public sealed class CollectionService
{
    private readonly InboxPaths _paths;
    private readonly InboxReader _reader;
    private string LedgerPath => Path.Combine(_paths.Root, "collections.json");
    private string UndoPath => Path.Combine(_paths.Root, "collection-undo.json");
    private string UndoDirectory => Path.Combine(_paths.Root, "CollectionUndo");
    public BatchCollectionLedger Ledger { get; private set; }
    public CollectionService(InboxPaths paths, InboxReader reader)
    {
        _paths = paths;
        _reader = reader;
        Ledger = BatchCollectionLedger.Load(LedgerPath);
        RecoverInterruptedRemoval();
        RecoverUndoJournal();
        Ledger.RecoverInterruptedDeliveries();
        Save();
    }

    public void Save() => Ledger.Save(LedgerPath);
    public Guid Append(ReadyBatch batch, string? recognizedName)
    {
        var id = Ledger.Append(batch.Id, batch.CreatedAt);
        var group = Ledger.Collections.FirstOrDefault(c => c.Id == id);
        if (group is null) return id; // A removed, already-seen batch must never be collected again.
        var name = batch.ChatName ?? recognizedName ?? group.DefaultChatName;
        if (!string.IsNullOrWhiteSpace(name)) _reader.RecordContext(batch.Id, name);
        Save();
        return id;
    }

    public void SetChatName(Guid id, Guid batchId, string name, bool useAsDefault)
    {
        var group = Ledger.Editable(id);
        if (!group.BatchIDs.Contains(batchId)) throw new InvalidOperationException(L10n.Text("批次不属于这组收集。"));
        _reader.RecordChatName(batchId, string.IsNullOrWhiteSpace(name) ? null : name.Trim());
        if (useAsDefault)
        {
            Ledger.SetDefaultChatName(id, name);
            foreach (var member in group.BatchIDs.Where(b => string.IsNullOrWhiteSpace(_reader.StateFor(b)?.ChatName)))
                _reader.RecordContext(member, name.Trim());
        }
        Save();
    }

    public List<ReadyBatch> Freeze(Guid id, bool requireChatNames = true)
    {
        var group = Ledger.Editable(id);
        var batches = _reader.LoadBatches().ToDictionary(b => b.Id);
        if (group.BatchIDs.Count == 0 || group.BatchIDs.Any(b => !batches.ContainsKey(b)))
            throw new InvalidOperationException(L10n.Text("部分收集文件已不存在，请查看批次。"));
        if (requireChatNames && group.BatchIDs.Any(b => string.IsNullOrWhiteSpace(batches[b].ChatName)))
            throw new InvalidOperationException(L10n.Text("请先填写每批的群或联系人名称。"));
        var previousStatus = group.Status;
        var ids = Ledger.BeginDelivery(id);
        try { Save(); } // Freeze durably before any clipboard write.
        catch { group.Status = previousStatus; throw; }
        return ids.Select(b => batches[b]).ToList();
    }

    public void Finish(Guid id, bool succeeded, string target, string detail, string? scene = null)
    {
        Ledger.FinishDelivery(id, succeeded, target, detail, scene);
        Save();
    }

    public sealed record RemovedBatch(Guid CollectionId, Guid BatchId, int Position, string StoredDirectory,
        bool Detached = false, CollectionStatus PreviousStatus = CollectionStatus.Draft, Guid OperationId = default, bool BackupComplete = false);

    private List<(string File, RemovedBatch Record)> UndoRecords()
    {
        var result = new List<(string, RemovedBatch)>();
        if (File.Exists(UndoPath) && ConfigStore.Load<RemovedBatch>(_paths.Root, Path.GetFileName(UndoPath)) is { } old)
            result.Add((UndoPath, old));
        if (Directory.Exists(UndoDirectory))
            foreach (var file in Directory.GetFiles(UndoDirectory, "*.json").Order(StringComparer.Ordinal))
                if (ConfigStore.Load<RemovedBatch>(UndoDirectory, Path.GetFileName(file)) is { } item)
                    result.Add((file, item));
        return result;
    }

    private string ValidateStored(RemovedBatch record)
    {
        var expected = Path.Combine(_paths.Root, "RemovedCollectionBatches", record.BatchId.ToString("D"));
        if (!string.Equals(Path.GetFullPath(expected), Path.GetFullPath(record.StoredDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L10n.Text("撤销记录无效。"));
        return expected;
    }

    private void RecoverUndoJournal()
    {
        foreach (var (file, record) in UndoRecords().Where(r => r.File != UndoPath))
        {
            var stored = ValidateStored(record);
            var original = Path.Combine(_paths.Ready, record.BatchId.ToString("D"));
            if (record.Detached)
            {
                if (Ledger.Collections.Any(c => c.BatchIDs.Contains(record.BatchId))) File.Delete(file);
                continue;
            }
            if (Ledger.Collections.Any(c => c.BatchIDs.Contains(record.BatchId)))
            {
                // A removal that never committed leaves the original membership intact.
                if (Directory.Exists(stored))
                {
                    if (!Directory.Exists(original)) Directory.Move(stored, original);
                    else
                    {
                        if (record.BackupComplete) CopyDirectory(stored, original, overwrite: true);
                        Directory.Delete(stored, recursive: true);
                    }
                }
                File.Delete(file);
            }
        }
    }

    private void RecoverInterruptedRemoval()
    {
        var undo = ConfigStore.Load<RemovedBatch>(_paths.Root, Path.GetFileName(UndoPath));
        if (undo is null) return;
        var expected = Path.Combine(_paths.Root, "RemovedCollectionBatches", undo.BatchId.ToString("D"));
        if (!string.Equals(Path.GetFullPath(expected), Path.GetFullPath(undo.StoredDirectory), StringComparison.OrdinalIgnoreCase)) return;
        var original = Path.Combine(_paths.Ready, undo.BatchId.ToString("D"));
        var member = Ledger.Collections.Any(c => c.BatchIDs.Contains(undo.BatchId));
        // A crash between the filesystem move and ledger commit must leave an accessible batch.
        if (member && Directory.Exists(expected) && !Directory.Exists(original)) Directory.Move(expected, original);
        else if (!member && Directory.Exists(original) && !Directory.Exists(expected))
            Ledger.Restore(undo.CollectionId, undo.BatchId, undo.Position);
    }

    public void Remove(Guid id, Guid batchId)
        => RemoveCore(id, batchId, detached: false, Guid.NewGuid());

    public void Detach(Guid id, Guid batchId)
        => RemoveCore(id, batchId, detached: true, Guid.NewGuid());

    public void DeleteCollection(Guid id)
    {
        var members = Ledger.Editable(id).BatchIDs.ToList();
        var operation = Guid.NewGuid();
        var completed = 0;
        try
        {
            foreach (var member in members) { RemoveCore(id, member, false, operation); completed++; }
        }
        catch
        {
            if (completed > 0) Undo();
            throw;
        }
    }

    private void RemoveCore(Guid id, Guid batchId, bool detached, Guid operation)
    {
        var group = Ledger.Editable(id);
        var position = group.BatchIDs.IndexOf(batchId);
        if (position < 0) throw new InvalidOperationException(L10n.Text("批次不属于这组收集。"));
        var original = Path.Combine(_paths.Ready, batchId.ToString("D"));
        var removedRoot = Path.Combine(_paths.Root, "RemovedCollectionBatches");
        Directory.CreateDirectory(removedRoot);
        var stored = Path.Combine(removedRoot, batchId.ToString("D"));
        var undo = new RemovedBatch(id, batchId, position, stored, detached, group.Status, operation);
        Directory.CreateDirectory(UndoDirectory);
        var journal = Path.Combine(UndoDirectory, $"{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}.json");
        if (!detached)
        {
            if (Directory.Exists(stored)) throw new IOException(L10n.Text("此批次已有未恢复的备份。"));
        }
        try
        {
            ConfigStore.Save(UndoDirectory, Path.GetFileName(journal), undo);
            if (!detached)
            {
                // Keep the journal before copying so an interrupted backup can be cleaned safely.
                CopyDirectory(original, stored);
                undo = undo with { BackupComplete = true };
                ConfigStore.Save(UndoDirectory, Path.GetFileName(journal), undo);
                _reader.RecycleCollectionBatch(batchId);
            }
            Ledger.Remove(id, batchId);
            Save();
        }
        catch
        {
            if (!detached && Directory.Exists(stored))
            {
                if (!Directory.Exists(original)) Directory.Move(stored, original);
                else
                {
                    if (undo.BackupComplete) CopyDirectory(stored, original, overwrite: true);
                    Directory.Delete(stored, recursive: true);
                }
            }
            if (!group.BatchIDs.Contains(batchId)) Ledger.Restore(id, batchId, position);
            group.Status = undo.PreviousStatus;
            Save();
            File.Delete(journal);
            throw;
        }
    }

    private static void CopyDirectory(string source, string destination, bool overwrite = false)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            if (overwrite && File.Exists(target) && SameFile(file, target)) continue;
            File.Copy(file, target, overwrite);
        }
        foreach (var directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), overwrite);
    }

    private static bool SameFile(string first, string second)
    {
        if (new FileInfo(first).Length != new FileInfo(second).Length) return false;
        using var a = File.OpenRead(first);
        using var b = File.OpenRead(second);
        return System.Security.Cryptography.SHA256.HashData(a).SequenceEqual(System.Security.Cryptography.SHA256.HashData(b));
    }

    private bool Recoverable(RemovedBatch record) => Directory.Exists(record.Detached
        ? Path.Combine(_paths.Ready, record.BatchId.ToString("D")) : ValidateStored(record));

    public bool CanUndo => UndoRecords().LastOrDefault() is { Record: { } record } && Recoverable(record);

    public void Undo()
    {
        var records = UndoRecords();
        if (records.Count == 0) throw new InvalidOperationException(L10n.Text("没有可以撤销的删除。"));
        var operation = records[^1].Record.OperationId;
        var restore = records.AsEnumerable().Reverse().TakeWhile(r => operation != Guid.Empty
                     ? r.Record.OperationId == operation : r.File == records[^1].File).ToList();
        foreach (var entry in restore)
        {
            Ledger.Editable(entry.Record.CollectionId);
            if (!Recoverable(entry.Record)) throw new IOException(L10n.Text("撤销文件已不存在，请从系统回收站恢复。"));
        }
        foreach (var entry in restore)
            UndoOne(entry.File, entry.Record);
    }

    private void UndoOne(string journal, RemovedBatch undo)
    {
        var expected = ValidateStored(undo);
        var group = Ledger.Editable(undo.CollectionId);
        var previousStatus = group.Status;
        var destination = Path.Combine(_paths.Ready, undo.BatchId.ToString("D"));
        if (!undo.Detached) Directory.Move(expected, destination);
        try
        {
            Ledger.Restore(undo.CollectionId, undo.BatchId, undo.Position);
            group.Status = undo.PreviousStatus == CollectionStatus.Collecting
                && Ledger.Current is { } current && current.Id != group.Id ? CollectionStatus.Draft : undo.PreviousStatus;
            Save();
        }
        catch
        {
            group.BatchIDs.Remove(undo.BatchId);
            group.Status = previousStatus;
            if (!undo.Detached) Directory.Move(destination, expected);
            throw;
        }
        File.Delete(journal);
    }
}
