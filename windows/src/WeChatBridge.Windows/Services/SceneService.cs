using System.IO;
using System.Text.Json;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Services;

/// <summary>
/// The app-side half of the scene system: owns the Config-directory stores,
/// wraps <see cref="SceneCoordinator"/> for the share path, and holds the
/// library rules the 场景 pane needs. Ported from the macOS
/// <c>ActionRunner</c> scene wiring, <c>SceneShortcutController</c> and the
/// mutating half of <c>SceneSettingsView</c>.
///
/// Every UI seam is a delegate so the same file drives on fakes in tests —
/// the file is compile-linked into the test assembly and must stay free of
/// WPF references (no <c>System.Windows</c>, no <c>Dispatcher</c>, no window
/// types):
///
/// <list type="bullet">
///   <item>The picker is the Core <see cref="ScenePickerHandler"/>. The WPF
///   host wires it as
///   <c>(scenes, ct) =&gt; ScenePickerWindow.ChooseAsync(scenes, ct)</c>,
///   marshalled onto the UI dispatcher if the caller is not already on it —
///   <c>ChooseAsync</c> creates a <c>Window</c> and is therefore UI-thread
///   bound. With no picker installed, questions answer themselves as
///   「直接转发」, so a share is never held hostage by an invisible panel.</item>
///   <item>The WeChat window title read defaults to
///   <see cref="WeChatWindowTitleReader.TryRead"/> on a worker thread — window
///   titles only, no screen capture, matching the macOS Accessibility path's
///   privacy boundary. Tests inject a stub.</item>
///   <item>Batch insights (senders + transcript end) default to
///   <see cref="WeChatBatchInsightsReader.Read"/> on a worker thread.</item>
///   <item>Shortcut toasts arrive through <c>notify</c>, e.g.
///   <c>model.ShowToast</c>.</item>
///   <item>Global hotkeys arrive as an <see cref="ISceneHotkeySource"/> — the
///   app passes <see cref="WindowsSceneHotkeySource"/>; tests pass null.</item>
/// </list>
///
/// Threading: every member is safe to call off the UI thread (the stores are
/// file-backed and the coordinator serializes internally). <see cref="Changed"/>
/// is raised on the caller's thread; UI subscribers must marshal themselves.
/// </summary>
public sealed class SceneService : IDisposable
{
    /// <summary>
    /// The decision the forward path consumes, ported from the macOS
    /// <c>SceneCoordinator.Selection</c> usage in <c>ActionRunner.forward</c>.
    /// </summary>
    public sealed record SceneChoice
    {
        /// <summary>The raw coordinator selection; kept whole for <see cref="SceneService.CompleteForward"/>.</summary>
        public required SceneCoordinator.Selection Selection { get; init; }

        /// <summary>The scene to attach, or null when the share goes out bare.</summary>
        public WeChatScene? Scene => Selection.Scene;

        /// <summary>The chat/group name as bound or read off the WeChat window title.</summary>
        public string? GroupName => Selection.GroupName;

        /// <summary>Where the previous summary for this group ended — the 续聊 boundary.</summary>
        public DateTimeOffset? PreviousSummaryAt => Selection.PreviousSummaryAt;

        /// <summary>The newest message timestamp seen in this batch.</summary>
        public DateTimeOffset? BatchEnd => Selection.Insights.End;
    }

    /// <summary>
    /// The coordinator's two IO reads started ahead of time. Kicking them off
    /// while the user is still reading the entry picker — or while a previous
    /// forward is still delivering — is what keeps the scene picker appearing
    /// instantly after the pick instead of a second wait. Both tasks already
    /// degrade to empty results; they never fault.
    /// </summary>
    public sealed class ShareContextPrefetch
    {
        internal Task<GroupTitleParser.Title?>? TitleTask { get; init; }
        internal Task<WeChatBatchInsights>? InsightsTask { get; init; }
    }

    /// <summary>One scene-package import attempt's tally.</summary>
    public sealed record SceneImportReport
    {
        /// <summary>Packages applied to the library.</summary>
        public int Imported { get; init; }

        /// <summary>Same-version packages the user declined to overwrite.</summary>
        public int Declined { get; init; }

        /// <summary>
        /// Packages older than the installed scene — macOS
        /// 「已安装的场景版本更新，未导入较旧版本。」.
        /// </summary>
        public int SkippedOlder { get; init; }

        /// <summary>
        /// Validation/read failures. A non-empty list means the import stopped
        /// there, mirroring the macOS first-error abort.
        /// </summary>
        public IReadOnlyList<string> Errors { get; init; } = [];
    }

