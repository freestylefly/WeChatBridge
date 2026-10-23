using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Delivery;

namespace WeChatBridge.Windows.Services;

/// <summary>
/// Pill emphasis for skill rows. The app palette only ships neutral/live/warn,
/// so the macOS "bad" tone folds into <see cref="SkillTone.Warn"/>.
/// </summary>
public enum SkillTone
{
    Neutral,
    Live,
    Warn,
}

/// <summary>
/// One card in the skills list: the skill's metadata, how many scenes
/// reference it, and where its app-owned library copy stands. WeChatBridge
/// never installs into agents — the library SKILL.md is delivered through
/// the scene prompt's <c>{{skill:id}}</c> reference, so the card answers one
/// question: is the library copy usable.
/// </summary>
public sealed class SkillRow : INotifyPropertyChanged
{
    private bool _isExpanded;

    internal SkillRow(
        OfficialSkill skill,
        int sceneCount,
        SkillLibraryState libraryState,
        bool isUserSkill,
        string? libraryFile)
    {
        Skill = skill;
        SceneCount = sceneCount;
        LibraryState = libraryState;
        IsUserSkill = isUserSkill;
        LibraryFile = libraryFile;
    }

    public OfficialSkill Skill { get; }

    /// <summary>How many stored scenes reference this skill id (用于 N 个场景 badge).</summary>
    public int SceneCount { get; }

    /// <summary>The badge links to the scenes page only when something references the skill.</summary>
    public bool HasScenes => SceneCount > 0;

    /// <summary>Where the app-owned library copy stands — what path references point at.</summary>
    public SkillLibraryState LibraryState { get; }

    /// <summary>True for skills the user imported themselves — the card offers 移除 and a different badge.</summary>
    public bool IsUserSkill { get; }

    /// <summary>The source capsule text under the card title.</summary>
    public string OriginText => IsUserSkill ? L10n.Text("导入的技能") : L10n.Text("官方技能");

    /// <summary>Absolute SKILL.md path while the library copy is ready; the exact file scenes point at.</summary>
    public string? LibraryFile { get; }

    /// <summary>The inline reference a scene would use — shown in the details for copy.</summary>
    public string ReferenceToken => $"{{{{skill:{Skill.Id}}}}}";

    /// <summary>Library badge text; null hides it (a missing package already has its explainer).</summary>
    public string? LibraryText => LibraryState switch
    {
        SkillLibraryState.Ready => L10n.Text("技能库已就绪"),
        SkillLibraryState.Conflict => L10n.Text("技能库副本已被修改"),
        _ => null,
    };

    public bool LibraryConflict => LibraryState == SkillLibraryState.Conflict;

