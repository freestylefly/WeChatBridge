using System.IO.Compression;
using static WeChatBridge.Windows.Core.SkillPackage;

namespace WeChatBridge.Windows.Core;

/// <summary>Metadata read from a package's SKILL.md frontmatter.</summary>
public sealed record SkillPackageInfo(string Id, string DisplayName, string Summary, string Version);

/// <summary>
/// Reads user-supplied skill zips into a staging directory. A valid archive
/// holds exactly one SKILL.md — either at the zip root or wrapped in a single
/// top-level directory, which is also the shape our own 导出 ZIP produces
/// ("{id}/SKILL.md"). Every entry name is normalized and checked for
/// traversal before anything touches disk, and the extracted tree then goes
/// through the same <see cref="SkillPackage"/> validation as shipped
/// packages (no reparse points, no non-file members).
/// </summary>
public static class SkillArchive
{
    /// <summary>Guard against zip bombs — real skill packages are kilobytes.</summary>
    private const long MaxArchiveBytes = 128L * 1024 * 1024;

    /// <summary>
    /// Extracts <paramref name="archivePath"/> under <paramref name="stagingParent"/>
    /// and returns the package directory with its parsed frontmatter. On success the
    /// caller owns the staging directory; on failure it is already gone.
    /// </summary>
    public static (string Directory, SkillPackageInfo Info) ExtractToStaging(
        string archivePath, string stagingParent)
    {
        var staging = Path.Combine(stagingParent, $"skill-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var root = WriteEntries(archivePath, staging);
            return (staging, ReadInfo(staging, fallbackId: root));
        }
        catch
        {
            try { DeleteAny(staging); } catch (Exception error) when (IsFileSystemError(error)) { }
            throw;
        }
    }

    /// <summary>
    /// Parses the frontmatter of <c>SKILL.md</c> inside an already-extracted
    /// package directory. <paramref name="fallbackId"/> is the wrapping folder
    /// name from the archive — used as the id only when frontmatter omits
    /// <c>name</c>. The display name comes from the first Markdown heading,
    /// falling back to the id itself.
    /// </summary>
    public static SkillPackageInfo ReadInfo(string packageDirectory, string? fallbackId = null)
    {
        var file = Path.Combine(packageDirectory, "SKILL.md");
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception error) when (IsFileSystemError(error))
        {
            throw new SkillException("SKILL.md 无法读取。");
        }

        var fields = Frontmatter(text, out var body);
        var id = fields.TryGetValue("name", out var named) && named.Length > 0
            ? named
            : fallbackId;
        if (!SkillId.IsValid(id))
            throw new SkillException(
                id is { Length: > 0 }
                    ? $"技能 ID「{id}」不符合规范（仅限小写字母、数字和连字符）。"
                    : "SKILL.md 的 frontmatter 中没有 name 字段。");

        var version = fields.TryGetValue("version", out var v) ? v
            : fields.TryGetValue("metadata.version", out var nested) ? nested : null;
        // The registry only needs an ordered version; anything unreadable
        // lands on 1.0.0 rather than failing the whole import.
        if (version is null || SceneVersion.Parse(version) is null)
            version = "1.0.0";