    /// <summary>A scene package that failed validation, with a user-readable reason.</summary>
    public sealed class ScenePackageException(string message) : Exception(message);

    /// <summary>
    /// Package files use the shared camelCase/indented options — the same shape
    /// <c>ScenePackage.encoder()</c> writes on macOS (property order matches the
    /// declaration order, which mirrors the CodingKeys order), so a package
    /// exported on one platform imports on the other unchanged.
    /// </summary>
    private static readonly JsonSerializerOptions PackageJsonOptions = BatchManifest.JsonOptions;

    private readonly ScenePickerHandler? _picker;
    private readonly Func<CancellationToken, Task<GroupTitleParser.Title?>> _titleReader;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<WeChatBatchInsights>> _insightsReader;

    /// <param name="configDirectory">
    /// The Config directory the three stores share; null means
    /// <see cref="ConfigStore.DefaultDirectory"/>. Tests pass a temp directory.
    /// </param>
    /// <param name="picker">The scene picker; null answers every prompt as 「直接转发」.</param>
    /// <param name="titleReader">Group-title read; null reads the real WeChat windows.</param>
    /// <param name="insightsReader">Transcript insights read; null parses the share ZIPs.</param>
    /// <param name="hotkeys">
    /// Global Ctrl+Alt+1–9 source. Null leaves
    /// <see cref="SceneShortcutController.HandleSceneDigit"/> reachable but no
    /// keys registered — tests and headless launches use that.
    /// </param>
    /// <param name="notify">Toast text hook for hotkey picks.</param>
    public SceneService(
        string? configDirectory = null,
        ScenePickerHandler? picker = null,
        Func<CancellationToken, Task<GroupTitleParser.Title?>>? titleReader = null,
        Func<IReadOnlyList<string>, CancellationToken, Task<WeChatBatchInsights>>? insightsReader = null,
        ISceneHotkeySource? hotkeys = null,
        Action<string>? notify = null)
    {
        Scenes = new SceneStore(configDirectory);
        Memories = new GroupMemoryStore(configDirectory);
        Pending = new NextForwardSceneStore(configDirectory);
        _picker = picker;

        var windowTitles = new WeChatWindowTitleReader();
        _titleReader = titleReader
            ?? (cancellationToken => Task.Run(
                () => windowTitles.TryRead(), cancellationToken));
        _insightsReader = insightsReader
            ?? ((paths, cancellationToken) => Task.Run(
                () => WeChatBatchInsightsReader.Read(paths, cancellationToken), cancellationToken));

        Shortcuts = new SceneShortcutController(
            () => Scenes.Load().EnabledScenes, Pending, hotkeys, notify);
    }

    /// <summary>The scene library store: <c>scenes.json</c>.</summary>
    public SceneStore Scenes { get; }

    /// <summary>Group bindings and sender fingerprints: <c>group-memories.json</c>.</summary>
    public GroupMemoryStore Memories { get; }

    /// <summary>The ⌃⌥-equivalent pending selection: <c>pending-scene.json</c>.</summary>
    public NextForwardSceneStore Pending { get; }

    /// <summary>
    /// The Ctrl+Alt+1–9 controller. The digit picks the scene for the *next*
    /// forward only, exactly like the macOS ⌃⌥ shortcuts.
    /// </summary>
    public SceneShortcutController Shortcuts { get; }

    /// <summary>
    /// Raised after anything this service writes — scene edits, learned group
    /// bindings, memory advances. Panes subscribe to rebuild their lists. Raised
    /// on whichever thread performed the write.
    /// </summary>
    public event Action? Changed;

    public void Dispose() => Shortcuts.Dispose();

    // MARK: - Share path (the integrator's seam)

    /// <summary>
    /// Starts the title read and transcript-insights parse early — call this
    /// the moment a fresh share's intent is consumed, then hand the result to
    /// <see cref="ResolveForShareAsync"/>. The reads are the same ones the
    /// coordinator would run; prefetch only changes *when* they run. A null
    /// <paramref name="filePaths"/> skips insights, matching the resolver.
    /// </summary>
    public ShareContextPrefetch PrefetchForShare(
        IReadOnlyList<string>? filePaths,
        bool captureTitle)
    {
        return new ShareContextPrefetch
        {
            TitleTask = captureTitle ? SafeTitle() : null,
            InsightsTask = filePaths is not null && Scenes.Load().EnabledScenes.Count > 0
                ? SafeInsights(filePaths)
                : null,
        };

        async Task<GroupTitleParser.Title?> SafeTitle()
        {
            try { return await _titleReader(CancellationToken.None); }
            catch { return null; }
        }

        async Task<WeChatBatchInsights> SafeInsights(IReadOnlyList<string> paths)
        {
            try { return await _insightsReader(paths, CancellationToken.None); }
            catch { return WeChatBatchInsights.Empty; }
        }
    }

