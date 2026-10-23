using System.Text;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// Copies files into a user-chosen folder without ever destroying what was
/// already there. Ported from <c>FolderDelivery.swift</c>: copy into a private
/// staging directory inside the destination, then publish each complete file
/// with a non-overwriting rename. On failure only this call's files are rolled
/// back; pre-existing destinations stay untouched.
/// </summary>
public static class FolderDelivery
{
    public enum FailureKind
    {
        InvalidFolder,
        InvalidSource,
        CleanupFailed,
    }

    public sealed class FailureException : Exception
    {
        public FailureException(FailureKind kind, Exception? inner = null)
            : base(Describe(kind, inner), inner) => Kind = kind;

        public FailureKind Kind { get; }

        private static string Describe(FailureKind kind, Exception? inner) => kind switch
        {
            FailureKind.InvalidFolder => "请选择一个可写入的文件夹。",
            FailureKind.InvalidSource => "待保存的文件无法读取，请重新导出。",
            FailureKind.CleanupFailed => $"保存未完成，部分文件可能已留在目标文件夹：{inner?.Message}",
            _ => "请选择一个可写入的文件夹。",
        };
    }

    public static void ValidateFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            throw new FailureException(FailureKind.InvalidFolder);
        // ACLs, read-only flags and sync providers all decide writability at
        // write time; the honest check is a real create.
        var probe = Path.Combine(folder, ".wechatbridge-write-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new FailureException(FailureKind.InvalidFolder, error);
        }
    }

    /// <summary>
    /// Copy first, then publish complete files with exclusive renames. Returns
    /// the destination paths in the order the sources were given.
    /// </summary>
    public static List<string> Save(
        IReadOnlyList<string> sources,
        string folder,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateFolder(folder);
        if (sources.Count == 0)
            return [];
        var destination = ResolveFolder(folder);
        var staging = MakeStagingDirectory(destination, cancellationToken);
        var published = new List<(string Path, FileIdentity Identity)>(sources.Count);
        try
        {
            var prepared = new List<(string Staged, string Name)>(sources.Count);
            for (var index = 0; index < sources.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var staged = Path.Combine(staging, index.ToString());
                Copy(sources[index], staged, cancellationToken);
                var name = DisplayName.Sanitize(Path.GetFileName(sources[index]));
                // Reserve room for collision suffixes even for unusually long
                // extensions, which DisplayName intentionally keeps attached.
                while (name.Length > 0 && Encoding.UTF8.GetByteCount(name) > 200)
                    name = DisplayName.DropLastScalar(name);
                prepared.Add((staged, name));
            }

            foreach (var file in prepared)
            {
                var number = 1;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var candidate = Path.Combine(destination, UniqueName(file.Name, number));
                    try
                    {
                        // overwrite:false is the RENAME_EXCL equivalent: a name
                        // already taken bumps the counter instead of replacing.
                        File.Move(file.Staged, candidate, overwrite: false);
                        published.Add((candidate, FileIdentity.Of(candidate)));
                        break;
                    }
                    catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate))
                    {
                        number++;
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return published.Select(p => p.Path).ToList();
        }
        catch (Exception error)
        {
            var cleanupFailed = false;
            for (var index = published.Count - 1; index >= 0; index--)
            {
                var file = published[index];
                try
                {
                    var info = new FileInfo(file.Path);
                    if (!info.Exists)
                        continue;
                    // Someone may replace an output while a save is being
                    // stopped. Never remove that replacement. macOS proves the
                    // file is still ours with (device, inode); length plus both
                    // timestamps is the closest managed equivalent.
                    if (!file.Identity.Matches(file.Path))
                        continue;
                    File.Delete(file.Path);
                }
                catch (IOException)
                {
                    cleanupFailed = true;
                }
                catch (UnauthorizedAccessException)
                {
                    cleanupFailed = true;
                }
            }
            if (cleanupFailed)
                throw new FailureException(FailureKind.CleanupFailed, error);
            throw;
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// What we believe about a published file, so rollback only removes what
    /// this save wrote — Windows has no inode, so length and both timestamps
    /// stand in for <c>(st_dev, st_ino)</c>.
    /// </summary>
    private sealed record FileIdentity(long Length, DateTime CreationTimeUtc, DateTime LastWriteTimeUtc)
    {
        public static FileIdentity Of(string path)
        {
            var info = new FileInfo(path);
            info.Refresh();
            return new FileIdentity(info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc);
        }

        public bool Matches(string path)
        {
            var info = new FileInfo(path);
            return info.Exists
                && info.Length == Length
                && info.CreationTimeUtc == CreationTimeUtc
                && info.LastWriteTimeUtc == LastWriteTimeUtc;
        }
    }

    private static void Copy(string source, string staged, CancellationToken cancellationToken)
    {
        var info = new FileInfo(source);
        if (!info.Exists
            || (info.Attributes & FileAttributes.Directory) != 0
            || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new FailureException(FailureKind.InvalidSource);
        }
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[1_048_576];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
        }
        cancellationToken.ThrowIfCancellationRequested();
        output.Flush(flushToDisk: true);
    }

    private static string MakeStagingDirectory(string folder, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var staging = Path.Combine(folder, ".wechatbridge-forward-" + Guid.NewGuid().ToString("N"));
            if (!Directory.Exists(staging) && !File.Exists(staging))
            {
                Directory.CreateDirectory(staging);
                return staging;
            }
        }
    }

    private static string UniqueName(string name, int number)
    {
        if (number <= 1)
            return name;
        var extension = Path.GetExtension(name);
        var stem = name[..^extension.Length];
        return $"{stem} ({number}){extension}";
    }

    private static string ResolveFolder(string folder)
    {
        // resolvingSymlinksInPath: hand the vault's real location to the copy.
        try
        {
            var link = Directory.ResolveLinkTarget(folder, returnFinalTarget: true);
            return link?.FullName ?? Path.GetFullPath(folder);
        }
        catch
        {
            return Path.GetFullPath(folder);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Cleanup must never hide the original save failure.
        }
    }
}