    /// <summary>Details open/closed — disclosure only; there is no card-level action.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
                return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }
    }

    /// <summary>Card glyph, mirroring SkillMark's symbol pick by skill id.</summary>
    public string Mark => Skill.Id.Contains("article", StringComparison.Ordinal) ? L10n.Text("文")
        : Skill.Id.Contains("video", StringComparison.Ordinal) ? "▶"
        : "◇";

    public string StatusTitle => LibraryState switch
    {
        SkillLibraryState.Ready => L10n.Text("已就绪"),
        SkillLibraryState.Conflict => L10n.Text("副本冲突"),
        _ => L10n.Text("缺技能包"),
    };

    public SkillTone Tone =>
        LibraryState == SkillLibraryState.Ready ? SkillTone.Live : SkillTone.Warn;

    /// <summary>Shows the explainer inside the details when no usable package exists.</summary>
    public bool PackageMissing => LibraryState == SkillLibraryState.Missing;

    /// <summary>The explainer wording — imported skills fail differently than unshipped ones.</summary>
    public string PackageMissingText => IsUserSkill
        ? L10n.Text("技能库中的副本不可用或被外部修改，重新导入压缩包可恢复。")
        : L10n.Text("技能包尚未随当前构建提供；场景仍可转发，提示词会要求 Agent 在不可用时说明未完成部分。");

    /// <summary>Search over id/name/summary, case-insensitive — mirrors scopedRecords.</summary>
    public bool MatchesQuery(string needle) =>
        needle.Length == 0
        || Skill.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || Skill.Summary.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || Skill.Id.Contains(needle, StringComparison.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// The pane's only dependency: loads the official skill catalog, merges in
/// user-imported library skills, and resolves <c>{{skill:id}}</c> references
/// for scene prompts — the port of macOS <c>SkillLibrary</c> minus the agent
/// install machinery, which the Windows port dropped: agents reach skills
/// through the library path written into the prompt.
///
/// <para>
/// Like <c>SceneService</c>, this file is compile-linked into the test assembly
/// and must stay free of WPF references (no <c>System.Windows</c>, no
/// Dispatcher). Async work hops through <see cref="Task.Run(Action)"/> and
/// resumes on the caller's context, so UI callers see property changes on the
/// UI thread without the file knowing what a dispatcher is.
/// </para>
/// </summary>
public sealed class SkillService : INotifyPropertyChanged
{
    /// <summary>How far above the app/current directory the resource hunt may climb.</summary>
    private const int MaxAncestorHops = 8;

    private readonly Func<IReadOnlyList<WeChatScene>> _scenes;

    /// <summary>
    /// User-imported skills, synthesized into catalog-shaped records on every
    /// reload so imported packages get the same card treatment as official
    /// ones. Keyed by id; never contains catalog ids.
    /// </summary>
    private readonly Dictionary<string, OfficialSkill> _userSkills = new(StringComparer.Ordinal);

    private OfficialSkillCatalog _catalog =
        new(OfficialSkillCatalog.CurrentSchemaVersion, []);
    private string? _loadError;
    private string? _libraryIssue;

    /// <param name="resourcesRoot">
    /// The directory containing <c>Skills/catalog.json</c>; null auto-detects
    /// via <see cref="FindResourcesRoot()"/>. Tests pass every path explicitly
    /// so nothing touches the real profile.
    /// </param>
    /// <param name="stateDirectory">Where the sandboxed library lives; null means the real profile.</param>
    /// <param name="scenes">Scene library read for the 用于 N 个场景 badge; null reads the store.</param>
    /// <param name="library">
    /// The app-owned skill library. Null uses the real profile location, or —
    /// when <paramref name="stateDirectory"/> is injected (tests) — Skills and
    /// SkillBackups folders beside it, so nothing escapes the sandbox.
    /// </param>
    public SkillService(
        string? resourcesRoot = null,
        string? stateDirectory = null,
        Func<IReadOnlyList<WeChatScene>>? scenes = null,
        SkillStore? library = null)
    {
        ResourcesRoot = resourcesRoot ?? FindResourcesRoot();
        _scenes = scenes ?? LoadScenes;
        Library = library ?? (stateDirectory is null
            ? new SkillStore()
            : new SkillStore(
                root: Path.Combine(stateDirectory, "Skills"),
                configDirectory: stateDirectory,
                backupRoot: Path.Combine(stateDirectory, "SkillBackups")));
        Reload();
    }

    /// <summary>Where <c>Skills/catalog.json</c> was found; null means the build shipped none.</summary>
    public string? ResourcesRoot { get; }

    /// <summary>The app-owned skill library scene prompts point agents at.</summary>
    public SkillStore Library { get; }

    /// <summary>Last library sync problem (a conflict or I/O failure); null when clean.</summary>
    public string? LibraryIssue
    {
        get => _libraryIssue;
        private set => Set(ref _libraryIssue, value);
    }

    /// <summary>One card per skill — official ones in catalog order, then imports.</summary>
    public ObservableCollection<SkillRow> Rows { get; } = [];

    /// <summary>Catalog load failure text; null when the catalog is healthy.</summary>
    public string? LoadError
    {
        get => _loadError;
        private set => Set(ref _loadError, value);
    }

    public int SkillCount => Rows.Count;

    /// <summary>How many cards are user imports rather than shipped skills.</summary>
    public int ImportedCount => _userSkills.Count;

    /// <summary>How many skills at least one scene references.</summary>
    public int ReferencedCount => Rows.Count(r => r.HasScenes);

    /// <summary>Re-reads the catalog and the library registry — mirrors SkillLibrary.reload.</summary>
    public void Reload()
    {
        if (ResourcesRoot is not { } root)
        {
            _catalog = new OfficialSkillCatalog(OfficialSkillCatalog.CurrentSchemaVersion, []);
            LoadError = L10n.Text("没有找到内置技能清单。");
        }
        else
        {
            try
            {
                _catalog = OfficialSkillCatalog.LoadFrom(root);
                LoadError = null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or JsonException or SkillException)
            {
                _catalog = new OfficialSkillCatalog(OfficialSkillCatalog.CurrentSchemaVersion, []);
                LoadError = error.Message;
            }
            SyncLibrary(root);
        }
        LoadUserSkills();

        var expanded = Rows.Where(r => r.IsExpanded).Select(r => r.Skill.Id).ToHashSet();
        var scenes = _scenes();
        Rows.Clear();
        foreach (var skill in _catalog.Skills.Concat(_userSkills.Values))
        {
            var row = new SkillRow(
                skill,
                scenes.Count(s => s.EffectiveSkillIDs().Contains(skill.Id)),
                Library.State(skill.Id),
                isUserSkill: _userSkills.ContainsKey(skill.Id),
                Library.SkillFile(skill.Id));
            row.IsExpanded = expanded.Contains(skill.Id);
            Rows.Add(row);
        }
        OnPropertyChanged(nameof(SkillCount));
        OnPropertyChanged(nameof(ImportedCount));
        OnPropertyChanged(nameof(ReferencedCount));
    }

    /// <summary>Every skill a scene can reference: catalog + imported, then stray library ids.</summary>
    public IReadOnlyList<(string Id, string Name)> ReferenceableSkills() =>
        _catalog.Skills.Concat(_userSkills.Values).Select(s => (s.Id, s.Name))
            .Concat(Library.Entries().Keys
                .Where(id => _catalog.Skills.All(s => s.Id != id)
                    && !_userSkills.ContainsKey(id))
                .OrderBy(id => id, StringComparer.Ordinal)
                .Select(id => (id, id)))
            .ToList();

    /// <summary>
    /// How <paramref name="id"/> reaches <paramref name="agent"/>: a pointer to
    /// the library's SKILL.md whenever a library copy exists — the prompt names
    /// the file for every destination, readable or not. A known skill with no
    /// library copy resolves as missing; an id nobody knows resolves as unknown.
    /// </summary>
    public SkillResolution Resolve(string id, AgentId? agent)
    {
        var skill = _catalog.Skills.FirstOrDefault(s => s.Id == id)
            ?? (_userSkills.TryGetValue(id, out var user) ? user : null);
        var file = Library.SkillFile(id);
        if (skill is null && file is null)
            return new SkillResolution(id, id, SkillRenderMode.Unknown);
        var name = skill?.Name ?? id;
        return file is not null
            ? new SkillResolution(id, name, SkillRenderMode.Path, file)
            : new SkillResolution(id, name, SkillRenderMode.Missing);
    }

    /// <summary>The render context the forward path and the scene preview hand to <see cref="ScenePrompt"/>.</summary>
    public SkillRenderContext PromptContext(AgentId? agent) => new(agent, id => Resolve(id, agent));

    /// <summary>
    /// 导入技能 — unpacks a user-picked zip into the library and reloads so
    /// the new card and scene references pick it up. Errors (corrupt archive,
    /// unsafe paths, invalid frontmatter, an id that an official skill already
    /// owns) surface as <see cref="SkillException"/> for the pane to
    /// display.
    /// </summary>
    public async Task<SkillPackageInfo> ImportArchiveAsync(string archivePath)
    {
        var info = await Task.Run(() =>
        {
            var (staging, parsed) = SkillArchive.ExtractToStaging(
                archivePath, Path.Combine(Path.GetTempPath(), "WeChatBridge"));
            try
            {
                if (_catalog.Skills.Any(s => s.Id == parsed.Id))
                    throw new SkillException(
                        L10n.Format($"「{parsed.Id}」是内置技能，请修改 SKILL.md 的 name 后再导入。"));
                Library.Import(staging, parsed.Id, parsed.Version);
                return parsed;
            }
            finally
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        });
        Reload();
        return info;
    }

    /// <summary>
    /// 从技能库移除 — the package moves into the backups and the card drops
    /// out. Only user-imported skills can be removed; official ones come back
    /// on the next catalog sync.
    /// </summary>
    public async Task RemoveSkillAsync(SkillRow row)
    {
        if (!row.IsUserSkill)
            throw new SkillException(L10n.Text("内置技能不能移除。"));
        await Task.Run(() => Library.Remove(row.Skill.Id));
        Reload();
    }

    /// <summary>
    /// The directory holding <c>Skills/catalog.json</c>: macOS walks up from the
    /// bundle's resource dir and the cwd, at most <see cref="MaxAncestorHops"/>
    /// levels. Windows does the same from the app base directory and the current
    /// directory, additionally accepting the repo layout where Skills sits
    /// under <c>Resources/</c> rather than at the root.
    /// </summary>
    public static string? FindResourcesRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            if (FindResourcesRoot(start) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>Single-start variant of <see cref="FindResourcesRoot()"/> — the testable seam.</summary>
    internal static string? FindResourcesRoot(string start)
    {
        var candidate = Path.GetFullPath(start);
        for (var hop = 0; hop < MaxAncestorHops; hop++)
        {
            if (File.Exists(Path.Combine(candidate, "Skills", "catalog.json")))
                return candidate;
            var nested = Path.Combine(candidate, "Resources");
            if (File.Exists(Path.Combine(nested, "Skills", "catalog.json")))
                return nested;
            var parent = Path.GetDirectoryName(candidate);
            if (parent is null || string.Equals(parent, candidate, StringComparison.Ordinal))
                break;
            candidate = parent;
        }
        return null;
    }

    /// <summary>
    /// Rebuilds <see cref="_userSkills"/> from the library registry: every
    /// imported id that still has a package on disk becomes a catalog-shaped
    /// record. Ids that a shipped catalog later claims stay official — the
    /// catalog wins.
    /// </summary>
    private void LoadUserSkills()
    {
        _userSkills.Clear();
        var catalogIds = _catalog.Skills.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in Library.Entries().Values)
        {
            if (entry.Source != SkillStore.ImportedSource
                || catalogIds.Contains(entry.Id)
                || Library.State(entry.Id) == SkillLibraryState.Missing)
                continue;
            var info = TryReadInfo(Library.SkillDirectory(entry.Id));
            _userSkills[entry.Id] = new OfficialSkill(
                entry.Id,
                info?.DisplayName ?? entry.Id,
                info?.Summary ?? "",
                entry.Version,
                Package: null,
                AgentIds.All);
        }
    }

    /// <summary>A library SKILL.md read for display metadata — failure just loses the pretty name.</summary>
    private static SkillPackageInfo? TryReadInfo(string directory)
    {
        try
        {
            return SkillArchive.ReadInfo(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or SkillException)
        {
            return null;
        }
    }

    /// <summary>
    /// Copies shipped official packages into the library. A failure here must
    /// not take the pane down — it is surfaced through <see cref="LibraryIssue"/>.
    /// </summary>
    private void SyncLibrary(string resourcesRoot)
    {
        try
        {
            var report = Library.SyncOfficial(_catalog, resourcesRoot);
            LibraryIssue = report.Conflicts.Count == 0
                ? null
                : L10n.Format($"技能库中的 {string.Join("、", report.Conflicts)} 被外部修改，未自动更新。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or SkillException)
        {
            LibraryIssue = error.Message;
        }
    }

    private static IReadOnlyList<WeChatScene> LoadScenes()
    {
        try
        {
            return new SceneStore().Load().Scenes;
        }
        catch
        {
            // A broken scenes.json must not take the skills pane with it.
            return [];
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
