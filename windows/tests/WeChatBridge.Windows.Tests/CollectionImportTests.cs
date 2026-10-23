using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

public sealed class CollectionImportTests
{
    [Fact]
    public void ReceiptIsTransientAndDoesNotCreateOrCountBatches()
    {
        using var inbox = new TempInbox();
        using var receipt = new CollectionImportSession(inbox.Paths);
        var pending = CollectionImportProgress.Read(inbox.Paths)!;
        Assert.Equal("saving", pending.Phase);
        Assert.True(pending.IsFresh(DateTimeOffset.UtcNow));
        Assert.Empty(new InboxReader(inbox.Paths).LoadBatches());
        receipt.Finish(true);
        receipt.Dispose();
        var finished = CollectionImportProgress.Read(inbox.Paths)!;
        Assert.Equal(pending.OperationId, finished.OperationId);
        Assert.Equal("finished", finished.Phase);
        Assert.Empty(new InboxReader(inbox.Paths).LoadBatches());
    }
    [Fact]
    public void FailedReceiptPreservesFailureAndKilledHelpersExpire()
    {
        using var inbox = new TempInbox();
        var receipt = new CollectionImportSession(inbox.Paths);
        receipt.Dispose();
        receipt.Finish(true);
        Assert.Equal("failed", CollectionImportProgress.Read(inbox.Paths)!.Phase);
        var stale = new CollectionImportProgress(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-2), "saving");
        Assert.False(stale.IsFresh(DateTimeOffset.UtcNow));
        File.WriteAllText(Path.Combine(inbox.Paths.Root, CollectionImportProgress.FileName), "{broken");
        Assert.Null(CollectionImportProgress.Read(inbox.Paths));
    }
}
