namespace WeChatBridge.Windows.Core.Delivery;

/// <summary>
/// What 「发送到自定义」 should do with the list of apps the user has added.
/// Ported from <c>CustomForwardDecision.swift</c>.
///
/// The decision is a pure function of the list so it can be tested without a
/// window: the engine's orchestration is where interop lives, and the branch
/// that matters is this one.
///
/// One app is not a choice. Presenting a picker with a single row would be
/// WeChatBridge asking a question whose answer it already has — the user's own
/// requirement, and the reason <see cref="Single"/> exists rather than
/// <c>Choose([one])</c>.
/// </summary>
public abstract record CustomForwardDecision
{
    private CustomForwardDecision() { }

    /// <summary>Nothing to send to. The app says so and offers to open 设置 → 入口.</summary>
    public sealed record None : CustomForwardDecision;

    /// <summary>Exactly one app: forward to it straight away, no interface at all.</summary>
    public sealed record Single(ForwardTarget Target) : CustomForwardDecision;

    /// <summary>Two or more: ask, with the list in the order it was given.</summary>
    public sealed record Choose(IReadOnlyList<ForwardTarget> Targets) : CustomForwardDecision;

    /// <summary>
    /// <paramref name="targets"/> arrives in display order — last used first,
    /// then the order the user arranged in 设置 → 入口 — and that order is
    /// preserved, because the panel's first row is the one Return picks.
    /// </summary>
    public static CustomForwardDecision Decide(IReadOnlyList<ForwardTarget> targets) =>
        targets.Count switch
        {
            0 => new None(),
            1 => new Single(targets[0]),
            _ => new Choose(targets),
        };
}
