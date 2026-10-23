using System.Globalization;
using static WeChatBridge.Windows.Core.SkillPackage;

namespace WeChatBridge.Windows.Core;

/// <summary>Where one skill stands inside the app-owned library.</summary>
public enum SkillLibraryState
{
    /// <summary>Not in the library (no package shipped, or never synced).</summary>
    Missing,

    /// <summary>Present and byte-identical to what the library last wrote.</summary>
    Ready,

    /// <summary>Present but edited outside the app, or placed there by hand — never overwritten.</summary>
    Conflict,
}

/// <summary>One library record: what was written, from where, and its digest.</summary>
public sealed record SkillLibraryEntry(
    string Id,
    string Version,
    string Digest,
    string Source,
    DateTimeOffset SyncedAt);

/// <summary>What one <see cref="SkillStore.SyncOfficial"/> pass changed.</summary>
public sealed record SkillSyncReport(int Added, int Updated, IReadOnlyList<string> Conflicts)
{
    public static readonly SkillSyncReport Empty = new(0, 0, []);
}

/// <summary>
/// The app-owned skill library — the single source of truth for every skill
/// WeChatBridge manages. Packages live under
/// <c>%LOCALAPPDATA%\WeChatBridge\Skills\&lt;id&gt;\</c> (the directory name is
/// the skill id, which is also the SKILL.md <c>name</c>), the registry in
/// <c>Config\skill-library.json</c>, and replaced versions in
/// <c>SkillBackups\</c>. Scene prompts point agents at these files; agent
/// skill directories are only deployment targets.
///
/// Safety mirrors the macOS installer: a directory the registry does
/// not know, or whose digest no longer matches, is reported as a conflict and
/// left untouched.
/// </summary>
public sealed class SkillStore
{
    public const string RegistryFileName = "skill-library.json";
    public const string OfficialSource = "official";

    /// <summary>Registry source for user-imported packages — not touched by official sync.</summary>
    public const string ImportedSource = "import";

    public const int MaxBackups = 20;

    private const string BackupStampFormat = "yyyyMMddHHmmssfff";

