using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// An app the user added to 「发送到自定义」. Ported from ForwardTarget.swift.
/// On Windows <see cref="BundleIdentifier"/> carries the stable target identity —
/// an executable path or a packaged app's AUMID — stored by value rather than
/// resolved on every read so history can still name an uninstalled app.
/// </summary>
public sealed record ForwardTarget(
    string BundleIdentifier,
    string DisplayName,
    DateTimeOffset AddedAt,
    /// <summary>
    /// Paste the files' paths as text instead of the files themselves: terminals
    /// have nothing to receive a pasted file with.
    /// </summary>
    bool PastesPathOnly = false);

/// <summary>
/// JSON-file-backed store for user-facing lists that macOS keeps in UserDefaults.
/// Windows equivalent lives under %LOCALAPPDATA%\WeChatBridge\Config.
/// </summary>
public static class ConfigStore
{
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WeChatBridge",
        "Config");

    public static T? Load<T>(string directory, string fileName)
    {
        try
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
                return default;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), BatchManifest.JsonOptions);
        }
        catch
        {
            // A corrupt preference must not stop the share flow; treat as empty.
            return default;
        }
    }

    public static void Save<T>(string directory, string fileName, T value)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        var staging = path + ".tmp";
        File.WriteAllText(staging, JsonSerializer.Serialize(value, BatchManifest.JsonOptions));
        File.Move(staging, path, overwrite: true);
    }
}

/// <summary>
/// The list of custom forward targets plus the last-used marker.
/// Ported from ForwardTargetStore.swift; stored order is the user's own order.
/// </summary>
public sealed class ForwardTargetStore
{
    public const string FileName = "forward-targets.json";
    public const string LastUsedFileName = "forward-target-last-used.txt";

    private readonly string _directory;

    public ForwardTargetStore(string? directory = null) =>
        _directory = directory ?? ConfigStore.DefaultDirectory;

    public List<ForwardTarget> Load() =>
        ConfigStore.Load<List<ForwardTarget>>(_directory, FileName) ?? [];

    public void Save(IReadOnlyList<ForwardTarget> targets) =>
        ConfigStore.Save(_directory, FileName, targets.ToList());

    /// <summary>Last pick first, then stored order — the panel opens on the likeliest row.</summary>
    public List<ForwardTarget> OrderedTargets() =>
        Ordered(Load(), LastUsedBundleIdentifier());

    public static List<ForwardTarget> Ordered(List<ForwardTarget> targets, string? lastUsed)
    {
        if (lastUsed is null)
            return targets;
        var index = targets.FindIndex(t => t.BundleIdentifier == lastUsed);
        if (index < 0)
            return targets;
        var ordered = new List<ForwardTarget>(targets);
        var item = ordered[index];
        ordered.RemoveAt(index);
        ordered.Insert(0, item);
        return ordered;
    }

    public string? LastUsedBundleIdentifier() =>
        ConfigStore.Load<string>(_directory, LastUsedFileName);

    public void RecordLastUsed(string bundleIdentifier) =>
        ConfigStore.Save(_directory, LastUsedFileName, bundleIdentifier);
}
