using System.Collections.Concurrent;
using System.IO;

namespace WeChatBridge.Windows.Services;

/// <summary>
/// Watches only WorkBuddy's documented local chat-history export folder. Existing
/// files are baselined at startup; only archives created during this session are
/// delivered to the caller.
/// </summary>
internal sealed class WorkBuddyArchiveWatcher : IDisposable
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".workbuddy", "app", "tmp", "chat-history");

    private readonly FileSystemWatcher _watcher;
    private readonly Action<string> _onArchive;
    private readonly Action<Exception> _onError;
    private readonly ConcurrentDictionary<string, byte> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public WorkBuddyArchiveWatcher(Action<string> onArchive, Action<Exception> onError)
    {
        _onArchive = onArchive;
        _onError = onError;
        var directory = DefaultDirectory;
        Directory.CreateDirectory(directory);
        foreach (var path in Directory.EnumerateFiles(directory, "*.zip", SearchOption.TopDirectoryOnly))
            _known.TryAdd(path, 0);

        _watcher = new FileSystemWatcher(directory, "*.zip")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            EnableRaisingEvents = false,
        };
        _watcher.Created += OnCreated;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += (_, args) => _onError(args.GetException());
        _watcher.EnableRaisingEvents = true;
    }

    private void OnCreated(object sender, FileSystemEventArgs args) => Queue(args.FullPath);
    private void OnRenamed(object sender, RenamedEventArgs args) => Queue(args.FullPath);

    private void Queue(string path)
    {
        if (_disposed || !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || _known.ContainsKey(path) || !_pending.TryAdd(path, 0)) return;
        _ = ImportWhenReadyAsync(path);
    }

    private async Task ImportWhenReadyAsync(string path)
    {
        try
        {
            long previousLength = -1;
            DateTime previousWrite = DateTime.MinValue;
            for (var attempt = 0; attempt < 40 && !_disposed; attempt++)
            {
                await Task.Delay(750).ConfigureAwait(false);
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                if (info.Length != previousLength || info.LastWriteTimeUtc != previousWrite)
                {
                    previousLength = info.Length;
                    previousWrite = info.LastWriteTimeUtc;
                    continue;
                }

                // WorkBuddy may still have the ZIP open while it finishes writing.
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                    if (stream.Length == 0) continue;
                }
                catch (IOException) { continue; }

                if (_known.TryAdd(path, 0)) _onArchive(path);
                return;
            }
        }
        catch (Exception error)
        {
            _onError(error);
        }
        finally
        {
            _pending.TryRemove(path, out _);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher.Dispose();
    }
}
