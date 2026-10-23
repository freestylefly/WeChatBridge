using System.Text.Json;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// Group facts on disk: <c>group-memories.json</c> under
/// <c>%LOCALAPPDATA%\WeChatBridge\Config</c>, a dictionary keyed by the
/// normalized group name. Ported from the
/// <c>com.xiangming.wechatbridge.groupMemory.v1</c> UserDefaults blob.
///
/// Kept apart from the scene library on purpose — macOS stores it separately so
/// that deleting a scene or clearing history cannot erase which group was
/// bound to which scene, and the fingerprint senders that take weeks to learn.
/// </summary>
public sealed class GroupMemoryStore
{
    public const string FileName = "group-memories.json";

    private readonly string _directory;

    public GroupMemoryStore(string? directory = null) =>
        _directory = directory ?? ConfigStore.DefaultDirectory;

    /// <summary>
    /// Loads the memory map, or an empty one. <see cref="ConfigStore"/> swallows
    /// corrupt JSON into null, and null must mean "no memories yet" — a damaged
    /// file must not break scene resolution, it just falls back to asking.
    /// </summary>
    public Dictionary<string, GroupMemory> Load()
    {
        var path = Path.Combine(_directory, FileName);
        if (!File.Exists(path))
            return new Dictionary<string, GroupMemory>(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, GroupMemory>>(
                       File.ReadAllText(path), SceneConfigJson.Options)
                   ?? new Dictionary<string, GroupMemory>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, GroupMemory>(StringComparer.Ordinal);
        }
    }

    public void Save(IReadOnlyDictionary<string, GroupMemory> memories) =>
        ConfigStore.Save(
            _directory, FileName,
            new Dictionary<string, GroupMemory>(memories, StringComparer.Ordinal));
}
