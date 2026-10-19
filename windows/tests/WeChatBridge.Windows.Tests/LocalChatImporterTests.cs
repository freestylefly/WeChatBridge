using System.IO.Compression;
using System.Text;
using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

public sealed class LocalChatImporterTests
{
    [Fact]
    public async Task ZipBytesArePreservedAndReloadDoesNotCreateAForwardOrCopiedOutcome()
    {
        using var fixture = new TempInbox();
        var zipPath = CreateZip(fixture, "export.zip");
        var expected = await File.ReadAllBytesAsync(zipPath);
        var result = await LocalChatImporter.ImportFilesAsync(fixture.Paths, [zipPath]);
        var reader = new InboxReader(fixture.Paths);
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var batch = Assert.Single(reader.LoadBatches());
            Assert.Equal(ShareAction.Hub, batch.Action);
            Assert.False(batch.IsFirstSeen);
            Assert.Null(batch.Outcome);
            Assert.Equal(expected, await File.ReadAllBytesAsync(Assert.Single(batch.Items).FullPath));
        }
        Assert.False(File.Exists(Path.Combine(result.BatchDirectory, BatchIntent.FileName)));
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.Staging));
    }

    [Fact]
    public async Task TextAndNamedPastedTextBecomeReadableUtf8Transcripts()
    {
        using var fixture = new TempInbox();
        const string body = "·小林\n2026年10月17日 08:00\n你好，保留原始文字。\n";
        var source = fixture.WriteSource("笔记.txt", "");
        await File.WriteAllTextAsync(source, body, Encoding.Unicode);
        await LocalChatImporter.ImportFilesAsync(fixture.Paths, [source]);
        await LocalChatImporter.ImportTextAsync(fixture.Paths, body, " 项目群 ");
        var batches = new InboxReader(fixture.Paths).LoadBatches();
        Assert.Equal(2, batches.Count);
        Assert.Contains(batches, b => b.ChatName == "项目群");
        foreach (var batch in batches)
        {
            var transcript = WeChatNativeArchive.GetTranscript(await File.ReadAllBytesAsync(batch.Items[0].FullPath));
            Assert.NotNull(transcript);
            Assert.Equal(body, transcript.Body);
            Assert.Single(transcript.Records!);
        }
        Assert.Equal(body, await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task InvalidMemberPreventsPublishingTheEntireBatch()
    {
        using var fixture = new TempInbox();
        var zip = CreateZip(fixture, "valid.zip");
        var broken = fixture.WriteSource("broken.zip", "not a zip");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LocalChatImporter.ImportFilesAsync(fixture.Paths, [zip, broken]));
        Assert.Empty(new InboxReader(fixture.Paths).LoadBatches());
        Assert.True(File.Exists(zip));
    }

    [Fact]
    public async Task MixedZipAndTextWithSameNameNeverOverwriteEachOther()
    {
        using var fixture = new TempInbox();
        var zip = CreateZip(fixture, "chat.zip");
        var txt = fixture.WriteSource("chat.txt", "文字记录");
        await LocalChatImporter.ImportFilesAsync(fixture.Paths, [zip, txt]);
        var batch = Assert.Single(new InboxReader(fixture.Paths).LoadBatches());
        Assert.Equal(["chat.zip", "chat (2).zip"], batch.Items.Select(i => i.DisplayName));
        Assert.Equal("文字记录", WeChatNativeArchive.GetTranscript(await File.ReadAllBytesAsync(batch.Items[1].FullPath))?.Body);
    }

    [Fact]
    public async Task RejectsEmptyUnsupportedAndOversizedTextWithoutPublishing()
    {
        using var fixture = new TempInbox();
        await Assert.ThrowsAsync<InboxValidationException>(() => LocalChatImporter.ImportTextAsync(fixture.Paths, "  "));
        await Assert.ThrowsAsync<InboxValidationException>(() =>
            LocalChatImporter.ImportFilesAsync(fixture.Paths, [fixture.WriteSource("file.exe", "data")]));
        await Assert.ThrowsAsync<InboxValidationException>(() => LocalChatImporter.ImportFilesAsync(fixture.Paths,
            [fixture.WriteSourceOfSize("huge.txt", LocalChatImporter.MaxTextBytes + 1L)]));
        Assert.Empty(new InboxReader(fixture.Paths).LoadBatches());
    }

    [Fact]
    public async Task CancellationDoesNotPublishOrLeaveStaging()
    {
        using var fixture = new TempInbox();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalChatImporter.ImportFilesAsync(
            fixture.Paths, [CreateZip(fixture, "cancel.zip")], cancellation.Token));
        Assert.Empty(new InboxReader(fixture.Paths).LoadBatches());
        Assert.False(Directory.Exists(fixture.Paths.Staging) && Directory.EnumerateDirectories(fixture.Paths.Staging).Any());
    }

    private static string CreateZip(TempInbox fixture, string name)
    {
        var source = fixture.WriteSource(name, "");
        using var zip = new ZipArchive(File.OpenWrite(source), ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("聊天记录.txt").Open());
        writer.Write("测试聊天\n");
        return source;
    }
}
