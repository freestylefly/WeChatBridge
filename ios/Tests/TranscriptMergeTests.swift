import WeChatBridgeCore
import XCTest
@testable import WeChatBridgeiOS

final class TranscriptMergeTests: XCTestCase {
    private let shanghai = TimeZone(identifier: "Asia/Shanghai")!

    private func body(_ fixture: String) throws -> String {
        let data = try Data(contentsOf: FixtureBatches.url(fixture))
        return try XCTUnwrap(WeChatNativeArchive.transcript(data)).body
    }

    func testRenderRoundTrips() throws {
        let records = try WeChatTranscriptRecord.parse(body("group-a.zip"), timeZone: shanghai)
        XCTAssertEqual(records.count, 8)
        let rendered = TranscriptMerge.render(records, timeZone: shanghai)
        XCTAssertEqual(try WeChatTranscriptRecord.parse(rendered, timeZone: shanghai), records)
    }

    func testRenderMatchesWeChatLayout() throws {
        let records = try WeChatTranscriptRecord.parse("·甲\n2026年10月20日 09:00\n你好\n\n·乙\n2026年10月20日 09:01\n好\n", timeZone: shanghai)
        XCTAssertEqual(TranscriptMerge.render(records, timeZone: shanghai), "·甲\n2026年10月20日 09:00\n你好\n\n·乙\n2026年10月20日 09:01\n好\n")
    }

    func testInterleavedMergeIsTimeOrderedAndStable() throws {
        let merged = try TranscriptMerge.mergedRecords([body("group-a.zip"), body("interleave.zip")], timeZone: shanghai)
        XCTAssertEqual(merged.count, 10)
        XCTAssertEqual(merged.map(\.date), merged.map(\.date).sorted())
        // Same minute: the first export's message stays first.
        XCTAssertEqual(merged[0].sender, "甲")
        XCTAssertEqual(merged[1].text, "插在第一条之后")
        XCTAssertEqual(merged[4].sender, "甲")
        XCTAssertEqual(merged[5].text, "插在中间")
    }

    func testDuplicateExportMergesToSameCount() throws {
        let once = try body("group-a.zip")
        let merged = try TranscriptMerge.mergedRecords([once, once], timeZone: shanghai)
        XCTAssertEqual(merged, try WeChatTranscriptRecord.parse(once, timeZone: shanghai))
    }
}
