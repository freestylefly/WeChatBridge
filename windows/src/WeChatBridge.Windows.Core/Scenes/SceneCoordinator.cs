namespace WeChatBridge.Windows.Core;

/// <summary>
/// The small amount of structure scenes need before anything is pasted: who
/// spoke, and when the exported range ended. Ported from
/// <c>WeChatBatchInsights</c>. The reader that fills it from a WeChat chat ZIP
/// belongs to the inbox pipeline; the coordinator only consumes the shape, via
/// an injectable reader delegate.
/// </summary>
public sealed record WeChatBatchInsights
{
    /// <summary>Normalized sender names (see <see cref="GroupName.NormalizeSender"/>).</summary>
    public IReadOnlySet<string> Senders { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public DateTimeOffset? End { get; init; }

    public static WeChatBatchInsights Empty { get; } = new();
}

/// <summary>
/// Resolves and remembers scene choices for incoming share batches. Ported from
/// <c>SceneCoordinator</c> in the macOS app. Every collaborator — settings,
/// memories, the pending ⌃⌥1–9 selection, the WeChat window title, the batch
/// transcript reader and the picker UI — arrives behind a store or a delegate,
/// so the resolution flow is fully testable without a window, a running WeChat,
/// or a real share.
/// </summary>
public sealed class SceneCoordinator
{
    /// <summary>What the resolution decided about a batch.</summary>
    public sealed class Selection
    {
        public IReadOnlyList<WeChatScene> Scenes { get; set; } = [];
        public string? GroupName { get; set; }
        public DateTimeOffset? PreviousSummaryAt { get; set; }
        public WeChatBatchInsights Insights { get; set; } = WeChatBatchInsights.Empty;

        public WeChatScene? Scene => Scenes.Count > 0 ? Scenes[0] : null;
    }

    public enum AnswerKind { Ready, Expired }

    public sealed record Answer(AnswerKind Kind, Selection? Selection)
    {
        public static Answer Ready(Selection selection) => new(AnswerKind.Ready, selection);

        public static Answer Expired { get; } = new(AnswerKind.Expired, null);
    }

    private readonly SceneStore _scenes;
    private readonly GroupMemoryStore _memories;
    private readonly NextForwardSceneStore _pending;
    private readonly ScenePickerHandler? _picker;
    private readonly Func<CancellationToken, Task<GroupTitleParser.Title?>>? _titleReader;
    private readonly Func<CancellationToken, Task<WeChatBatchInsights>>? _insightsReader;

    // Serializes prepares like @MainActor did: memory is a file, and two
    // overlapping load-modify-save cycles would drop a learned binding.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SceneCoordinator(
        SceneStore scenes,
        GroupMemoryStore memories,
        NextForwardSceneStore pending,
        ScenePickerHandler? picker = null,
        Func<CancellationToken, Task<GroupTitleParser.Title?>>? titleReader = null,
        Func<CancellationToken, Task<WeChatBatchInsights>>? insightsReader = null)
    {
        _scenes = scenes;
        _memories = memories;
        _pending = pending;
        _picker = picker;
        _titleReader = titleReader;
        _insightsReader = insightsReader;
    }

