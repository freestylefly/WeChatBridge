using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// Mirrors <c>InboxMaintenanceTests</c> on macOS. Staging debris is the one thing a
/// killed share leaves behind, so the sweep has to remove it without ever touching a
/// copy that is still in flight.
/// </summary>
public sealed class InboxMaintenanceTests
{
    [Fact]
    public void PruneStagingRemovesOnlyTreesOlderThanTheWindow()
    {
        using var fixture = new TempInbox();
        fixture.Paths.EnsureCreated();
        var stale = Path.Combine(fixture.Paths.Staging, $"{Guid.NewGuid():N}.staging");
        var fresh = Path.Combine(fixture.Paths.Staging, $"{Guid.NewGuid():N}.staging");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(fresh);
        File.WriteAllText(Path.Combine(stale, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(fresh, "manifest.json"), "{}");
        fixture.SetWriteTime(DateTimeOffset.UtcNow.AddHours(-1), stale);

        var removed = fixture.Paths.PruneStaging(TimeSpan.FromMinutes(30));

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void PruneStagingKeepsAnOldTreeStillBeingWrittenInto()
    {
        // The failure this guards against: a multi-gigabyte copy only touches the file
        // being written, so judging the batch by its parent directory timestamp would
        // delete a share that is still arriving.
        using var fixture = new TempInbox();
        fixture.Paths.EnsureCreated();
        var staging = Path.Combine(fixture.Paths.Staging, $"{Guid.NewGuid():N}.staging");
        var files = Path.Combine(staging, "files");
        Directory.CreateDirectory(files);
        var inFlight = Path.Combine(files, "big.zip");
        File.WriteAllText(inFlight, "partial");
        fixture.SetWriteTime(DateTimeOffset.UtcNow.AddHours(-1), staging);
        File.SetLastWriteTimeUtc(inFlight, DateTime.UtcNow);

        Assert.Equal(0, fixture.Paths.PruneStaging(TimeSpan.FromMinutes(30)));
        Assert.True(Directory.Exists(staging));
    }

    [Fact]
    public async Task PruneStagingLeavesReadyBatchesAlone()
    {
        using var fixture = new TempInbox();
        var source = fixture.WriteSource("kept.zip", "zip");
        var committed = await InboxWriter.CommitAsync(
            fixture.Paths,
            [new InboxSourceFile(source, "kept.zip", "application/zip", 0, 0)]);
        fixture.SetWriteTime(DateTimeOffset.UtcNow.AddDays(-1), committed.BatchDirectory);

        fixture.Paths.PruneStaging(TimeSpan.FromMinutes(1));

        Assert.True(Directory.Exists(committed.BatchDirectory));
        Assert.Single(Directory.EnumerateDirectories(fixture.Paths.Ready));
    }
}
