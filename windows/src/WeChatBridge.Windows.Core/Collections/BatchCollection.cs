using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatBridge.Windows.Core;

public enum CollectionStatus { Collecting, Draft, Delivering, Delivered, Retry }

public sealed record BatchCollection
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Name { get; set; } = "";
    public List<Guid> BatchIDs { get; set; } = [];
    public CollectionStatus Status { get; set; } = CollectionStatus.Collecting;
    public string? TargetName { get; set; }
    public string? Detail { get; set; }
    public string? SceneName { get; set; }
    public string? DefaultChatName { get; set; }
}

/// <summary>App-owned ledger; original ZIPs remain in Ready and are never combined or rewritten.</summary>
public sealed class BatchCollectionLedger
{
    public int SchemaVersion { get; init; } = 1;
    public List<BatchCollection> Collections { get; init; } = [];
    public HashSet<Guid> SeenBatchIDs { get; init; } = [];
    [JsonIgnore]
    public BatchCollection? Current => Collections.FirstOrDefault(c => c.Status == CollectionStatus.Collecting);
    [JsonIgnore]
    public HashSet<Guid> ProtectedBatchIDs => Collections.Where(c => c.Status != CollectionStatus.Delivered)
        .SelectMany(c => c.BatchIDs).ToHashSet();

    public Guid Append(Guid batchId, DateTimeOffset? at = null)
    {
        if (SeenBatchIDs.Contains(batchId))
            return Collections.FirstOrDefault(c => c.BatchIDs.Contains(batchId))?.Id ?? batchId;
        var collection = Current;
        if (collection is null)
        {
            collection = new BatchCollection { CreatedAt = at ?? DateTimeOffset.UtcNow };
            Collections.Add(collection);
        }
        collection.BatchIDs.Add(batchId);
        SeenBatchIDs.Add(batchId);
        return collection.Id;
    }

    public BatchCollection Editable(Guid id)
    {
        var collection = Collections.FirstOrDefault(c => c.Id == id)
            ?? throw new InvalidOperationException(L10n.Text("这组收集已不存在。"));
        if (collection.Status == CollectionStatus.Delivering)
            throw new InvalidOperationException(L10n.Text("正在交付，请稍后操作。"));
        return collection;
    }

    public void Resume(Guid id)
    {
        var collection = Editable(id);
        if (collection.BatchIDs.Count == 0) throw new InvalidOperationException(L10n.Text("这组收集没有文件。"));
        ParkCurrent();
        collection.Status = CollectionStatus.Collecting;
        collection.Detail = null;
    }

    public void ParkCurrent()
    {
        foreach (var collection in Collections.Where(c => c.Status == CollectionStatus.Collecting))
            collection.Status = CollectionStatus.Draft;
    }

    public void Rename(Guid id, string name) => Editable(id).Name = name.Trim()[..Math.Min(name.Trim().Length, 120)];

    public void SetDefaultChatName(Guid id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException(L10n.Text("请填写群或联系人名称。"));
        Editable(id).DefaultChatName = name.Trim();
    }

    public int Remove(Guid id, Guid batchId)
    {
        var collection = Editable(id);
        var position = collection.BatchIDs.IndexOf(batchId);
        if (position < 0) throw new InvalidOperationException(L10n.Text("批次不属于这组收集。"));
        collection.BatchIDs.RemoveAt(position);
        if (collection.BatchIDs.Count == 0) collection.Status = CollectionStatus.Draft;
        return position;
    }

    public void Restore(Guid id, Guid batchId, int position)
    {
        var collection = Editable(id);
        if (Collections.Any(c => c.BatchIDs.Contains(batchId))) throw new InvalidOperationException(L10n.Text("批次已在收集中。"));
        collection.BatchIDs.Insert(Math.Clamp(position, 0, collection.BatchIDs.Count), batchId);
    }

    public List<Guid> BeginDelivery(Guid id)
    {
        var collection = Editable(id);
        if (collection.BatchIDs.Count == 0) throw new InvalidOperationException(L10n.Text("这组收集没有文件。"));
        collection.Status = CollectionStatus.Delivering;
        collection.Detail = null;
        return collection.BatchIDs.ToList();
    }

