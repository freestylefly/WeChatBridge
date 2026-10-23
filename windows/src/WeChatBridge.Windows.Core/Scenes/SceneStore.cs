using System.Text.Json;
using System.Text.Json.Serialization;

namespace WeChatBridge.Windows.Core;

/// <summary>
/// JSON options shared by the Config-directory scene stores. Identical to the
/// inbox options except for <see cref="ConfigTimestampConverter"/>: macOS keeps
/// these blobs in UserDefaults with <c>JSONEncoder</c>'s deferred-to-Date
/// strategy, which writes dates as *seconds since 2001-01-01*, while every file
/// Windows writes uses the shared ISO-8601 <c>Z</c> form. Reading both keeps a
/// macOS-authored preferences file — or a hand-edited <c>updatedAt: 0</c> —
/// decodable on Windows.
/// </summary>
public static class SceneConfigJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(BatchManifest.JsonOptions);
        // Converters are consulted in order; this must precede the ISO-only one.
        options.Converters.Insert(0, new ConfigTimestampConverter());
        return options;
    }
}

/// <summary>
/// Reads an ISO-8601 string or a legacy Apple-epoch number, always writes the
/// shared ISO-8601 <c>Z</c> form.
/// </summary>
public sealed class ConfigTimestampConverter : JsonConverter<DateTimeOffset>
{
    private static readonly DateTimeOffset AppleReferenceDate =
        new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => DateTimeOffset.Parse(
                reader.GetString()!,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.RoundtripKind),
            JsonTokenType.Number => AppleReferenceDate.AddSeconds(reader.GetDouble()),
            _ => throw new JsonException("Config timestamp must be a string or a number."),
        };

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>
/// The scene library on disk: <c>scenes.json</c> under
/// <c>%LOCALAPPDATA%\WeChatBridge\Config</c>. Ported from the
/// <c>com.xiangming.wechatbridge.scenes.v3</c> UserDefaults blob, including the
/// decode-time repairs macOS performs — installing missing official scenes and
/// migrating the legacy attached-prompt blob.
/// </summary>
public sealed class SceneStore
{
    public const string FileName = "scenes.json";

    private readonly string _directory;

    public SceneStore(string? directory = null) =>
        _directory = directory ?? ConfigStore.DefaultDirectory;

    /// <summary>
    /// Loads the library, or the five starter scenes when nothing is stored.
    /// A file that cannot be read or decoded is treated as "never written":
    /// a corrupt preference must not stop the share flow.
    /// </summary>
    public SceneSettings Load()
    {
        var path = Path.Combine(_directory, FileName);
        if (!File.Exists(path))
            return SceneSettings.MakeDefault();
        try
        {
            var text = File.ReadAllText(path);
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("scenes", out _))
            {
                var settings = JsonSerializer.Deserialize<SceneSettings>(text, SceneConfigJson.Options);
                if (settings is null)
                    return SceneSettings.MakeDefault();
                // macOS runs installOfficialScenes + normalize inside its decoder;
                // doing it here keeps a partial library self-healing.
                settings.InstallOfficialScenes();
                return settings;
            }
            return MigrateLegacyPromptBlob(root) ?? SceneSettings.MakeDefault();
        }
        catch
        {
            return SceneSettings.MakeDefault();
        }
    }

    public void Save(SceneSettings settings) =>
        ConfigStore.Save(_directory, FileName, settings);

    /// <summary>
    /// The pre-scene preferences blob had no <c>scenes</c> key — just
    /// <c>prompts</c> + <c>selectedID</c>. macOS converts each prompt into an
    /// enabled scene ahead of the starters; the Windows store applies the same
    /// migration at load so a copied-over blob means the same thing.
    /// </summary>
    private static SceneSettings? MigrateLegacyPromptBlob(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("prompts", out var prompts)
            || prompts.ValueKind != JsonValueKind.Array)
            return null;

        var converted = new List<WeChatScene>();
        var index = 0;
        foreach (var prompt in prompts.EnumerateArray())
        {
            index++;
            if (!prompt.TryGetProperty("id", out var idElement)
                || !prompt.TryGetProperty("text", out var textElement)
                || !Guid.TryParse(idElement.GetString(), out var id))
                continue;
            converted.Add(new WeChatScene
            {
                // uuidString is upper-case on macOS; the id is what binds groups.
                Id = id.ToString().ToUpperInvariant(),
                Name = $"原 Prompt {index}",
                Summary = "由旧版附加 Prompt 迁移。",
                Instruction = textElement.GetString() ?? "",
                OutputSpec = SceneSettings.StandardOutputSpec,
                Enabled = true,
                PackageVersion = "1.0.0",
            });
        }
        if (converted.Count == 0)
            return SceneSettings.MakeDefault();

        var scenes = SceneSettings.StarterScenes();
        scenes.AddRange(converted);
        var settings = new SceneSettings { Scenes = scenes };

        var selected = root.TryGetProperty("selectedID", out var selectedElement)
            && Guid.TryParse(selectedElement.GetString(), out var selectedId)
                ? selectedId.ToString().ToUpperInvariant()
                : null;
        settings.DefaultSceneID = settings.Scenes.Any(s => s.Id == selected)
            ? selected
            : converted[0].Id;
        if (root.TryGetProperty("attachToForwards", out var attach) && attach.ValueKind == JsonValueKind.True)
            settings.AttachToForwards = true;
        settings.InstallOfficialScenes();
        return settings;
    }
}
