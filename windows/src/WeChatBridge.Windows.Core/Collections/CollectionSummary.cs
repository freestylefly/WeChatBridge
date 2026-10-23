namespace WeChatBridge.Windows.Core;

public static class CollectionSummary
{
    public static string Format(IReadOnlyList<CollectionBatchMetadata> batches, string size)
    {
        var known = batches.Where(b => b.Count is not null).Sum(b => b.Count!.Value);
        var unknown = batches.Count(b => b.Count is null);
        var count = unknown == 0 ? L10n.Format($"约 {known} 条")
            : unknown == batches.Count ? L10n.Text("条数暂不可用")
            : L10n.Format($"已识别约 {known} 条 · {unknown} 批条数未知");
        return L10n.Format($"{batches.Count} 批 · {count} · {size}");
    }
}
