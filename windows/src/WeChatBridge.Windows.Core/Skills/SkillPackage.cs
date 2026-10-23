using System.Security.Cryptography;
using System.Text;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// Package-directory helpers used by <see cref="SkillStore"/> (the app-owned
/// skill library) and <see cref="SkillArchive"/> (user zip imports):
/// validation, the cross-platform SHA-256 digest, safe enumeration and copy.
/// The digest matches the macOS SkillInstaller byte-for-byte so copies hash
/// identically on both platforms.
/// </summary>
internal static class SkillPackage
{
    /// <summary>
    /// The install sidecar written next to agent-deployed copies. The Windows
    /// port no longer installs into agents, but legacy packages may carry one
    /// and the digest must skip it to match macOS.
    /// </summary>
    internal const string MetadataFileName = ".wechatbridge-install.json";

    private static readonly byte[] DigestSeparator = [0];

    /// <summary>
    /// A package member. <see cref="RelativePath"/> is always '/'-joined —
    /// macOS derives it from POSIX paths and the digest bytes must match.
    /// </summary>
    internal sealed record PackageFile(string FullPath, string RelativePath);

    internal static string ValidatePackage(string root)
    {
        if (!Directory.Exists(root))
            throw new SkillException("技能包目录不存在。");
        if (!File.Exists(Path.Combine(root, "SKILL.md")))
            throw new SkillException("技能包缺少 SKILL.md。");
        return PackageDigest(root);
    }

    /// <summary>
    /// Byte-identical digest stream to macOS: per file, the UTF-8 '/'-joined
    /// relative path, one NUL byte, the file bytes, one NUL byte — enumerated in
    /// sorted order with the install sidecar skipped.
    /// </summary>
    internal static string PackageDigest(string root)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in EnumeratePackageFiles(root))
        {
            if (file.RelativePath == MetadataFileName)
                continue;
            hasher.AppendData(Encoding.UTF8.GetBytes(file.RelativePath));
            hasher.AppendData(DigestSeparator);
            hasher.AppendData(File.ReadAllBytes(file.FullPath));
            hasher.AppendData(DigestSeparator);
        }
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    internal static void CopyPackage(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in EnumeratePackageFiles(source))
        {
            var target = Path.Combine(
                destination, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file.FullPath, target);
        }
    }

    /// <summary>
    /// Sorted file enumeration with the macOS safety checks preserved verbatim:
    /// the root or any member being a symlink/junction (reparse point) rejects
    /// the package, as does any member that is neither file nor directory.
    /// The walk recurses manually so a linked directory can never be entered —
    /// Directory.Enumerate with AllDirectories could follow a junction before
    /// we had a chance to reject it.
    /// </summary>
    internal static List<PackageFile> EnumeratePackageFiles(string root)
    {
        var rootFull = Path.GetFullPath(root);
        FileAttributes rootAttributes;
        try
        {
            rootAttributes = File.GetAttributes(rootFull);
        }
        catch (Exception error) when (IsFileSystemError(error))
        {
            throw new SkillException("技能包目录不存在。");
        }
        if (rootAttributes.HasFlag(FileAttributes.ReparsePoint))
            throw new SkillException("技能包不能包含符号链接。");
        if (!rootAttributes.HasFlag(FileAttributes.Directory))
            throw new SkillException("技能包目录无效。");

        var files = new List<PackageFile>();
        var pending = new Stack<string>();
        pending.Push(rootFull);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new SkillException("技能包不能包含符号链接。");
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(entry);
                    continue;
                }
                if (!File.Exists(entry))
                    throw new SkillException("技能包只能包含普通文件和目录。");

                var relative = Path.GetRelativePath(rootFull, entry).Replace('\\', '/');
                if (relative.StartsWith("..", StringComparison.Ordinal))
                    throw new SkillException("技能包包含越界路径。");
                files.Add(new PackageFile(entry, relative));
            }
        }

        // Ordinal ordering is deterministic and matches the macOS relativePath
        // sort for every path a package can realistically contain.
        files.Sort(static (a, b) => string.CompareOrdinal(a.RelativePath, b.RelativePath));
        return files;
    }

    /// <summary>File OR directory — the analogue of Cocoa fileExists(atPath:).</summary>
    internal static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    internal static void MoveAny(string source, string destination)
    {
        if (Directory.Exists(source))
            Directory.Move(source, destination);
        else
            File.Move(source, destination);
    }

    internal static void DeleteAny(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        else if (File.Exists(path))
            File.Delete(path);
    }

    internal static bool IsFileSystemError(Exception error) =>
        error is IOException or UnauthorizedAccessException or NotSupportedException
            or System.Security.SecurityException;
}
