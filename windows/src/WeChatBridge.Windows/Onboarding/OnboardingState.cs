using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Onboarding;

/// <summary>
/// The persisted first-run flag — the port of macOS
/// <c>Preferences.onboardingCompleted</c>/<c>onboardingStep</c>. Stored as
/// %LOCALAPPDATA%\WeChatBridge\Config\onboarding.json through
/// <see cref="ConfigStore"/>: a missing or corrupt file reads as "never run",
/// which fails toward showing the guide, never toward hiding it.
/// </summary>
public sealed class OnboardingState
{
    public const string FileName = "onboarding.json";
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>
    /// Only finishing the guide counts — closing its window mid-way does not,
    /// matching macOS where 完成 is the single place the flag flips.
    /// </summary>
    public bool Completed { get; set; }

    /// <summary>
    /// The step the guide resumes at if the window was closed mid-run. Reset to
    /// 0 by <see cref="OnboardingStateStore.MarkCompleted"/>: a finished run —
    /// and any re-run of 重新运行设置向导 — always starts at the top.
    /// </summary>
    public int Step { get; set; }

    /// <summary>When 完成 was pressed. Diagnostic only; never read for a decision.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>
/// The <see cref="ConfigStore"/> pattern over <see cref="OnboardingState"/> —
/// same load/save seam as <c>AppSettingsStore</c> and <c>ForwardTargetStore</c>.
/// </summary>
public sealed class OnboardingStateStore
{
    private readonly string _directory;

    public OnboardingStateStore(string? directory = null) =>
        _directory = directory ?? ConfigStore.DefaultDirectory;

    public OnboardingState Load() =>
        ConfigStore.Load<OnboardingState>(_directory, OnboardingState.FileName) ?? new OnboardingState();

    public void Save(OnboardingState state) =>
        ConfigStore.Save(_directory, OnboardingState.FileName, state);

    /// <summary>
    /// What OnStartup checks: the guide is owed until it has been finished once.
    /// Interactive launches only — a <c>--background</c> share launch must never
    /// put this window over WeChat.
    /// </summary>
    public bool NeedsOnboarding() => !Load().Completed;

    /// <summary>
    /// Written on every step change so a guide closed mid-run resumes where it
    /// stopped, the same way macOS persists <c>onboardingStep</c>.
    /// </summary>
    public void SaveStep(int step)
    {
        var state = Load();
        if (state.Step == step)
            return;
        state.Step = step;
        Save(state);
    }

    /// <summary>完成 pressed — the run counts, and the counter returns to the top.</summary>
    public void MarkCompleted()
    {
        var state = Load();
        state.Completed = true;
        state.CompletedAt = DateTimeOffset.UtcNow;
        state.Step = 0;
        Save(state);
    }
}
