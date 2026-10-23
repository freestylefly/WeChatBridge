using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Delivery;

namespace WeChatBridge.Windows.Services;

/// <summary>
/// Shows the custom-target list and returns the user's pick; the UI
/// implementation is the WPF <see cref="TargetPickerWindow"/>. A delegate (not
/// an interface) so the service stays usable in a headless host — the same
/// seam as <c>ScenePickerHandler</c> in Core.
/// </summary>
public delegate Task<TargetPickerAnswer> TargetPickerHandler(
    IReadOnlyList<ForwardTarget> targets, CancellationToken cancellationToken);

/// <summary>How a 「发送到自定义」 resolution ended.</summary>
public enum CustomTargetResolutionKind
{
    /// <summary>A target to forward to — picked in the panel or auto-routed.</summary>
    Picked,
    /// <summary>The user's list is empty: nothing to send to, nothing to ask.</summary>
    NoTargets,
    /// <summary>Esc, the ✕, a superseding question, or a headless host: the user said no.</summary>
    Cancelled,
    /// <summary>Unanswered for <see cref="BatchIntent.FreshnessWindow"/>: nobody said anything.</summary>
    Expired,
}

public sealed record CustomTargetResolution(CustomTargetResolutionKind Kind, ForwardTarget? Target)
{
    public static CustomTargetResolution Picked(ForwardTarget target) =>
        new(CustomTargetResolutionKind.Picked, target);

    public static CustomTargetResolution NoTargets { get; } =
        new(CustomTargetResolutionKind.NoTargets, null);

    public static CustomTargetResolution Cancelled { get; } =
        new(CustomTargetResolutionKind.Cancelled, null);

    public static CustomTargetResolution Expired { get; } =
        new(CustomTargetResolutionKind.Expired, null);
}

/// <summary>
/// Resolves 「发送到自定义」 for a share that arrived without a destination —
/// which is every one: the share-target helper writes a <see cref="BatchIntent"/>
/// whose <see cref="BatchIntent.TargetBundleIdentifier"/> is null because the
/// helper has no interface to ask with, exactly like the retired macOS share
/// extension. Ported from the <c>.custom</c> branch of
/// <c>ActionRunner.deliver</c> in the macOS app.
///
/// <para>
/// Integration seam: <c>MainViewModel.Announce</c> consumes a fresh intent and
/// calls <c>PerformForward(batch, intent.Action, ResolveTarget(intent))</c>.
/// For a custom intent that lookup returns null today and the delivery engine
/// answers <see cref="DeliveryFailureKind.NoTarget"/>. Wire this service in
/// there instead:
/// </para>
/// <code>
/// if (intent.Action == ShareAction.Custom &amp;&amp; intent.TargetBundleIdentifier is null)
/// {
///     var resolution = await customTargets.ResolveAsync();
///     switch (resolution.Kind)
///     {
///         case CustomTargetResolutionKind.Picked:
///             // PerformForward(batch, ShareAction.Custom, resolution.Target)
///             break;
///         case CustomTargetResolutionKind.NoTargets:
///             // files are on the clipboard; toast 「还没有添加自定义应用。」 with an
///             // 「添加应用」 action navigating to the Entries pane (macOS openEntries)
///             break;
///         case CustomTargetResolutionKind.Cancelled:
///             // an answer, not a fault: record 已取消, no toast
///             break;
///         case CustomTargetResolutionKind.Expired:
///             // record 未执行 — a share nobody answered is no longer the
///             // gesture the user was making
///             break;
///     }
/// }
/// </code>
/// <para>
/// On macOS the files are written to the pasteboard *before* the panel opens so
/// cancelling still leaves the user one ⌘V away. On Windows the share-target
/// helper has already committed the batch to the clipboard by the time this
/// runs, so nothing extra is needed here.
/// </para>
/// </summary>
public sealed class CustomTargetService
{
    private readonly ForwardTargetStore _store;
    private readonly TargetPickerHandler? _picker;

    /// Serializes resolutions like the macOS pending-chain did: a second
    /// 「发送到自定义」 while one is unanswered supersedes, and two overlapping
    /// reads of the store must not interleave with a last-used write.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="store">The user's target list; null wires the default config directory.</param>
    /// <param name="picker">
    /// The panel; null is a headless host, which answers every prompt as
    /// cancelled so a share is never held hostage by an invisible panel
    /// (the <c>SceneCoordinator.AskPicker</c> precedent).
    /// </param>
    public CustomTargetService(ForwardTargetStore? store = null, TargetPickerHandler? picker = null)
    {
        _store = store ?? new ForwardTargetStore();
        _picker = picker;
    }

    /// <summary>The production wiring: the real store and the WPF panel.</summary>
    public static CustomTargetService CreateWindowed(ForwardTargetStore? store = null) =>
        new(store, (targets, cancellationToken) =>
            TargetPickerWindow.ChooseAsync(targets, cancellationToken));

    /// <summary>
    /// The full resolution: load the user's list last-used-first, auto-route
    /// where there is no real choice, ask when there is, and remember the pick
    /// so the next panel opens on it.
    /// </summary>
    public async Task<CustomTargetResolution> ResolveAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ResolveCore(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The chosen target, or null for none/cancelled/expired.</summary>
    public async Task<ForwardTarget?> ResolveTargetAsync(CancellationToken cancellationToken = default) =>
        (await ResolveAsync(cancellationToken)).Target;

    private async Task<CustomTargetResolution> ResolveCore(CancellationToken cancellationToken)
    {
        switch (CustomForwardDecision.Decide(_store.OrderedTargets()))
        {
            case CustomForwardDecision.None:
                return CustomTargetResolution.NoTargets;
            case CustomForwardDecision.Single { Target: { } single }:
                // One app is not a choice — presenting a picker with a single
                // row would be asking a question whose answer is already known.
                return Recorded(single);
            case CustomForwardDecision.Choose { Targets: { } candidates }:
                var answer = _picker is null
                    ? TargetPickerAnswer.Cancelled
                    : await _picker(candidates, cancellationToken);
                return answer switch
                {
                    { Kind: TargetPickerAnswerKind.Picked, Target: { } picked } => Recorded(picked),
                    { Kind: TargetPickerAnswerKind.Expired } => CustomTargetResolution.Expired,
                    _ => CustomTargetResolution.Cancelled,
                };
            default:
                return CustomTargetResolution.Cancelled;
        }
    }

    /// <summary>
    /// The pick — whether made in the panel or by the one-app shortcut — is
    /// remembered so the next 「发送到自定义」 opens with it on top and Return
    /// takes it (macOS <c>ForwardTargets.recordUse</c>).
    /// </summary>
    private CustomTargetResolution Recorded(ForwardTarget target)
    {
        _store.RecordLastUsed(target.BundleIdentifier);
        return CustomTargetResolution.Picked(target);
    }
}