    /// <summary>
    /// The per-share decision the forward path calls before delivering — the
    /// Windows port of <c>ActionRunner.handle</c>'s
    /// <c>sceneCoordinator.prepare</c> call. Reads the WeChat window title when
    /// <paramref name="captureTitle"/> is set (fresh share arrivals only — a
    /// resend must not relabel the batch with whatever chat is open now), runs
    /// pending-shortcut → binding/fingerprint → picker → optional default, and
    /// returns null when the picker let the share expire: the integrator records
    /// the batch as expired in that case, exactly as <c>recordExpired</c> does on
    /// macOS.
    /// </summary>
    /// <param name="filePaths">The batch's files, used for sender fingerprints
    /// and the summary watermark. Null skips the transcript read.</param>
    /// <param name="groupName">A group name the caller already knows (rare).</param>
    /// <param name="allowDefault">Whether the configured default scene may
    /// answer last; macOS share arrivals pass false.</param>
    /// <param name="prefetch">Reads already in flight from
    /// <see cref="PrefetchForShare"/>; when supplied, the coordinator awaits
    /// those instead of starting its own.</param>
    /// <param name="resolveScenes">False makes this a title read only — the
    /// batch's 群名 snapshot for a destination (Obsidian) that takes notes,
    /// not scenes. No bindings, fingerprints, picker, or default scene.</param>
    public async Task<SceneChoice?> ResolveForShareAsync(
        IReadOnlyList<string>? filePaths = null,
        string? groupName = null,
        bool captureTitle = true,
        bool allowDefault = false,
        ShareContextPrefetch? prefetch = null,
        bool resolveScenes = true,
        CancellationToken cancellationToken = default)
    {
        // macOS passes enabled = !enabledScenes.isEmpty — the whole pipeline
        // (picker included) is skipped when nothing is enabled, while the title
        // read still runs for the batch's 群名 snapshot.
        var enabled = resolveScenes && Scenes.Load().EnabledScenes.Count > 0;
        var answer = await CoordinatorFor(filePaths, prefetch).PrepareAsync(
            enabled, groupName, captureTitle, allowDefault, cancellationToken);
        if (answer.Kind != SceneCoordinator.AnswerKind.Ready || answer.Selection is not { } selection)
            return null;
        if (selection.GroupName is not null)
            RaiseChanged(); // a picker answer may have just learned a binding
        return new SceneChoice { Selection = selection };
    }

    /// <summary>
    /// Records what a delivered share taught us about its group — last scene,
    /// summary watermark, sender fingerprint. Call only after the forward
    /// actually landed, the same place macOS calls <c>sceneCoordinator.advance</c>.
    /// </summary>
    public void CompleteForward(SceneChoice choice)
    {
        CoordinatorFor(filePaths: null).Advance(choice.Selection);
        RaiseChanged();
    }

    public async Task<SceneChoice> ExplicitCollectionChoiceAsync(ReadyBatch batch, WeChatScene? scene)
    {
        var insights = scene is null ? WeChatBatchInsights.Empty
            : await _insightsReader(batch.Items.Select(i => i.FullPath).ToList(), CancellationToken.None);
        var memory = batch.ChatName is { } name ? Memories.Load().GetValueOrDefault(GroupName.Normalize(name)) : null;
        return new SceneChoice { Selection = new SceneCoordinator.Selection
        {
            GroupName = batch.ChatName, Scenes = scene is null ? [] : [scene],
            Insights = insights, PreviousSummaryAt = memory?.LastSummaryAt,
        } };
    }