    public void FinishDelivery(Guid id, bool succeeded, string target, string detail, string? scene = null)
    {
        var collection = Collections.FirstOrDefault(c => c.Id == id && c.Status == CollectionStatus.Delivering)
            ?? throw new InvalidOperationException(L10n.Text("收集没有正在交付。"));
        collection.Status = succeeded ? CollectionStatus.Delivered : CollectionStatus.Retry;
        collection.TargetName = target;
        collection.Detail = detail;
        collection.SceneName = scene;
    }

    public void RecoverInterruptedDeliveries()
    {
        foreach (var collection in Collections.Where(c => c.Status == CollectionStatus.Delivering))
        {
            collection.Status = CollectionStatus.Retry;
            collection.Detail = L10n.Text("交付被中断，原始文件已保留。请确认目标附件后再重试。");
        }
    }

    public static BatchCollectionLedger Load(string path)
    {
        if (!File.Exists(path)) return new();
        var ledger = JsonSerializer.Deserialize<BatchCollectionLedger>(File.ReadAllText(path), BatchManifest.JsonOptions)
            ?? throw new InvalidDataException(L10n.Text("收集记录无法读取，原始文件已保留。"));
        if (ledger.Collections is null || ledger.SeenBatchIDs is null
            || ledger.Collections.Any(c => c is null || c.BatchIDs is null || c.Name is null))
            throw new InvalidDataException(L10n.Text("收集记录无法读取，原始文件已保留。"));
        var members = ledger.Collections.SelectMany(c => c.BatchIDs).ToList();
        if (ledger.SchemaVersion != 1 || ledger.Collections.Select(c => c.Id).Distinct().Count() != ledger.Collections.Count
            || members.Distinct().Count() != members.Count || !members.All(ledger.SeenBatchIDs.Contains)
            || ledger.Collections.Count(c => c.Status == CollectionStatus.Collecting) > 1
            || ledger.Collections.Any(c => !Enum.IsDefined(c.Status)))
            throw new InvalidDataException(L10n.Text("收集记录无法读取，原始文件已保留。"));
        return ledger;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = path + ".tmp";
        File.WriteAllText(staging, JsonSerializer.Serialize(this, BatchManifest.JsonOptions));
        File.Move(staging, path, overwrite: true);
    }
}

public sealed record CollectionBatchMetadata(int? Count, WeChatTranscriptRecord? First, WeChatTranscriptRecord? Last)
{
    public IReadOnlyList<WeChatTranscriptRecord> FirstRecords { get; init; } = [];
    public IReadOnlyList<WeChatTranscriptRecord> LastRecords { get; init; } = [];
    public static CollectionBatchMetadata Read(IEnumerable<string> paths)
    {
        var records = new List<WeChatTranscriptRecord>();
        var complete = true;
        var archives = 0;
        foreach (var path in paths.Where(p => Path.GetExtension(p).Equals(".zip", StringComparison.OrdinalIgnoreCase)))
        {
            archives++;
            try { var parsed = WeChatNativeArchive.GetTranscript(File.ReadAllBytes(path))?.Records;
                if (parsed is null) complete = false; else records.AddRange(parsed); }
            catch { complete = false; }
        }
        // OrderBy is stable, preserving exported order among minute-resolution ties.
        var ordered = records.OrderBy(r => r.Date).ToList();
        return new(complete && archives > 0 ? records.Count : null, ordered.FirstOrDefault(), ordered.LastOrDefault())
        {
            FirstRecords = ordered.Take(2).ToList(), LastRecords = ordered.TakeLast(2).ToList(),
        };
    }
}

public sealed class CollectionStatusConverter : JsonConverter<CollectionStatus>
{
    public override CollectionStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return Enum.TryParse<CollectionStatus>(reader.GetString(), ignoreCase: true, out var status) && Enum.IsDefined(status)
            ? status : throw new JsonException(L10n.Text("收集状态无效。"));
    }
    public override void Write(Utf8JsonWriter writer, CollectionStatus value, JsonSerializerOptions options)
    {
        var name = value.ToString();
        writer.WriteStringValue(char.ToLowerInvariant(name[0]) + name[1..]);
    }
}
