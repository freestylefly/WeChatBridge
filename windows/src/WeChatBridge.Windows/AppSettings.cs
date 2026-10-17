using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows;

/// <summary>
/// App-level preferences that macOS keeps in UserDefaults. Stored as
/// %LOCALAPPDATA%\WeChatBridge\Config\settings.json through
/// <see cref="ConfigStore"/> so a corrupt file reads as defaults rather than
/// breaking the share flow.
/// </summary>
public sealed class AppSettings
{
    public const string FileName = "settings.json";
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// A week, matching macOS <c>Preferences.defaultHistoryRetentionDays</c>:
    /// long enough that last Friday's export is still there on Monday, short
    /// enough that the inbox does not quietly become an archive.
    /// </summary>
    public const int DefaultHistoryRetentionDays = 7;

    /// <summary>The retention choices the settings pane offers; 0 means 永久保留.</summary>
    public static readonly IReadOnlyList<int> RetentionChoices = [0, 7, 30];

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// Share-menu entries the user switched off, by <see cref="ShareActions.RawValue"/>.
    /// Absent means enabled — a fresh install offers every entry.
    /// </summary>
    public List<string> DisabledEntries { get; set; } = [];

    /// <summary>0 means keep history forever.</summary>
    public int HistoryRetentionDays { get; set; } = DefaultHistoryRetentionDays;

    /// <summary>The Obsidian vault 沉淀到 Obsidian writes into.</summary>
    public string? ObsidianVaultPath { get; set; }

    /// <summary>Folder under the vault the chat Markdown and original ZIP land in.</summary>
    public string ObsidianSubfolder { get; set; } = "微信流";
    public string Language { get; set; } = "system";
    public bool OpenObsidianAfterDelivery { get; set; }
    public string? DeliveryFolderPath { get; set; }
    public string DeliverySubfolder { get; set; } = "微信流";

    /// <summary>
    /// 自定义的 Inbox 根目录。为 null 时使用默认 %LOCALAPPDATA%\WeChatBridge\Inbox；
    /// 非空时，主程序与分享助手都会把 Staging/Ready/Failed/Logs 建在该目录之下。
    /// </summary>
    public string? InboxRoot { get; set; }

    public string? LastCollectionTarget { get; set; }
    public List<string> RecentCollectionFolders { get; set; } = [];
    public string? LastCollectionScene { get; set; }

    /// <summary>
    /// Keep a hidden instance resident after sign-in. A share's latency is
    /// dominated by cold-starting this process; resident, the helper's signal
    /// is consumed and forwarded in under a second.
    /// </summary>
    public bool LaunchAtLogin { get; set; } = true;

    public bool IsEntryEnabled(ShareAction action) =>
        !DisabledEntries.Contains(action.RawValue());

    public void SetEntryEnabled(ShareAction action, bool enabled)
    {
        var raw = action.RawValue();
        if (enabled)
            DisabledEntries.Remove(raw);
        else if (!DisabledEntries.Contains(raw))
            DisabledEntries.Add(raw);
    }
}

public sealed class AppSettingsStore
{
    private readonly string _directory;

    public AppSettingsStore(string? directory = null) =>
        _directory = directory ?? ConfigStore.DefaultDirectory;

    public AppSettings Load() =>
        ConfigStore.Load<AppSettings>(_directory, AppSettings.FileName) ?? new AppSettings();

    public void Save(AppSettings settings) =>
        ConfigStore.Save(_directory, AppSettings.FileName, settings);
}

/// <summary>
/// 从已保存的设置解析 Inbox 路径：<see cref="AppSettings.InboxRoot"/> 为空时回退到默认
/// %LOCALAPPDATA%\WeChatBridge\Inbox。主程序与分享助手共用，确保两边落在同一个 Inbox。
/// </summary>
public static class InboxResolver
{
    public static InboxPaths Resolve() =>
        new InboxPaths(new AppSettingsStore().Load().InboxRoot);
}