    /// <summary>
    /// The prompt text pasted ahead of the files, or null. Ported from
    /// <c>ActionRunner.forward</c>: a scene that does not list the current agent
    /// in <see cref="WeChatScene.CompatibleAgents"/> is skipped here rather than
    /// at selection time — the batch still records which scene was chosen.
    /// </summary>
    /// <param name="agent">The destination's agent, via
    /// <see cref="AgentIds.Matching"/>; null for destinations without one
    /// (clipboard, Obsidian, most custom targets) attaches the prompt as-is.</param>
    /// <param name="skills">Resolves inline <c>{{skill:id}}</c> references and
    /// the 技能要求 section for this destination; null names skills by id.</param>
    public string? RenderPrompt(
        SceneChoice choice,
        AgentId? agent,
        SkillRenderContext? skills = null)
    {
        if (choice.Scene is not { } scene)
            return null;
        if (agent is { } id && !scene.CompatibleAgents.Contains(id))
            return null;
        return ScenePrompt.Render(
            scene, choice.PreviousSummaryAt, choice.BatchEnd, skills);
    }

    // MARK: - Library management (the pane's verbs)

    /// <summary>The full library, freshly loaded — the pane owns no cached truth.</summary>
    public SceneSettings LoadSettings() => Scenes.Load();

    /// <summary>新建 — an enabled blank scene named 新场景, returned for selection.</summary>
    public WeChatScene AddScene()
    {
        var settings = Scenes.Load();
        var scene = new WeChatScene { Name = L10n.Text("新场景"), Enabled = true };
        settings.Add(scene);
        Scenes.Save(settings);
        RaiseChanged();
        return scene;
    }

    /// <summary>复制为我的场景 — an enabled, editable copy of any scene.</summary>
    public WeChatScene? DuplicateScene(string sceneId)
    {
        var settings = Scenes.Load();
        var source = settings.Scenes.FirstOrDefault(s => s.Id == sceneId);
        if (source is null)
            return null;
        var copy = settings.CopiedAsUserTask(source);
        settings.Add(copy);
        Scenes.Save(settings);
        RaiseChanged();
        return copy;
    }

    /// <summary>
    /// Field edits (name, summary, prompt, compatible agents). The stored
    /// <see cref="WeChatScene.Enabled"/> switch is preserved — enabling is
    /// <see cref="SetSceneEnabled"/>'s job, matching the separate macOS toggle.
    /// </summary>
    public void UpdateScene(WeChatScene edited)
    {
        var settings = Scenes.Load();
        if (settings.Scenes.All(s => s.Id != edited.Id))
            return;
        // User scenes declare skills by referencing them inline; the stored list
        // follows the prompt so removing a {{skill:id}} drops the dependency.
        if (!edited.IsOfficial)
        {
            edited.RequiredSkillIDs = SkillReference.Parse(edited.Instruction)
                .Concat(SkillReference.Parse(edited.OutputSpec))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        settings.Replace(edited);
        Scenes.Save(settings);
        RaiseChanged();
    }

    /// <summary>
    /// The 启用 switch. Turning a scene off also drops it as default and from
    /// every group binding — a disabled scene must stop answering shortcuts
    /// and resolutions immediately, as the macOS <c>enabledBinding</c> does.
    /// </summary>
    public void SetSceneEnabled(string sceneId, bool enabled)
    {
        var settings = Scenes.Load();
        var index = settings.Scenes.FindIndex(s => s.Id == sceneId);
        if (index < 0 || settings.Scenes[index].Enabled == enabled)
            return;
        settings.Scenes[index].Enabled = enabled;
        if (!enabled && settings.DefaultSceneID == sceneId)
            settings.DefaultSceneID = null;
        Scenes.Save(settings);
        if (!enabled)
            RemoveSceneFromBindings(sceneId);
        RaiseChanged();
    }

    /// <summary>上移 / 下移 — swaps the scene with its neighbour in library order.</summary>
    public void MoveScene(string sceneId, int offset)
    {
        var settings = Scenes.Load();
        var index = settings.Scenes.FindIndex(s => s.Id == sceneId);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= settings.Scenes.Count)
            return;
        (settings.Scenes[target], settings.Scenes[index]) =
            (settings.Scenes[index], settings.Scenes[target]);
        Scenes.Save(settings);
        RaiseChanged();
    }

    /// <summary>
    /// 删除场景 — removes the scene, every group binding to it, and the
    /// lastSceneID marker that would otherwise resurrect it as a resolution.
    /// </summary>
    public void RemoveScene(string sceneId)
    {
        var settings = Scenes.Load();
        settings.Remove(sceneId);
        Scenes.Save(settings);
        var memories = Memories.Load();
        var touched = false;
        foreach (var memory in memories.Values)
        {
            if (memory.LastSceneID == sceneId)
            {
                memory.LastSceneID = null;
                touched = true;
            }
        }
        if (touched)
            Memories.Save(memories);
        RemoveSceneFromBindings(sceneId);
        RaiseChanged();
    }

