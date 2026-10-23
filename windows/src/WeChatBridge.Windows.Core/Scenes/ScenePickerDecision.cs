namespace WeChatBridge.Windows.Core;

/// <summary>
/// What the scene picker answered. Ported from <c>ScenePickerPanel.Answer</c>:
/// <see cref="ScenePickerAnswerKind.Expired"/> is distinct from
/// <see cref="ScenePickerAnswerKind.Cancelled"/> because an expired share is a
/// dead batch the caller records as such, while a cancelled pick still lets the
/// default-scene fallback run.
/// </summary>
public enum ScenePickerAnswerKind
{
    Picked,
    Cancelled,
    Expired,
}

public sealed record ScenePickerAnswer(ScenePickerAnswerKind Kind, WeChatScene? Scene)
{
    public static ScenePickerAnswer Picked(WeChatScene scene) => new(ScenePickerAnswerKind.Picked, scene);

    public static ScenePickerAnswer Cancelled { get; } = new(ScenePickerAnswerKind.Cancelled, null);

    public static ScenePickerAnswer Expired { get; } = new(ScenePickerAnswerKind.Expired, null);
}

/// <summary>
/// Shows the scene list and returns the user's pick; the UI implementation is
/// the WPF <c>ScenePickerWindow</c>. A delegate (not an interface) so the
/// coordinator stays usable in a headless host and tests inject a stub without
/// mocking.
/// </summary>
public delegate Task<ScenePickerAnswer> ScenePickerHandler(
    IReadOnlyList<WeChatScene> scenes, CancellationToken cancellationToken);

/// <summary>
/// What a set of candidate scenes should do to the share flow. Mirrors
/// <c>CustomForwardDecision</c> in WeChatBridgeCore: the count alone decides,
/// because one candidate is not a choice — presenting a picker with a single
/// row would be asking a question whose answer is already known.
/// </summary>
public enum ScenePickerDecisionKind
{
    /// <summary>No candidates: nothing to ask, and no prompt appears.</summary>
    NoPrompt,
    /// <summary>Exactly one candidate: use it straight away, no interface.</summary>
    Single,
    /// <summary>Two or more: ask, with the candidates in the order given.</summary>
    Choose,
}

public sealed record ScenePickerDecision(
    ScenePickerDecisionKind Kind, IReadOnlyList<WeChatScene> Scenes)
{
    public static ScenePickerDecision Decide(IReadOnlyList<WeChatScene> candidates) =>
        candidates.Count switch
        {
            0 => new ScenePickerDecision(ScenePickerDecisionKind.NoPrompt, []),
            1 => new ScenePickerDecision(ScenePickerDecisionKind.Single, candidates),
            _ => new ScenePickerDecision(ScenePickerDecisionKind.Choose, candidates),
        };
}
