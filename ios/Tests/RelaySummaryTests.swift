import WeChatBridgeCore
import XCTest
@testable import WeChatBridgeiOS

final class RelaySummaryTests: XCTestCase {
    private var fixtures: FixtureBatches!

    override func setUpWithError() throws {
        fixtures = try FixtureBatches()
    }

    override func tearDown() {
        fixtures = nil
    }

    func testGroupSummary() throws {
        let batch = try fixtures.batch("group-a.zip")
        defer { RelayFiles.remove(batchID: batch.id) }
        let summary = RelaySummary.make(for: batch)
        XCTAssertFalse(summary.parseFailed)
        XCTAssertEqual(summary.messageCount, 8)
        XCTAssertEqual(summary.senders, ["甲", "乙", "丙"])
        XCTAssertEqual(summary.mediaCount, 3)
        XCTAssertEqual(summary.preview.count, 2)
        XCTAssertEqual(summary.preview[0], "甲：早上好，今天讨论发布计划")
        XCTAssertNotNil(summary.start)
        XCTAssertEqual(summary.end!.timeIntervalSince(summary.start!), 7 * 60)
        XCTAssertEqual(summary.archiveItemID, batch.items[0].id)
        // The last message is a sticker; search needs the last typed words.
        XCTAssertEqual(summary.anchor, "第一行")
        XCTAssertEqual(summary.last?.sender, "甲")
        XCTAssertFalse(summary.hitsForwardLimit)
    }

    func testZipWithoutTextIsParseFailed() throws {
        let batch = try fixtures.batch("files-only.zip")
        defer { RelayFiles.remove(batchID: batch.id) }
        let summary = RelaySummary.make(for: batch)
        XCTAssertEqual(summary.kind, .files)
        XCTAssertTrue(summary.parseFailed)
        XCTAssertNil(summary.messageCount)
        XCTAssertEqual(summary.title, "files-only.zip")
        XCTAssertEqual(RelayCardView.counts(summary), "1 个附件")
    }

    /// A single exported message: `·` heading, then lines, but no timestamps.
    func testTextInAnotherLayoutIsReadAsText() throws {
        let batch = try fixtures.batch("text-note.zip")
        defer { RelayFiles.remove(batchID: batch.id) }
        let summary = RelaySummary.make(for: batch)
        XCTAssertEqual(summary.kind, .text)
        XCTAssertFalse(summary.parseFailed)
        XCTAssertEqual(summary.title, "示例收藏 - 资料合集 第一期 10月23日更新")
        XCTAssertEqual(summary.preview, ["公开课讲义合集", "https://example.com/s/a1"])
        XCTAssertNil(summary.messageCount)
        XCTAssertEqual(summary.mediaCount, 0)
        XCTAssertEqual(RelayCardView.counts(summary), "文本")

        guard case .success(let loaded) = LoadedTranscript.load(batch) else { return XCTFail("text did not load") }
        XCTAssertTrue(loaded.records.isEmpty)
        XCTAssertEqual(loaded.text?.hasPrefix("·示例收藏"), true)
    }

    func testChatExportWinsOverPlainText() throws {
        let batch = try fixtures.batch("text-note.zip", "group-a.zip")
        defer { RelayFiles.remove(batchID: batch.id) }
        let summary = RelaySummary.make(for: batch)
        XCTAssertEqual(summary.kind, .chat)
        XCTAssertEqual(summary.archiveItemID, batch.items[1].id)
        XCTAssertEqual(RelayCardView.counts(summary), "8 条消息 · 4 个附件")
    }

    func testPlainTextLinksAreLive() {
        let text = PlainTextView.linked("工具清单\nhttps://example.com/s/c3")
        XCTAssertEqual(text.runs.compactMap(\.link), [URL(string: "https://example.com/s/c3")!])
    }

    func testCacheRoundTrip() throws {
        let batch = try fixtures.batch("interleave.zip")
        defer { RelayFiles.remove(batchID: batch.id) }
        let summary = RelaySummary.make(for: batch)
        summary.store(in: batch.directory)
        XCTAssertEqual(RelaySummary.cached(in: batch.directory), summary)
    }

    func testExtractionIsReused() throws {
        let batch = try fixtures.batch("group-a.zip")
        defer { RelayFiles.remove(batchID: batch.id) }
        let data = try Data(contentsOf: batch.items[0].url)
        let first = try RelayFiles.extract(data, batchID: batch.id)
        let second = try RelayFiles.extract(data, batchID: batch.id)
        XCTAssertEqual(first.names, second.names)
        XCTAssertTrue(FileManager.default.fileExists(atPath: first.directory.appendingPathComponent("聊天记录.txt").path))
    }

    func testRangeFormatting() {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = .current
        let start = calendar.date(from: DateComponents(year: 2026, month: 10, day: 20, hour: 9, minute: 0))!
        XCTAssertEqual(RelayFormat.range(start, start.addingTimeInterval(420), calendar: calendar), "10月20日 09:00–09:07")
        XCTAssertEqual(RelayFormat.range(start, start.addingTimeInterval(86_400), calendar: calendar), "10月20日 09:00–10月21日 09:00")
        XCTAssertNil(RelayFormat.range(nil, nil))
    }

    func testTextCardShowsOneLinkAsItsDomain() {
        let summary = RelaySummary.make(text: "合集\n讲义\nhttps://example.com/s/a1", archiveName: "a.txt", mediaCount: 0, archiveItemID: nil)
        XCTAssertEqual(RelayCardView.previewLines(summary), ["讲义", "🔗 example.com"])
        let twoLinks = RelaySummary.make(text: "合集\nhttps://example.com/1\nhttps://example.org/2", archiveName: "a.txt", mediaCount: 0, archiveItemID: nil)
        XCTAssertEqual(RelayCardView.previewLines(twoLinks), ["🔗 example.com"])
    }
}
