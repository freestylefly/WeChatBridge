namespace WeChatBridge.Windows.Core;

/// <summary>
/// The Obsidian destination as a JSON file, so the Core library can deliver
/// without depending on the WPF settings type. Stored as
/// <c>%LOCALAPPDATA%\WeChatBridge\Config\obsidian.json</c> with camelCase
/// fields (<c>vaultPath</c>, <c>subfolder</c>) through
/// <see cref="BatchManifest.JsonOptions"/>, matching the other config files.
/// </summary>
public sealed record ObsidianSettings
{
    public string? VaultPath { get; init; }

    public string Subfolder { get; init; } = "微信流";
}

/// <summary>Loads and saves <see cref="ObsidianSettings"/> via <see cref="ConfigStore"/>.</summary>
public sealed class ObsidianSettingsStore
{
    public const string FileName = "obsidian.json";

    private readonly string _directory;

    public ObsidianSettingsStore(string? directory = null) =>
        _directory = directory ?? ConfigStore.DefaultDirectory;

    /// <summary>A missing or corrupt file reads as not-configured, like every ConfigStore value.</summary>
    public ObsidianSettings Load() =>
        ConfigStore.Load<ObsidianSettings>(_directory, FileName) ?? new ObsidianSettings();

    public void Save(ObsidianSettings settings) =>
        ConfigStore.Save(_directory, FileName, settings);
}
