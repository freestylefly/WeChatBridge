using System.Text;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// Turns host-supplied text into something safe to write to disk. Ported from
/// <c>DisplayName</c> for the vault-delivery names it produces. The forbidden
/// set is a superset of macOS's <c>/:\0</c>: Windows also refuses
/// <c>&lt;&gt;|?*"\</c>, and a name that cannot be created is a failed
/// delivery, not a quirky filename.
/// </summary>
public static class DisplayName
{
    public static string SubfolderPath(string? raw) => string.Join(
        Path.DirectorySeparatorChar,
        (raw ?? "").Replace('\\', '/').Split('/')
            .Select(s => s.Trim()).Where(s => s.Length > 0 && s != "." && s != "..")
            .Select(s => Sanitize(s).TrimEnd('.', ' ')));

    public const string FallbackBaseName = "共享文件";

    private static readonly HashSet<char> WindowsInvalid = new(Path.GetInvalidFileNameChars());

    public static string Sanitize(string? raw, string? fallbackExtension = null)
    {
        var name = LastPathComponent(raw);
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is '/' or ':' or '\0')
            {
                builder.Append('-');
            }
            else if (char.IsControl(c))
            {
                // Control characters (including newlines a host might smuggle
                // in) have no place in a filename shown in a list.
                builder.Append(' ');
            }
            else if (WindowsInvalid.Contains(c))
            {
                builder.Append('-');
            }
            else
            {
                builder.Append(c);
            }
        }
        name = builder.ToString().Trim();

        // "." and ".." are directory entries, and a leading dot hides the file
        // from Explorer — none of which the sharer asked for.
        if (name.Length == 0 || name == "." || name == ".." || name.StartsWith('.'))
            name = FallbackBaseName + name;

        if (Path.GetExtension(name).Length == 0 && !string.IsNullOrEmpty(fallbackExtension))
            name = $"{name}.{fallbackExtension}";
        return Truncate(name);
    }

    /// <summary>
    /// Keeps the extension attached: a 260-character Chinese name truncated
    /// naively becomes an extensionless file that no app knows how to open.
    /// </summary>
    internal static string Truncate(string name, int limit = 200)
    {
        if (Encoding.UTF8.GetByteCount(name) <= limit)
            return name;
        var extension = Path.GetExtension(name);
        var baseName = name[..^extension.Length];
        var budget = Math.Max(1, limit - Encoding.UTF8.GetByteCount(extension));
        while (baseName.Length > 0 && Encoding.UTF8.GetByteCount(baseName) > budget)
            baseName = DropLastScalar(baseName);
        if (baseName.Length == 0)
            baseName = FallbackBaseName;
        return baseName + extension;
    }

    /// <summary>NSString.lastPathComponent semantics: only '/' separates.</summary>
    private static string LastPathComponent(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;
        var trimmed = raw.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }

    /// <summary>Drops one scalar, not one UTF-16 unit — halves no emoji.</summary>
    internal static string DropLastScalar(string value)
    {
        if (value.Length == 0)
            return value;
        var count = value.Length > 1 && char.IsLowSurrogate(value[^1]) ? 2 : 1;
        return value[..^count];
    }
}
