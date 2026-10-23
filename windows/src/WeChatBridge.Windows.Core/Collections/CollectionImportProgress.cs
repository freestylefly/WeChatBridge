using System.Text.Json;

namespace WeChatBridge.Windows.Core;

/// <summary>Transient receipt feedback, separate from the durable collection ledger.</summary>
public sealed record CollectionImportProgress(Guid OperationId, DateTimeOffset UpdatedAt, string Phase)
{
    public const string FileName = "collection-import.json";
    public const string EventName = "Local\\WeChatBridge.Windows.CollectionImport";
    public bool IsFresh(DateTimeOffset now) => now - UpdatedAt < TimeSpan.FromSeconds(45) && UpdatedAt <= now.AddSeconds(5);
    public static CollectionImportProgress? Read(InboxPaths paths)
    {
        try { return JsonSerializer.Deserialize<CollectionImportProgress>(File.ReadAllText(Path.Combine(paths.Root, FileName))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public void Publish(InboxPaths paths)
    {
        try
        {
            paths.EnsureCreated();
            var file = Path.Combine(paths.Root, FileName);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this));
            File.Move(temp, file, true);
            if (OperatingSystem.IsWindows())
            {
                using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
                signal.Set();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Heartbeat expires if the helper dies; terminal states cannot be overwritten by a timer.</summary>
public sealed class CollectionImportSession : IDisposable
{
    private readonly InboxPaths _paths;
    private readonly Guid _id = Guid.NewGuid();
    private readonly object _gate = new();
    private readonly Timer _timer;
    private bool _ended;
    public CollectionImportSession(InboxPaths paths)
    {
        _paths = paths;
        new CollectionImportProgress(_id, DateTimeOffset.UtcNow, "saving").Publish(paths);
        _timer = new Timer(_ =>
        {
            lock (_gate) if (!_ended) new CollectionImportProgress(_id, DateTimeOffset.UtcNow, "saving").Publish(_paths);
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }
    public void Finish(bool success)
    {
        lock (_gate)
        {
            if (_ended) return;
            _ended = true;
            _timer.Dispose();
            new CollectionImportProgress(_id, DateTimeOffset.UtcNow, success ? "finished" : "failed").Publish(_paths);
        }
    }
    public void Dispose() => Finish(false);
}
