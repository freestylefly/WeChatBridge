using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// A throwaway inbox plus a source directory to share files from, so tests never
/// touch the real <c>%LOCALAPPDATA%</c> inbox.
/// </summary>
internal sealed class TempInbox : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "WeChatBridgeTests",
        Guid.NewGuid().ToString("N"));
    private readonly string _sourceRoot;

    public TempInbox()
    {
        Paths = new InboxPaths(_root);
        _sourceRoot = Path.Combine(_root, "sources");
        Directory.CreateDirectory(_sourceRoot);
    }

    public InboxPaths Paths { get; }

    public string Root => _root;

    public string WriteSource(string name, string content)
    {
        var path = Path.Combine(_sourceRoot, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Creates a source file of an exact byte length.</summary>
    public string WriteSourceOfSize(string name, long bytes)
    {
        var path = Path.Combine(_sourceRoot, name);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.SetLength(bytes);
        return path;
    }

    /// <summary>
    /// Backdates every timestamp in a tree. Deepest first, because touching a
    /// directory after its children would refresh it again.
    /// </summary>
    public void SetWriteTime(DateTimeOffset stamp, string path)
    {
        var targets = new List<string>();
        if (Directory.Exists(path))
            targets.AddRange(Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories));
        targets.Add(path);

        foreach (var target in targets)
        {
            try
            {
                // File.SetLastWriteTimeUtc throws on a directory, and the matching
                // Directory call throws on a file, so the kind has to be picked first.
                if (Directory.Exists(target))
                    Directory.SetLastWriteTimeUtc(target, stamp.UtcDateTime);
                else
                    File.SetLastWriteTimeUtc(target, stamp.UtcDateTime);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
