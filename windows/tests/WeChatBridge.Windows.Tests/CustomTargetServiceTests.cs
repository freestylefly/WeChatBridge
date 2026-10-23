using WeChatBridge.Windows.Core;
using WeChatBridge.Windows.Core.Delivery;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// The decision logic behind <c>Services/CustomTargetService</c>: the service
/// itself lives in the WPF assembly (<c>net10.0-windows</c>) which this
/// <c>net10.0</c> test project cannot reference, so the semantics it delegates
/// to are exercised here — <see cref="CustomForwardDecision.Decide"/> for the
/// branch and <see cref="ForwardTargetStore"/> for ordering and the last-used
/// record, plus a picker fake driving the documented flow end to end. No real
/// windows: the store is pointed at a temp directory.
/// </summary>
public sealed class CustomTargetServiceTests
{
    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "WeChatBridgeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static ForwardTarget Target(string id, string name = "应用") =>
        new(id, name, new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// Mirrors <c>TargetPickerAnswerKind</c> in the WPF assembly — kept as a
    /// local enum so the flow below reads the same way the service does.
    /// </summary>
    private enum PickerAnswer { Picked, Cancelled, Expired }

    private sealed class StubPicker
    {
        public List<IReadOnlyList<ForwardTarget>> Asked { get; } = [];
        public Queue<(PickerAnswer Kind, ForwardTarget? Target)> Answers { get; } = new();

        public Task<(PickerAnswer, ForwardTarget?)> Choose(
            IReadOnlyList<ForwardTarget> targets, CancellationToken cancellationToken)
        {
            Asked.Add(targets);
            return Task.FromResult(
                Answers.Count > 0 ? Answers.Dequeue() : (PickerAnswer.Cancelled, (ForwardTarget?)null));
        }
    }

    /// <summary>
    /// The same walk <c>CustomTargetService.ResolveCore</c> performs — decide on
    /// the ordered list, ask only when there is a real choice, record the pick —
    /// expressed against the Core primitives so the contract is testable here.
    /// </summary>
    private static async Task<ForwardTarget?> Resolve(
        ForwardTargetStore store, StubPicker picker)
    {
        switch (CustomForwardDecision.Decide(store.OrderedTargets()))
        {
            case CustomForwardDecision.None:
                return null;
            case CustomForwardDecision.Single { Target: { } single }:
                store.RecordLastUsed(single.BundleIdentifier);
                return single;
            case CustomForwardDecision.Choose { Targets: { } candidates }:
                var (kind, target) = await picker.Choose(candidates, CancellationToken.None);
                switch (kind)
                {
                    case PickerAnswer.Picked when target is not null:
                        store.RecordLastUsed(target.BundleIdentifier);
                        return target;
                    default:
                        return null;
                }
            default:
                return null;
        }
    }

    // --- CustomForwardDecision ------------------------------------------------

    [Fact]
    public void DecideAsksOnlyWhenThereIsARealChoice()
    {
        Assert.IsType<CustomForwardDecision.None>(CustomForwardDecision.Decide([]));

        var single = CustomForwardDecision.Decide([Target("a")]);
        var chosen = Assert.IsType<CustomForwardDecision.Single>(single);
        Assert.Equal("a", chosen.Target.BundleIdentifier);

        var choose = CustomForwardDecision.Decide([Target("a"), Target("b")]);
        var list = Assert.IsType<CustomForwardDecision.Choose>(choose);
        // Display order is preserved: the panel's first row is the one Return picks.
        Assert.Equal(["a", "b"], list.Targets.Select(t => t.BundleIdentifier).ToArray());
    }

    // --- Ordering -------------------------------------------------------------

    [Fact]
    public void OrderedPutsLastUsedFirst()
    {
        var ordered = ForwardTargetStore.Ordered(
            [Target("a"), Target("b"), Target("c")], lastUsed: "c");
        Assert.Equal(["c", "a", "b"], ordered.Select(t => t.BundleIdentifier).ToArray());
    }

    [Fact]
    public void OrderedLeavesTheListAloneForMissingOrStaleLastUsed()
    {
        var targets = new List<ForwardTarget> { Target("a"), Target("b") };
        Assert.Equal(["a", "b"], ForwardTargetStore.Ordered(targets, null).Select(t => t.BundleIdentifier).ToArray());
        // A pointer at an app no longer in the list changes nothing.
        Assert.Equal(["a", "b"], ForwardTargetStore.Ordered(targets, "gone").Select(t => t.BundleIdentifier).ToArray());
    }

    // --- Store ----------------------------------------------------------------

    [Fact]
    public void LastUsedSurvivesAcrossStoreInstances()
    {
        var directory = TempDirectory();
        var store = new ForwardTargetStore(directory);
        Assert.Null(store.LastUsedBundleIdentifier());

        store.RecordLastUsed("a");
        Assert.Equal("a", new ForwardTargetStore(directory).LastUsedBundleIdentifier());
    }

    [Fact]
    public void OrderedTargetsAppliesTheRecordedPick()
    {
        var directory = TempDirectory();
        var store = new ForwardTargetStore(directory);
        store.Save([Target("a"), Target("b")]);
        store.RecordLastUsed("b");

        var ordered = new ForwardTargetStore(directory).OrderedTargets();
        Assert.Equal(["b", "a"], ordered.Select(t => t.BundleIdentifier).ToArray());
    }

    // --- Resolution flow --------------------------------------------------------

    [Fact]
    public async Task EmptyListResolvesWithoutAsking()
    {
        var store = new ForwardTargetStore(TempDirectory());
        var picker = new StubPicker();

        Assert.Null(await Resolve(store, picker));
        Assert.Empty(picker.Asked);
    }

    [Fact]
    public async Task SingleTargetGoesStraightThroughAndIsRemembered()
    {
        var store = new ForwardTargetStore(TempDirectory());
        store.Save([Target("only")]);
        var picker = new StubPicker();

        var picked = await Resolve(store, picker);

        Assert.Equal("only", picked!.BundleIdentifier);
        // One app is not a choice: no panel, and the answer is remembered anyway.
        Assert.Empty(picker.Asked);
        Assert.Equal("only", store.LastUsedBundleIdentifier());
    }

    [Fact]
    public async Task MultipleTargetsAskInDisplayOrderAndRecordThePick()
    {
        var store = new ForwardTargetStore(TempDirectory());
        store.Save([Target("a"), Target("b"), Target("c")]);
        store.RecordLastUsed("b");
        var picker = new StubPicker();
        picker.Answers.Enqueue((PickerAnswer.Picked, Target("c")));

        var picked = await Resolve(store, picker);

        Assert.Equal("c", picked!.BundleIdentifier);
        var asked = Assert.Single(picker.Asked);
        // Last-used first: the panel opens on 「b」, the row Return takes.
        Assert.Equal(["b", "a", "c"], asked.Select(t => t.BundleIdentifier).ToArray());
        Assert.Equal("c", store.LastUsedBundleIdentifier());
    }

    [Fact]
    public async Task CancelledAndExpiredNeitherPickNorRemember()
    {
        var store = new ForwardTargetStore(TempDirectory());
        store.Save([Target("a"), Target("b")]);
        var picker = new StubPicker();
        picker.Answers.Enqueue((PickerAnswer.Cancelled, (ForwardTarget?)null));
        picker.Answers.Enqueue((PickerAnswer.Expired, (ForwardTarget?)null));

        Assert.Null(await Resolve(store, picker));
        Assert.Null(await Resolve(store, picker));
        Assert.Equal(2, picker.Asked.Count);
        // Nobody chose anything: the previous last-used (none) is untouched.
        Assert.Null(store.LastUsedBundleIdentifier());
    }
}
