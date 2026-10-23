using System.Text.Json;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

/// <summary>
/// The manifest is the contract the helper hands to every later stage, so its shape is
/// asserted directly rather than only through the commit API.
/// </summary>
public sealed class ManifestContractTests
{
    [Fact]
    public async Task ManifestTimestampIsIso8601UtcWithZuluSuffix()
    {
        // macOS writes this field with JSONEncoder.dateEncodingStrategy = .iso8601, which
        // emits "…Z". A "+00:00" offset here would make the two platforms unreadable to
        // each other, so the exact textual form is part of the contract.
        using var fixture = new TempInbox();
        var source = fixture.WriteSource("聊天记录.zip", "zip");

        var result = await InboxWriter.CommitAsync(
            fixture.Paths,
            [new InboxSourceFile(source, "聊天记录.zip", "application/zip", 0, 0)]);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(result.BatchDirectory, "manifest.json")));
        var createdAt = document.RootElement.GetProperty("createdAt").GetString();
        Assert.NotNull(createdAt);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", createdAt);
    }

    [Fact]
    public async Task ManifestNeverPersistsAbsolutePaths()
    {
        using var fixture = new TempInbox();
        var source = fixture.WriteSource("聊天记录.zip", "zip");

        var result = await InboxWriter.CommitAsync(
            fixture.Paths,
            [new InboxSourceFile(source, "聊天记录.zip", "application/zip", 0, 0)]);

        var raw = await File.ReadAllTextAsync(Path.Combine(result.BatchDirectory, "manifest.json"));
        Assert.DoesNotContain(fixture.Root, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", raw);
        // The recorded path stays relative to the batch directory.
        Assert.Equal("files/聊天记录.zip", result.Manifest.Items[0].RelativePath);
    }

    [Fact]
    public async Task ManifestRoundTripsThroughTheSharedJsonOptions()
    {
        using var fixture = new TempInbox();
        var source = fixture.WriteSource("聊天记录.zip", "zip");
        var result = await InboxWriter.CommitAsync(
            fixture.Paths,
            [new InboxSourceFile(source, "聊天记录.zip", "application/zip", 0, 0)]);

        var restored = JsonSerializer.Deserialize<BatchManifest>(
            await File.ReadAllTextAsync(Path.Combine(result.BatchDirectory, "manifest.json")),
            BatchManifest.JsonOptions);

        Assert.NotNull(restored);
        Assert.Equal(BatchManifest.CurrentSchemaVersion, restored!.SchemaVersion);
        Assert.Equal(result.BatchId, restored.BatchId);
        Assert.Equal("clipboard", restored.Action);
        Assert.Equal(result.Manifest.Items, restored.Items);
    }

    [Fact]
    public async Task CommitAsyncPublishesEveryItemInASingleBatch()
    {
        using var fixture = new TempInbox();
        var first = fixture.WriteSource("one.zip", "first");
        var second = fixture.WriteSource("two.zip", "second");

        var result = await InboxWriter.CommitAsync(
            fixture.Paths,
            [
                new InboxSourceFile(first, "one.zip", "application/zip", 0, 0),
                new InboxSourceFile(second, "two.zip", "application/zip", 1, 0)
            ]);

        // One share produces exactly one batch: a partially published share would make the
        // user see half of what they sent.
        Assert.Single(Directory.EnumerateDirectories(fixture.Paths.Ready));
        Assert.Equal(2, result.Manifest.Items.Count);
        Assert.Equal([0, 1], result.Manifest.Items.Select(item => item.ItemIndex));
    }

    [Fact]
    public async Task CommitAsyncLeavesNoPartialBatchWhenOneFileFailsValidation()
    {
        using var fixture = new TempInbox();
        var good = fixture.WriteSource("good.zip", "zip");
        var bad = fixture.WriteSource("notes.txt", "not-a-zip");

        await Assert.ThrowsAsync<InboxValidationException>(() => InboxWriter.CommitAsync(
            fixture.Paths,
            [
                new InboxSourceFile(good, "good.zip", "application/zip", 0, 0),
                new InboxSourceFile(bad, "notes.txt", "text/plain", 1, 0)
            ]));

        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.Ready));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.Staging));
    }

    [Fact]
    public async Task CommitAsyncRejectsAFileOverThePerFileLimit()
    {
        using var fixture = new TempInbox();
        var oversized = fixture.WriteSourceOfSize("huge.zip", 4096);

        await Assert.ThrowsAsync<InboxValidationException>(() => InboxWriter.CommitAsync(
            fixture.Paths,
            [new InboxSourceFile(oversized, "huge.zip", "application/zip", 0, 0)],
            limits: new InboxLimits(MaxFileBytes: 1024, MaxBatchBytes: 1_073_741_824)));

        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.Ready));
        Assert.Single(Directory.EnumerateFiles(fixture.Paths.Failed, "*.json"));
    }

    [Fact]
    public async Task CommitAsyncRejectsABatchOverTheTotalLimit()
    {
        using var fixture = new TempInbox();
        var first = fixture.WriteSourceOfSize("one.zip", 800);
        var second = fixture.WriteSourceOfSize("two.zip", 800);

        await Assert.ThrowsAsync<InboxValidationException>(() => InboxWriter.CommitAsync(
            fixture.Paths,
            [
                new InboxSourceFile(first, "one.zip", "application/zip", 0, 0),
                new InboxSourceFile(second, "two.zip", "application/zip", 1, 0)
            ],
            limits: new InboxLimits(MaxFileBytes: 1024, MaxBatchBytes: 1200)));

        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.Ready));
    }

    [Fact]
    public async Task CommittedBatchSurvivesAndIsDiscoverableAfterARestart()
    {
        // The helper and the host are separate processes, so a Ready batch has to be
        // readable by a later process that only has the directory on disk.
        using var fixture = new TempInbox();
        var source = fixture.WriteSource("聊天记录.zip", "zip");
        await InboxWriter.CommitAsync(
            fixture.Paths,
            [new InboxSourceFile(source, "聊天记录.zip", "application/zip", 0, 0)]);

        var reopened = new InboxPaths(fixture.Root);
        var manifestPath = Path.Combine(
            Directory.EnumerateDirectories(reopened.Ready).Single(),
            "manifest.json");
        var manifest = JsonSerializer.Deserialize<BatchManifest>(
            await File.ReadAllTextAsync(manifestPath),
            BatchManifest.JsonOptions);

        Assert.NotNull(manifest);
        Assert.Single(manifest!.Items);
        Assert.Equal("聊天记录.zip", manifest.Items[0].DisplayName);
        Assert.True(File.Exists(Path.Combine(
            Path.GetDirectoryName(manifestPath)!,
            manifest.Items[0].RelativePath)));
    }
}
