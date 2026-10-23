using WeChatBridge.Windows.Core;

namespace WeChatBridge.Windows.Tests;

/// <summary>Ported from WeChatNativeArchiveTests.swift.</summary>
public sealed class ArchiveTests
{
    // Independently generated with Python's zipfile: two repeated multiline
    // messages, a media reference, UTF-8 filenames, and an attachment CRC.
    private static readonly byte[] StoredZip = Convert.FromBase64String(
        "UEsDBBQAAAgAAMBAJV0fGRySiQAAAIkAAAAQAAAA6IGK5aSp6K6w5b2VLnR4dMK355SyCjIwMjblubQ55pyINeaXpSAwODowNQrkvaDlpb0K56ys5LqM6KGMCgrCt+S5mQoyMDI25bm0OeaciDXml6UgMDg6MDUK5L2g5aW9CuesrOS6jOihjAoKwrfnlLIKMjAyNuW5tDnmnIg15pelIDA4OjA2CmltYWdlcy9waG90by5wbmcKUEsDBBQAAAAAAMBAJV2KfiaRIAAAACAAAAAQAAAAaW1hZ2VzL3Bob3RvLnBuZwABAgMEBQYHCAkKCwwNDg8QERITFBUWFxgZGhscHR4fUEsBAhQDFAAACAAAwEAlXR8ZHJKJAAAAiQAAABAAAAAAAAAAAAAAAIABAAAAAOiBiuWkqeiusOW9lS50eHRQSwECFAMUAAAAAADAQCVdin4mkSAAAAAgAAAAEAAAAAAAAAAAAAAAgAG3AAAAaW1hZ2VzL3Bob3RvLnBuZ1BLBQYAAAAAAgACAHwAAAAFAQAAAAA=");

    [Fact]
    public void MessageCountReadsStoredArchiveWithMedia()
    {
        Assert.Equal(3, WeChatNativeArchive.MessageCount(StoredZip));
    }

    [Fact]
    public void TranscriptReturnsTheNamedTxtAndItsRecords()
    {
        var transcript = WeChatNativeArchive.GetTranscript(StoredZip);

        Assert.NotNull(transcript);
        Assert.Equal("聊天记录.txt", transcript!.Path);
        Assert.Equal(3, transcript.Records!.Count);
        Assert.NotNull(transcript.Start);
        Assert.NotNull(transcript.End);
    }

    [Fact]
    public void MessageCountRejectsEmptyFilesAndArchives()
    {
        var fixtures = new[]
        {
            Array.Empty<byte>(),
            Convert.FromBase64String("UEsFBgAAAAAAAAAAAAAAAAAAAAAAAA=="),
            Convert.FromBase64String("UEsDBBQAAAAIAAAAJ10AAAAAAgAAAAAAAAAOAAAAdHJhbnNjcmlwdC50eHQDAFBLAQIUAxQAAAAIAAAAJ10AAAAAAgAAAAAAAAAOAAAAAAAAAAAAAACAAQAAAAB0cmFuc2NyaXB0LnR4dFBLBQYAAAAAAQABADwAAAAuAAAAAAA="),
        };
        foreach (var data in fixtures)
        {
            var error = Assert.Throws<WeChatReadException>(() => WeChatNativeArchive.MessageCount(data));
            Assert.Equal(WeChatReadError.InvalidTranscript, error.Error);
        }
    }

    [Fact]
    public void TranscriptParserKeepsMultilineBodiesAndRejectsUnrecognizedHeaders()
    {
        var body = "·甲\n2026年9月5日 08:05\n第一行\n第二行\n\n·甲\n2026年9月5日 08:06\n再见\n";
        var rows = WeChatTranscriptRecord.Parse(body, TimeZoneInfo.Utc);
        Assert.Equal(new[] { "第一行\n第二行", "再见" }, rows.Select(r => r.Text));
        Assert.Throws<WeChatReadException>(() => WeChatTranscriptRecord.Parse("unrecognized text"));
    }

    [Fact]
    public void ExtractWritesEntriesIntoAnEmptyDestination()
    {
        using var fixture = new TempInbox();
        var destination = Path.Combine(fixture.Root, "extract");
        Directory.CreateDirectory(destination);

        var names = WeChatNativeArchive.Extract(StoredZip, destination);

        Assert.Equal(new[] { "聊天记录.txt", "images/photo.png" }, names);
        Assert.True(File.Exists(Path.Combine(destination, "聊天记录.txt")));
        Assert.True(File.Exists(Path.Combine(destination, "images", "photo.png")));
    }

    [Fact]
    public void InsightsReaderCollectsSendersAndEndFromZipPaths()
    {
        using var fixture = new TempInbox();
        var zip = Path.Combine(fixture.Root, "archive.zip");
        File.WriteAllBytes(zip, StoredZip);
        var ignored = fixture.WriteSource("notes.txt", "not a zip");

        var insights = WeChatBatchInsightsReader.Read([zip, ignored]);

        Assert.True(insights.Senders.Count == 2);
        Assert.NotNull(insights.End);
    }
}
