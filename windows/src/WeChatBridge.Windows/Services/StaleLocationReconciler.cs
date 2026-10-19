using System.IO;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Services;

/// <summary>
/// Repairs the absolute paths stored *inside* the config files.
/// <para>
/// Moving the data root is enough to invalidate them, and those paths are not
/// covered by <see cref="AppLayout"/>: <c>DeliveryFolderPath</c>,
/// <c>RecentCollectionFolders</c> and a custom target's
/// <c>BundleIdentifier</c> all name folders outside the data root, so they
/// travel to a new machine — or a new drive letter — as absolute strings and
/// arrive dangling. Every affected row then reports 「未安装」 and the only
/// repair is hand-editing JSON inside a ZIP.
/// </para>
/// <para>
/// Only runs in portable mode. An installed build's delivery folder going
/// missing usually means the user moved or deleted their own documents, and
/// silently discarding that choice would be worse than reporting it.
/// </para>
/// </summary>
internal sealed class StaleLocationReconciler
{
    private readonly AppSettings _settings;
    private readonly AppSettingsStore _settingsStore;
    private readonly ForwardTargetStore _targetStore;

    public StaleLocationReconciler(AppSettings settings, AppSettingsStore settingsStore, ForwardTargetStore targetStore)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _targetStore = targetStore;
    }

    /// <summary>What was dropped, so the UI can say why a setting moved.</summary>
    public bool Changed { get; private set; }

    public int DroppedTargets { get; private set; }

    public int DroppedFolders { get; private set; }

    public bool VaultRepointed { get; private set; }

    public void Run()
    {
        if (!AppLayout.Current.IsPortable)
            return;
        ReconcileVault();
        ReconcileFolders();
        ReconcileTargets();
    }

    /// <summary>
    /// The vault rides the drive, so a new drive letter should re-point rather
    /// than clear — otherwise the user's whole note history is forgotten by
    /// moving the USB stick one port to the left.
    /// </summary>
    private void ReconcileVault()
    {
        if (_settings.ObsidianVaultPath is not { } stored || Directory.Exists(stored))
            return;
        var vault = AppLayout.Current.Vault;
        if (vault is not null && Directory.Exists(vault))
        {
            _settings.ObsidianVaultPath = vault;
            VaultRepointed = true;
        }
        else
        {
            _settings.ObsidianVaultPath = null;
        }
        Changed = true;
    }

    private void ReconcileFolders()
    {
        if (_settings.DeliveryFolderPath is { } delivery && !Directory.Exists(delivery))
        {
            _settings.DeliveryFolderPath = null;
            Changed = true;
            DroppedFolders++;
        }
        var kept = _settings.RecentCollectionFolders.Where(Directory.Exists).ToList();
        if (kept.Count != _settings.RecentCollectionFolders.Count)
        {
            DroppedFolders += _settings.RecentCollectionFolders.Count - kept.Count;
            _settings.RecentCollectionFolders = kept;
            Changed = true;
        }
        if (Changed)
            _settingsStore.Save(_settings);
    }

    /// <summary>
    /// Drops custom targets whose target has gone. An AUMID is left alone:
    /// <c>ShareTargetResolver</c> resolves it at use time and a packaged app can
    /// be absent simply because this user has not installed it yet.
    /// </summary>
    private void ReconcileTargets()
    {
        var targets = _targetStore.Load();
        if (targets.Count == 0)
            return;
        var kept = targets.Where(t => TargetStillResolvable(t)).ToList();
        if (kept.Count == targets.Count)
            return;
        _targetStore.Save(kept);
        DroppedTargets = targets.Count - kept.Count;
        Changed = true;
    }

    private static bool TargetStillResolvable(ForwardTarget target)
    {
        var id = target.BundleIdentifier;
        if (!Path.IsPathRooted(id))
            return true;
        return File.Exists(id) || Directory.Exists(id);
    }
}