        var summary = fields.TryGetValue("description", out var description)
            ? description : "";
        return new SkillPackageInfo(id!, Heading(body) ?? id!, summary, version);
    }

    /// <summary>
    /// Validates every entry name, picks the single package root, and writes
    /// the files under it into <paramref name="staging"/>. Returns the
    /// wrapping directory's name, or null for a root-level SKILL.md.
    /// </summary>
    private static string? WriteEntries(string archivePath, string staging)
    {
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(archivePath);
        }
        catch (Exception error) when (error is InvalidDataException or IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            throw new SkillException("压缩包无法读取或已损坏。");
        }

        using (archive)
        {
            var files = new List<(string Name, ZipArchiveEntry Entry)>();
            string? skillMd = null;
            foreach (var entry in archive.Entries)
            {
                var name = NormalizeEntryPath(entry.FullName);
                if (name.EndsWith("/", StringComparison.Ordinal))
                    continue; // directory entries are implicit
                files.Add((name, entry));
                var segments = name.Split('/');
                if (segments[^1] == "SKILL.md" && segments.Length <= 2)
                {
                    if (skillMd is not null)
                        throw new SkillException("压缩包中有多个 SKILL.md，无法确定技能目录。");
                    skillMd = name;
                }
            }
            if (skillMd is null)
                throw new SkillException("压缩包中没有找到 SKILL.md。");

            // "" for a root-level package, "dir/" for a wrapped one.
            var prefix = skillMd[..^"SKILL.md".Length];
            long totalBytes = 0;
            foreach (var (name, entry) in files)
            {
                if (!name.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                totalBytes += entry.Length;
                if (totalBytes > MaxArchiveBytes)
                    throw new SkillException("压缩包内容过大，超过 128 MB 上限。");
                var relative = name[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
                var target = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try
                {
                    using var input = entry.Open();
                    using var output = new FileStream(
                        target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    input.CopyTo(output);
                }
                catch (Exception error) when (IsFileSystemError(error))
                {
                    throw new SkillException($"文件「{relative}」无法写入临时目录。");
                }
            }
            ValidatePackage(staging);
            return prefix.Length == 0 ? null : prefix.TrimEnd('/');
        }
    }

    /// <summary>
    /// One archive name, '///'-normalized and rejected when it could escape the
    /// staging root: absolute paths, drive letters, parent segments.
    /// </summary>
    private static string NormalizeEntryPath(string fullName)
    {
        var name = fullName.Replace('\\', '/');
        var unsafePath = name.Length == 0
            || name.StartsWith("/", StringComparison.Ordinal)
            || (name.Length >= 2 && name[1] == ':')
            || name.Split('/').Any(segment => segment is ".." or ".");
        return unsafePath ? throw new SkillException("压缩包包含不安全的路径。") : name;
    }

    /// <summary>
    /// The frontmatter block between the opening and closing '---' lines, as a
    /// flat key→scalar map; one nested level (e.g. <c>metadata:</c>) folds into
    /// "parent.child" keys. Values may be single- or double-quoted.
    /// </summary>
    private static Dictionary<string, string> Frontmatter(string text, out string body)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        body = text;
        if (lines.Length == 0 || lines[0].Trim() != "---")
            return fields;

        var end = -1;
        string? parent = null;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim() is "---" or "...")
            {
                end = i;
                break;
            }
            if (line.StartsWith(' ') || line.StartsWith('\t'))
            {
                if (parent is not null && TrySplit(line.Trim(), out var key, out var value))
                    fields[$"{parent}.{key}"] = value;
                continue;
            }
            if (!TrySplit(line, out var k, out var v))
                continue;
            if (v.Length == 0)
            {
                parent = k; // a block mapping like "metadata:" — keep the parent name
                continue;
            }
            parent = null;
            fields[k] = v;
        }

        if (end > 0)
            body = string.Join('\n', lines[(end + 1)..]);
        return fields;
    }

    private static bool TrySplit(string line, out string key, out string value)
    {
        var colon = line.IndexOf(':', StringComparison.Ordinal);
        key = value = "";
        if (colon <= 0)
            return false;
        key = line[..colon].Trim();
        value = Unquote(line[(colon + 1)..].Trim());
        return key.Length > 0;
    }

    private static string Unquote(string value) =>
        value.Length >= 2
        && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1] : value;

    /// <summary>The first "# …" heading in the markdown body, if any.</summary>
    private static string? Heading(string body)
    {
        foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("# ", StringComparison.Ordinal) && trimmed.Length > 2)
                return trimmed[2..].Trim();
        }
        return null;
    }
}
