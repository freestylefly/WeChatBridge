using WeChatBridge.Windows.Core.Redeem;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// The idempotency record's own behaviour, including the ways it can fail.
/// <para>
/// This file is the difference between a recoverable retry and a customer arguing with
/// support, so the corrupt and missing cases matter as much as the happy one: each of
/// them has to land on "behave as if there is no record" rather than on "refuse".
/// </para>
/// </summary>
public sealed class RedeemPendingStoreTests
{
    [Fact]
    public void ARoundTripPreservesTheRequestIdAndDigest()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WcbRedeemPending", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PendingRedemptionStore(root);
            var pending = PendingRedemption.Create("digest-value", DateTimeOffset.UnixEpoch, "request-1");

            store.Save(pending);

            var loaded = store.Load();
            Assert.NotNull(loaded);
            Assert.Equal("request-1", loaded!.RequestId);
            Assert.Equal("digest-value", loaded.CodeDigest);
            Assert.Equal(DateTimeOffset.UnixEpoch, loaded.CreatedAt);
        }
        finally
        {
            if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SavingIsAtomicSoAnInterruptedWriteCannotTruncateTheRequestId()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WcbRedeemPending", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PendingRedemptionStore(root);

            store.Save(PendingRedemption.Create("digest", DateTimeOffset.UnixEpoch, "request-1"));

            // Staged and renamed, so no .tmp debris is left where the next load looks.
            Assert.False(System.IO.File.Exists(store.FilePath + ".tmp"));
            Assert.True(System.IO.File.Exists(store.FilePath));
        }
        finally
        {
            if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EveryWayOfLosingTheRecordReadsAsNoRecord()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WcbRedeemPending", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PendingRedemptionStore(root);
            Assert.Null(store.Load());

            System.IO.Directory.CreateDirectory(root);
            System.IO.File.WriteAllText(store.FilePath, "{ this is not json");
            Assert.Null(store.Load());

            // A newer format is no evidence either, for the same reason: reading half
            // of a format we do not understand would turn a downgrade into a state.
            System.IO.File.WriteAllText(store.FilePath, "{\"v\":99,\"code\":\"d\",\"rid\":\"r\",\"at\":\"2026-01-01T00:00:00Z\"}");
            Assert.Null(store.Load());

            System.IO.File.WriteAllText(store.FilePath, "{\"v\":1,\"code\":\"\",\"rid\":\"r\"}");
            Assert.Null(store.Load());

            System.IO.File.Delete(store.FilePath);
            Assert.Null(store.Load());
        }
        finally
        {
            if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ClearingRemovesTheRecordAndItsStagingDebris()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WcbRedeemPending", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PendingRedemptionStore(root);
            store.Save(PendingRedemption.Create("digest", DateTimeOffset.UnixEpoch, "request-1"));
            System.IO.File.WriteAllText(store.FilePath + ".tmp", "debris");

            store.Clear();

            Assert.Null(store.Load());
            Assert.False(System.IO.File.Exists(store.FilePath));
            Assert.False(System.IO.File.Exists(store.FilePath + ".tmp"));

            // Clearing twice is what a retry after success does; it must not throw.
            store.Clear();
        }
        finally
        {
            if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, recursive: true);
        }
    }
}