    private readonly string _configDirectory;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="root">Library directory; null means %LOCALAPPDATA%\WeChatBridge\Skills.</param>
    /// <param name="configDirectory">Where the registry lives; null means <see cref="ConfigStore.DefaultDirectory"/>.</param>
    /// <param name="backupRoot">Backup directory; null means a SkillBackups sibling of <paramref name="root"/>.</param>
    /// <param name="clock">Timestamp source for registry entries and backup names.</param>
    public SkillStore(
        string? root = null,
        string? configDirectory = null,
        string? backupRoot = null,
        Func<DateTimeOffset>? clock = null)
    {
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WeChatBridge");
        Root = Path.GetFullPath(root ?? Path.Combine(appData, "Skills"));
        BackupRoot = Path.GetFullPath(backupRoot
            ?? Path.Combine(Path.GetDirectoryName(Root) ?? appData, "SkillBackups"));
        _configDirectory = configDirectory ?? ConfigStore.DefaultDirectory;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Root { get; }

    public string BackupRoot { get; }

    public string SkillDirectory(string id) => Path.Combine(Root, id);

    public IReadOnlyDictionary<string, SkillLibraryEntry> Entries() => Registry();

    public SkillLibraryState State(string id)
    {
        if (!SkillId.IsValid(id))
            return SkillLibraryState.Missing;
        var directory = SkillDirectory(id);
        if (!Directory.Exists(directory) && !File.Exists(directory))
            return SkillLibraryState.Missing;
        if (!Registry().TryGetValue(id, out var entry))
            return SkillLibraryState.Conflict;
        try
        {
            return File.Exists(Path.Combine(directory, "SKILL.md")) && PackageDigest(directory) == entry.Digest
                ? SkillLibraryState.Ready
                : SkillLibraryState.Conflict;
        }
        catch (Exception error) when (IsFileSystemError(error) || error is SkillException)
        {
            return SkillLibraryState.Conflict;
        }
    }

    /// <summary>Absolute path of the skill's SKILL.md when the library copy is ready; otherwise null.</summary>
    public string? SkillFile(string id) =>
        State(id) == SkillLibraryState.Ready ? Path.Combine(SkillDirectory(id), "SKILL.md") : null;

    /// <summary>
    /// Brings every shipped official package into the library: missing ones
    /// are copied in, a newer bundled version replaces an untouched library copy
    /// (after a backup), and anything edited outside the app is reported, not
    /// overwritten. Skills without a package are skipped.
    /// </summary>
    public SkillSyncReport SyncOfficial(OfficialSkillCatalog catalog, string resourcesRoot)
    {
        var registry = Registry();
        var added = 0;
        var updated = 0;
        var conflicts = new List<string>();

        foreach (var skill in catalog.Skills)
        {
            if (skill.Package is not { } package || !SkillId.IsValid(skill.Id))
                continue;
            var source = Path.Combine(resourcesRoot, "Skills", package);
            if (!File.Exists(Path.Combine(source, "SKILL.md")))
                continue;

            var sourceDigest = ValidatePackage(source);
            var target = SkillDirectory(skill.Id);
            var exists = Directory.Exists(target) || File.Exists(target);

            if (!exists)
            {
                Write(source, target, backupAs: null);
                registry[skill.Id] = Entry(skill, sourceDigest);
                added++;
                continue;
            }

            if (State(skill.Id) != SkillLibraryState.Ready || !registry.TryGetValue(skill.Id, out var entry))
            {
                conflicts.Add(skill.Id);
                continue;
            }

            if (SceneVersion.Parse(entry.Version) is { } current
                && SceneVersion.Parse(skill.Version) is { } bundled
                && bundled > current)
            {
                Write(source, target, backupAs: $"{skill.Id}-{entry.Version}");
                registry[skill.Id] = Entry(skill, sourceDigest);
                updated++;
            }
        }

        if (added + updated > 0)
            SaveRegistry(registry);
        if (updated > 0)
            PruneBackups();
        return new SkillSyncReport(added, updated, conflicts);
    }

    /// <summary>
    /// Registers a user-supplied package directory (already extracted and
    /// validated by <see cref="SkillArchive"/>). Whatever currently occupies
    /// the slot — a ready copy, a conflicted one or a foreign directory — is
    /// moved into the backups first, so an explicit import never destroys
    /// bytes; a byte-identical re-import is a no-op that refreshes the stamp.
    /// </summary>
    public SkillLibraryEntry Import(string source, string id, string version)
    {
        var digest = ValidatePackage(source);
        var registry = Registry();
        var target = SkillDirectory(id);
        var exists = PathExists(target);

        if (exists
            && registry.TryGetValue(id, out var current)
            && State(id) == SkillLibraryState.Ready
            && current.Digest == digest)
        {
            var refreshed = current with { SyncedAt = _clock() };
            registry[id] = refreshed;
            SaveRegistry(registry);
            return refreshed;
        }

        Write(source, target,
            backupAs: exists
                ? $"{id}-{(registry.TryGetValue(id, out var old) ? old.Version : "replaced")}"
                : null);
        var entry = new SkillLibraryEntry(id, version, digest, ImportedSource, _clock());
        registry[id] = entry;
        SaveRegistry(registry);
        if (exists)
            PruneBackups();
        return entry;
    }

    /// <summary>
    /// Removes a library copy: the directory moves into the backups (still
    /// recoverable) and the registry entry is dropped. Returns false when the
    /// id was neither on disk nor registered.
    /// </summary>
    public bool Remove(string id)
    {
        var target = SkillDirectory(id);
        var found = PathExists(target);
        if (found)
        {
            Directory.CreateDirectory(BackupRoot);
            MoveAny(target, Path.Combine(BackupRoot,
                $"{id}-removed-{_clock().ToString(BackupStampFormat, CultureInfo.InvariantCulture)}"));
            PruneBackups();
        }
        var registry = Registry();
        var removed = registry.Remove(id);
        if (removed)
            SaveRegistry(registry);
        return found || removed;
    }

    private SkillLibraryEntry Entry(OfficialSkill skill, string digest) =>
        new(skill.Id, skill.Version, digest, OfficialSource, _clock());

    private Dictionary<string, SkillLibraryEntry> Registry() =>
        ConfigStore.Load<Dictionary<string, SkillLibraryEntry>>(_configDirectory, RegistryFileName) is { } stored
            ? new Dictionary<string, SkillLibraryEntry>(stored, StringComparer.Ordinal)
            : new Dictionary<string, SkillLibraryEntry>(StringComparer.Ordinal);

    private void SaveRegistry(Dictionary<string, SkillLibraryEntry> registry) =>
        ConfigStore.Save(_configDirectory, RegistryFileName,
            new SortedDictionary<string, SkillLibraryEntry>(registry, StringComparer.Ordinal));

    /// <summary>
    /// Staging copy, then an all-or-nothing swap: the target path only ever
    /// holds a complete package. The replaced copy moves into the backups.
    /// </summary>
    private void Write(string source, string target, string? backupAs)
    {
        Directory.CreateDirectory(Root);
        var id = Path.GetFileName(target);
        var staging = Path.Combine(Root, $".{id}.staging-{Guid.NewGuid():N}");
        try
        {
            CopyPackage(source, staging);
            if (backupAs is null)
            {
                Directory.Move(staging, target);
                return;
            }
            Directory.CreateDirectory(BackupRoot);
            var backup = Path.Combine(BackupRoot,
                $"{backupAs}-{_clock().ToString(BackupStampFormat, CultureInfo.InvariantCulture)}");
            MoveAny(target, backup);
            try
            {
                Directory.Move(staging, target);
            }
            catch
            {
                MoveAny(backup, target);
                throw;
            }
        }
        finally
        {
            try { DeleteAny(staging); } catch (Exception error) when (IsFileSystemError(error)) { }
        }
    }

    /// <summary>Keeps the newest <see cref="MaxBackups"/> backups, ordered by their name stamp.</summary>
    private void PruneBackups()
    {
        if (!Directory.Exists(BackupRoot))
            return;
        var stale = Directory.EnumerateDirectories(BackupRoot)
            .OrderByDescending(path => Path.GetFileName(path).Split('-')[^1], StringComparer.Ordinal)
            .Skip(MaxBackups);
        foreach (var path in stale)
        {
            try { DeleteAny(path); } catch (Exception error) when (IsFileSystemError(error)) { }
        }
    }
}