    /// <summary>
    /// 设为默认场景 / 取消默认场景. Null clears; any other id must name an
    /// enabled scene or the call is ignored — the resolver only falls back to
    /// scenes that are actually enabled.
    /// </summary>
    public void SetDefaultScene(string? sceneId)
    {
        var settings = Scenes.Load();
        if (sceneId is not null && settings.Scene(sceneId) is null)
            return;
        if (settings.DefaultSceneID == sceneId)
            return;
        settings.DefaultSceneID = sceneId;
        Scenes.Save(settings);
        RaiseChanged();
    }

    /// <summary>
    /// The forward-attachment switch macOS persists in the same blob. The macOS
    /// flow keys the pipeline off "any enabled scene", so this flag is kept for
    /// file-contract parity rather than gating resolution.
    /// </summary>
    public bool AttachToForwards
    {
        get => Scenes.Load().AttachToForwards;
        set
        {
            var settings = Scenes.Load();
            if (settings.AttachToForwards == value)
                return;
            settings.AttachToForwards = value;
            Scenes.Save(settings);
            RaiseChanged();
        }
    }

    /// <summary>Every remembered group, keyed by normalized name.</summary>
    public IReadOnlyDictionary<string, GroupMemory> LoadMemories() => Memories.Load();

    /// <summary>
    /// One checkbox in the group editor — ported from <c>toggleBinding</c>: the
    /// pick toggles, then the list is rewritten in library order keeping only
    /// enabled scenes, so binding order can never disagree with the resolver.
    /// </summary>
    public void ToggleGroupBinding(string groupKey, string sceneId)
    {
        var memories = Memories.Load();
        if (!memories.TryGetValue(groupKey, out var memory))
            return;
        if (!memory.BoundSceneIDs.Remove(sceneId))
            memory.BoundSceneIDs.Add(sceneId);
        var selected = memory.BoundSceneIDs.ToHashSet(StringComparer.Ordinal);
        memory.BoundSceneIDs = Scenes.Load().Scenes
            .Where(s => s.Enabled && selected.Contains(s.Id))
            .Select(s => s.Id)
            .ToList();
        memory.UpdatedAt = DateTimeOffset.UtcNow;
        memories[groupKey] = memory;
        Memories.Save(memories);
        RaiseChanged();
    }

    /// <summary>取消绑定 — the group asks on every share again.</summary>
    public void ClearGroupBinding(string groupKey)
    {
        var memories = Memories.Load();
        if (!memories.TryGetValue(groupKey, out var memory) || memory.BoundSceneIDs.Count == 0)
            return;
        memory.BoundSceneIDs = [];
        memory.UpdatedAt = DateTimeOffset.UtcNow;
        Memories.Save(memories);
        RaiseChanged();
    }

    /// <summary>
    /// The Ctrl+Alt+digit a scene answers to, by position among enabled scenes —
    /// null for disabled scenes and anything past the ninth.
    /// </summary>
    public string? ShortcutHint(WeChatScene scene)
    {
        var index = Scenes.Load().EnabledScenes.FindIndex(s => s.Id == scene.Id);
        return index is >= 0 and < 9 ? $"Ctrl+Alt+{index + 1}" : null;
    }

    // MARK: - Scene packages

    /// <summary>
    /// The distributable JSON for one scene, ported from <c>export</c>. Throws
    /// <see cref="ScenePackageException"/> when the version is not dotted
    /// numbers — the pane surfaces that message directly.
    /// </summary>
    public string ExportPackageJson(WeChatScene scene)
    {
        if (SceneVersion.Parse(scene.PackageVersion) is null)
            throw new ScenePackageException(L10n.Text("版本号必须是 1.0.0 这样的数字格式。"));
        return JsonSerializer.Serialize(ScenePackage.FromScene(scene), PackageJsonOptions);
    }

