namespace WeChatBridge.Windows.Core;

/// <summary>
/// The sender set of one batch, used to recognize a group whose title could not
/// be read. Ported from the macOS path where
/// <c>WeChatBatchInsightsReader</c> collects
/// <c>records.map(\.sender).map(GroupName.normalizeSender)</c> and
/// <see cref="GroupFingerprint"/> matches the set against remembered groups.
///
/// A renamed group keeps answering to its members: the name in the title bar is
/// a label, the sender set is the identity.
/// </summary>
public sealed record SenderFingerprint
{
    /// <summary>Normalized, non-empty sender names.</summary>
    public IReadOnlySet<string> Senders { get; }

    private SenderFingerprint(IReadOnlySet<string> senders) => Senders = senders;

    public static SenderFingerprint Empty { get; } = new(new HashSet<string>(StringComparer.Ordinal));

    /// <summary>Folds raw display names the same way memories were stored.</summary>
    public static SenderFingerprint FromRawNames(IEnumerable<string>? rawSenders)
    {
        if (rawSenders is null)
            return Empty;
        var normalized = rawSenders
            .Select(GroupName.NormalizeSender)
            .Where(s => s.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        return normalized.Count == 0 ? Empty : new SenderFingerprint(normalized);
    }

    /// <summary>
    /// The memory key of the one group this sender set belongs to, or null when
    /// the overlap is too thin or two groups match equally — a guessed identity
    /// is worse than none, since a wrong binding would keep answering.
    /// </summary>
    public string? MatchMemoryKey(IReadOnlyDictionary<string, GroupMemory> memories) =>
        GroupFingerprint.Match(Senders, memories);

    public GroupMemory? MatchMemory(IReadOnlyDictionary<string, GroupMemory> memories) =>
        MatchMemoryKey(memories) is { } key ? memories.GetValueOrDefault(key) : null;
}
