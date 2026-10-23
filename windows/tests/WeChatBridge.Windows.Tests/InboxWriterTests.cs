using System.Text.Json;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

public sealed class InboxWriterTests
{
    [Fact]
    public async Task CommitAsyncWritesManifestAndAtomicallyPublishesReadyBatch()
    {
        using var fixture = new TempInbox();
        var source = fixture.WriteSource("聊天记录.zip", "zip-bytes");

        var result = await InboxWriter.CommitAsync(
            fixture.Paths,
            [new InboxSourceFile(source, "聊天记录.zip", "application/zip", 0, 0)]);

        Assert.True(Directory.Exists(result.BatchDirectory));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.Staging));
        Assert.True(File.Exists(Path.Combine(result.BatchDirectory, "manifest.json")));
        var manifest = JsonSerializer.Deserialize<BatchManifest>(
            await File.ReadAllTextAsync(Path.Combine(result.BatchDirectory, "manifest.json")),
            BatchManifest.JsonOptions);
        Assert.NotNull(manifest);
        Assert.Equal(result.BatchId, manifest!.BatchId);
        Assert.Equal("files/聊天记录.zip", manifest.Items[0].RelativePath);
        Assert.Equal("zip-bytes", await File.ReadAllTextAsync(
            Path.Combine(result.BatchDirectory, manifest.Items[0].RelativePath)));
    }

    [Fact]
    public async Task CommitAsyncDisambiguatesDuplicateNamesWithoutOverwriting()
    {
        using var fixture = new TempInbox();
        var first = fixture.WriteSource("one.zip", "first");
        var second = fixture.WriteSource("two.zip", "second");

        var result = await InboxWriter.CommitAsync(
            fixture.Paths,
            [
                new InboxSourceFile(first, "聊天记录.zip", "application/zip", 0, 0),
                new InboxSourceFile(second, "聊天记录.zip", "application/zip", 1, 0)
            ]);

        Assert.Equal(["聊天记录.zip", "聊天记录 (2).zip"], result.Manifest.Items.Select(item => item.DisplayName));
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(result.BatchDirectory, "files", "聊天记录.zip")));
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(result.BatchDirectory, "files", "聊天记录 (2).zip")));
    }

    [Fact]
    public async Task CommitAsyncRejectsNonZipAndRecordsFailure()
    {
        using var fixture = new TempInbox();
        var source = fixture.WriteSource("notes.txt", "not-a-zip");

        await Assert.ThrowsAsync<InboxValidationException>(() => InboxWriter.CommitAsync(
            fixture.Paths,
            [new InboxSourceFile(source, "notes.txt", "text/plain", 0, 0)]));

        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.Ready));
        Assert.Single(Directory.EnumerateFiles(fixture.Paths.Failed, "*.json"));
    }

    [Fact]
    public async Task CommitAsyncRejectsTraversalNameWithoutWritingOutsideBatch()
    {
        using var fixture = new TempInbox();
        var source = fixture.WriteSource("safe.zip", "safe");

        var result = await InboxWriter.CommitAsync(
            fixture.Paths,
            [new InboxSourceFile(source, "..\\..\\escaped.zip", "application/zip", 0, 0)]);

        Assert.Equal("escaped.zip", result.Manifest.Items[0].DisplayName);
        Assert.False(File.Exists(Path.Combine(fixture.Paths.Root, "escaped.zip")));
    }

    [Fact]
    public async Task CommitAsyncRejectsReparsePointWhenSupported()
    {
        using var fixture = new TempInbox();
        var source = fixture.WriteSource("source.zip", "zip");
        var link = Path.Combine(fixture.Root, "link.zip");
        try
        {
            File.CreateSymbolicLink(link, source);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            return;
        }

        await Assert.ThrowsAsync<InboxValidationException>(() => InboxWriter.CommitAsync(
            fixture.Paths,
            [new InboxSourceFile(link, "link.zip", "application/zip", 0, 0)]));
    }
}
