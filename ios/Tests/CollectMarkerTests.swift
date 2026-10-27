import WeChatBridgeCore
import XCTest
@testable import WeChatBridgeiOS

final class CollectMarkerTests: XCTestCase {
    private var root: URL!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("collect-marker-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    override func tearDown() {
        try? FileManager.default.removeItem(at: root)
    }

    private func batch(marked: Bool, created: Date) throws -> (UUID, URL) {
        let id = UUID()
        let directory = root.appendingPathComponent(id.uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        if marked { try CollectMarker.mark(directory) }
        try FileManager.default.setAttributes([.creationDate: created], ofItemAtPath: directory.path)
        return (id, directory)
    }

    func testUnseenMarkedBatchesComeOldestFirst() throws {
        let now = Date()
        let (late, lateURL) = try batch(marked: true, created: now)
        let (early, earlyURL) = try batch(marked: true, created: now.addingTimeInterval(-60))
        let (_, plainURL) = try batch(marked: false, created: now.addingTimeInterval(-30))
        let (seen, seenURL) = try batch(marked: true, created: now.addingTimeInterval(-90))
        XCTAssertEqual(CollectMarker.unseen([lateURL, plainURL, earlyURL, seenURL], seen: [seen]), [early, late])
    }

    private func records(_ messages: [(String, String)]) throws -> [WeChatTranscriptRecord] {
        try WeChatTranscriptRecord.parse(messages.map { "·\($0.0)\n2026年10月20日 09:00\n\($0.1)\n" }.joined(separator: "\n"))
    }

    func testKeywordSkipsTagsAndKeepsTheFirstLine() throws {
        XCTAssertEqual(SearchAnchor.keyword(try records([("甲", "第一行\n第二行"), ("乙", "[图片] a.png"), ("丙", "[破涕为笑]")])), "第一行")
        XCTAssertNil(SearchAnchor.keyword(try records([("乙", "[图片] a.png")])))
        XCTAssertEqual(SearchAnchor.keyword(try records([("甲", String(repeating: "长", count: 50))]))?.count, 30)
    }
}
