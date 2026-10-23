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
    }

    func testNonWeChatZipIsParseFailed() throws {
        let summary = RelaySummary.make(for: try fixtures.batch("not-wechat.zip"))
        XCTAssertTrue(summary.parseFailed)
        XCTAssertNil(summary.messageCount)
        XCTAssertEqual(summary.title, "not-wechat.zip")
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
}