    /// <summary>
    /// 导入 JSON — parses every file first (one bad file aborts the whole
    /// import, like macOS), then applies the version rule: newer wins, older is
    /// skipped, same version asks <paramref name="confirmOverwrite"/> — the pane
    /// supplies the 「场景版本已存在」 dialog. Enabled state and group bindings
    /// survive an overwrite because <see cref="SceneSettings.Replace"/> keeps
    /// the stored switch.
    /// </summary>
    public SceneImportReport ImportScenePackages(
        IEnumerable<string> paths,
        Func<ScenePackage, bool>? confirmOverwrite = null)
    {
        var packages = new List<ScenePackage>();
        var errors = new List<string>();
        foreach (var path in paths)
        {
            if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
                continue;
            ScenePackage? package;
            try
            {
                package = JsonSerializer.Deserialize<ScenePackage>(
                    File.ReadAllText(path), PackageJsonOptions);
            }
            catch (Exception error) when (error is JsonException
                or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                errors.Add(error.Message);
                return Report(0, errors);
            }
            if (package is null)
            {
                errors.Add(L10n.Text("场景包缺少 id、名称、版本、指令或版本格式无效。"));
                return Report(0, errors);
            }
            try
            {
                ValidatePackage(package);
            }
            catch (ScenePackageException error)
            {
                errors.Add(error.Message);
                return Report(0, errors);
            }
            packages.Add(package);
        }
        if (packages.Count == 0)
        {
            if (errors.Count == 0)
                errors.Add(L10n.Text("没有找到可导入的场景包。"));
            return Report(0, errors);
        }

        var settings = Scenes.Load();
        var imported = 0;
        var declined = 0;
        var skippedOlder = 0;
        foreach (var package in packages)
        {
            var localVersion = SceneVersion.Parse(
                settings.Scenes.FirstOrDefault(s => s.Id == package.Id)?.PackageVersion);
            var incoming = SceneVersion.Parse(package.Version)!.Value;
            if (localVersion is { } local && incoming.CompareTo(local) < 0)
            {
                skippedOlder++;
                continue;
            }
            if (localVersion is { } same && incoming.CompareTo(same) == 0
                && confirmOverwrite?.Invoke(package) != true)
            {
                declined++;
                continue;
            }
            var scene = package.ToScene();
            SkillId.Migrate(scene);
            scene.Enabled = true; // a new import starts enabled; Replace keeps a stored switch
            settings.Replace(scene);
            imported++;
        }
        if (imported > 0)
        {
            Scenes.Save(settings);
            RaiseChanged();
        }
        return new SceneImportReport
        {
            Imported = imported,
            Declined = declined,
            SkippedOlder = skippedOlder,
            Errors = errors,
        };

        SceneImportReport Report(int importedCount, List<string> failureList) =>
            new() { Imported = importedCount, Errors = failureList };
    }

    private static void ValidatePackage(ScenePackage package)
    {
        if (package.SchemaVersion is < 1 or > ScenePackage.CurrentSchemaVersion)
            throw new ScenePackageException(L10n.Text("这个场景包由更新版本生成，当前微信流无法导入。"));
        if (string.IsNullOrWhiteSpace(package.Id)
            || string.IsNullOrWhiteSpace(package.Name)
            || string.IsNullOrWhiteSpace(package.Instruction)
            || SceneVersion.Parse(package.Version) is null)
            throw new ScenePackageException(L10n.Text("场景包缺少 id、名称、版本、指令或版本格式无效。"));
    }

    /// <summary>
    /// The coordinator is per-call: the transcript reader is bound to this
    /// share's file list, while stores, picker and title reader are fixed.
    /// A prefetch swaps the two reader delegates for the already-running tasks.
    /// </summary>
    private SceneCoordinator CoordinatorFor(
        IReadOnlyList<string>? filePaths,
        ShareContextPrefetch? prefetch = null)
    {
        if (prefetch is not null)
        {
            // A prefetch task that was never started falls back to the real
            // reader — prefetching must never shrink what the resolver sees.
            return new SceneCoordinator(
                Scenes,
                Memories,
                Pending,
                _picker,
                titleReader: prefetch.TitleTask is { } titleTask
                    ? _ => titleTask
                    : _titleReader,
                insightsReader: prefetch.InsightsTask is { } insightsTask
                    ? _ => insightsTask
                    : filePaths is null
                        ? null
                        : cancellationToken => _insightsReader(filePaths, cancellationToken));
        }
        return new(
            Scenes,
            Memories,
            Pending,
            _picker,
            _titleReader,
            insightsReader: filePaths is null
                ? null
                : cancellationToken => _insightsReader(filePaths, cancellationToken));
    }

    private void RemoveSceneFromBindings(string sceneId)
    {
        var memories = Memories.Load();
        var touched = false;
        foreach (var memory in memories.Values)
        {
            if (memory.BoundSceneIDs.RemoveAll(id => id == sceneId) > 0)
                touched = true;
        }
        if (touched)
            Memories.Save(memories);
    }

    private void RaiseChanged() => Changed?.Invoke();
}
