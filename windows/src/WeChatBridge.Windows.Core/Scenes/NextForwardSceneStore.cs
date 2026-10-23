using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// A scene chosen by a global shortcut, waiting for the next share to consume
/// it. Ported from <c>PendingSceneSelection</c> in Preferences.swift; the JSON
/// keys (<c>sceneID</c>, <c>selectedAt</c>) match macOS.
/// </summary>
public sealed record PendingSceneSelection
{
    [JsonPropertyName("sceneID")]
    public required string SceneID { get; init; }

    public DateTimeOffset SelectedAt { get; init; }
}

/// <summary>
/// The ⌃⌥1–9 selection state: <c>pending-scene.json</c> under the Config
/// directory. A shortcut chooses the scene for the *next* share, not forever —
/// anything older than <see cref="SelectionWindow"/> is ignored rather than
/// silently changing a forward the user has stopped thinking about.
///
/// Persisted like the macOS UserDefaults key so a selection survives an app
/// restart that lands between the hotkey and the share; the 60-second check
/// makes a stale file harmless.
/// </summary>
public sealed class NextForwardSceneStore
{
    public const string FileName = "pending-scene.json";

    /// <summary>Sixty seconds — macOS <c>consumePendingSceneID</c>'s window.</summary>
    public static readonly TimeSpan SelectionWindow = TimeSpan.FromSeconds(60);

    private readonly string _directory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    public NextForwardSceneStore(string? directory = null, Func<DateTimeOffset>? clock = null)
    {
        _directory = directory ?? ConfigStore.DefaultDirectory;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void SelectForNextForward(string sceneId, DateTimeOffset? selectedAt = null)
    {
        lock (_gate)
        {
            ConfigStore.Save(_directory, FileName, new PendingSceneSelection
            {
                SceneID = sceneId,
                SelectedAt = selectedAt ?? _clock(),
            });
        }
    }

    /// <summary>
    /// Takes the pending id out — consumed means gone whether it is still fresh
    /// or not, matching macOS clearing the key before checking the timestamp.
    /// A stale selection is reported as no selection.
    /// </summary>
    public string? ConsumePendingSceneId()
    {
        lock (_gate)
        {
            var selection = ConfigStore.Load<PendingSceneSelection>(_directory, FileName);
            try
            {
                File.Delete(Path.Combine(_directory, FileName));
            }
            catch
            {
                // A missing or locked file only means the selection did not
                // disappear; the freshness check below still bounds it.
            }
            if (selection is null)
                return null;
            if (_clock() - selection.SelectedAt > SelectionWindow)
                return null;
            return selection.SceneID;
        }
    }
}