    /// <summary>
    /// The per-share scene decision, ported from <c>prepare</c>:
    /// pending shortcut → binding/fingerprint (asking when a group claims
    /// several scenes) → picker over all enabled scenes for an unbound group →
    /// optional default scene.
    /// </summary>
    public async Task<Answer> PrepareAsync(
        bool enabled,
        string? groupName,
        bool captureTitle,
        bool allowDefault = true,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await PrepareCore(enabled, groupName, captureTitle, allowDefault, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Answer> PrepareCore(
        bool enabled,
        string? groupName,
        bool captureTitle,
        bool allowDefault,
        CancellationToken cancellationToken)
    {
        var selection = new Selection { GroupName = groupName };
        if (!enabled && !captureTitle)
            return Answer.Ready(selection);

        // Insights and the window title are both IO-bound; run them together as
        // the macOS worker queue did.
        var insightsTask = enabled && _insightsReader is not null
            ? _insightsReader(cancellationToken)
            : null;
        var title = captureTitle && _titleReader is not null
            ? await _titleReader(cancellationToken)
            : (GroupTitleParser.Title?)null;
        if (title is { } parsed)
            selection.GroupName = parsed.Name;
        if (!enabled)
            return Answer.Ready(selection);

        selection.Insights = insightsTask is null
            ? WeChatBatchInsights.Empty
            : await insightsTask;

        var settings = _scenes.Load();
        var memories = _memories.Load();

        // A ⌃⌥1–9 choice outranks every rule — the user just said it.
        if (_pending.ConsumePendingSceneId() is { } pendingId
            && settings.Scene(pendingId) is { } pendingScene)
        {
            selection.Scenes = [pendingScene];
            if (selection.GroupName is { } pendingGroup)
                selection.PreviousSummaryAt = memories.GetValueOrDefault(GroupName.Normalize(pendingGroup))?.LastSummaryAt;
            return Answer.Ready(selection);
        }

        var resolution = SceneResolver.Resolve(
            selection.GroupName, settings, memories,
            selection.Insights.Senders, allowDefault: false);
        // Only a remembered group identity (binding, or the sender fingerprint
        // that recovered one) auto-resolves. A keyword or last-used guess must
        // not silently pick a scene in front of the user.
        if (resolution.Source is not SceneMatchSource.Binding and not SceneMatchSource.Fingerprint)
        {
            resolution = new SceneResolution(
                [], resolution.GroupName, resolution.UsedFingerprint, SceneMatchSource.None);
        }
        selection.GroupName = resolution.GroupName ?? selection.GroupName;

        var hasBinding = selection.GroupName is { } name
            && memories.TryGetValue(GroupName.Normalize(name), out var boundMemory)
            && settings.ScenesFor(boundMemory.BoundSceneIDs).Count > 0;

        // A group bound to several scenes asks which one this share is for —
        // the same shape as CustomForwardDecision: many rows is a question, one
        // is not.
        if (ScenePickerDecision.Decide(resolution.Scenes).Kind == ScenePickerDecisionKind.Choose
            && selection.GroupName is not null)
        {
            switch (await AskPicker(resolution.Scenes, cancellationToken))
            {
                case { Kind: ScenePickerAnswerKind.Picked, Scene: { } picked }:
                    resolution = new SceneResolution(
                        [picked], selection.GroupName, resolution.UsedFingerprint, resolution.Source);
                    break;
                case { Kind: ScenePickerAnswerKind.Expired }:
                    return Answer.Expired;
                default:
                    resolution = new SceneResolution(
                        [], selection.GroupName, resolution.UsedFingerprint, SceneMatchSource.None);
                    break;
            }
        }

        // An unbound group with a usable name asks once and keeps the answer;
        // the pick is written as the group's binding for the next share.
        if (resolution.Scenes.Count == 0
            && !hasBinding
            && selection.GroupName is not null
            && settings.EnabledScenes.Count > 0)
        {
            switch (await AskPicker(settings.EnabledScenes, cancellationToken))
            {
                case { Kind: ScenePickerAnswerKind.Picked, Scene: { } picked }:
                    resolution = new SceneResolution(
                        [picked], selection.GroupName, false, SceneMatchSource.Binding);
                    RememberBinding(memories, [picked], selection.GroupName);
                    break;
                case { Kind: ScenePickerAnswerKind.Expired }:
                    return Answer.Expired;
                default:
                    break;
            }
        }

        if (resolution.Scenes.Count == 0
            && allowDefault
            && settings.DefaultScene() is { } fallback)
        {
            resolution = new SceneResolution(
                [fallback], selection.GroupName, false, SceneMatchSource.DefaultScene);
        }

        selection.Scenes = resolution.Scenes;
        if (selection.GroupName is { } finalGroup)
            selection.PreviousSummaryAt = memories.GetValueOrDefault(GroupName.Normalize(finalGroup))?.LastSummaryAt;
        return Answer.Ready(selection);
    }

    /// <summary>
    /// Records what a forwarded batch taught us about its group — last scene,
    /// summary watermark, sender fingerprint — ported from <c>advance</c>.
    /// The watermark only moves forward so a resent older batch cannot rewind it.
    /// </summary>
    public void Advance(Selection selection)
    {
        if (selection.GroupName is not { } groupName)
            return;
        var key = GroupName.Normalize(groupName);
        if (key.Length == 0)
            return;

        var memories = _memories.Load();
        if (selection.Scene is { } scene && selection.Insights.End is { } end)
        {
            memories[key] = GroupMemory.Advancing(
                memories.GetValueOrDefault(key),
                groupName, scene.Id, selection.Insights.Senders, end);
        }
        else
        {
            var memory = memories.GetValueOrDefault(key) ?? new GroupMemory { DisplayName = groupName };
            memory.Senders.UnionWith(selection.Insights.Senders);
            memory.UpdatedAt = DateTimeOffset.UtcNow;
            memories[key] = memory;
        }
        _memories.Save(memories);
    }

    private void RememberBinding(
        Dictionary<string, GroupMemory> memories,
        IReadOnlyList<WeChatScene> scenes,
        string? groupName)
    {
        if (groupName is null)
            return;
        var key = GroupName.Normalize(groupName);
        if (key.Length == 0)
            return;
        var memory = memories.GetValueOrDefault(key) ?? new GroupMemory { DisplayName = groupName };
        memory.BoundSceneIDs = scenes.Select(s => s.Id).ToList();
        memory.UpdatedAt = DateTimeOffset.UtcNow;
        memories[key] = memory;
        _memories.Save(memories);
    }

    /// <summary>
    /// No picker installed means a headless host — answer every prompt as
    /// 「直接转发」 so a share is never held hostage by an invisible panel.
    /// </summary>
    private Task<ScenePickerAnswer> AskPicker(
        IReadOnlyList<WeChatScene> scenes, CancellationToken cancellationToken) =>
        _picker is null
            ? Task.FromResult(ScenePickerAnswer.Cancelled)
            : _picker(scenes, cancellationToken);
}
