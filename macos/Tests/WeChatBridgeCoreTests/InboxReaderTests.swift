import WeChatBridgeCore
import Foundation
import XCTest

final class InboxReaderTests: XCTestCase {
    private var temporary: TemporaryInbox!

    override func setUp() {
        super.setUp()
        temporary = TemporaryInbox()
    }

    override func tearDown() {
        temporary.tearDown()
        super.tearDown()
    }

    func testBatchesComeBackNewestFirst() throws {
        let old = Date(timeIntervalSince1970: 1_000_000)
        let recent = Date(timeIntervalSince1970: 2_000_000)
        try temporary.commitBatch(names: ["old.zip"], createdAt: old)
        try temporary.commitBatch(names: ["recent.zip"], createdAt: recent)

        let batches = temporary.reader.loadBatches()
        XCTAssertEqual(batches.map { $0.items.first?.displayName }, ["recent.zip", "old.zip"])
    }

    func testABatchWrittenByANewerSchemaIsSkippedRatherThanGuessedAt() throws {
        let destination = try temporary.commitBatch(names: ["future.zip"])
        let manifestURL = destination.appendingPathComponent("manifest.json")
        var json = try JSONSerialization.jsonObject(with: Data(contentsOf: manifestURL)) as! [String: Any]
        json["schemaVersion"] = BatchManifest.currentSchemaVersion + 1
        try JSONSerialization.data(withJSONObject: json).write(to: manifestURL)

        XCTAssertEqual(temporary.reader.loadBatches(), [])
    }

    func testAManifestEntryWithoutItsFileIsNotAdvertised() throws {
        let destination = try temporary.commitBatch(names: ["gone.zip", "kept.zip"])
        let items = temporary.reader.loadBatches().flatMap(\.items)
        let gone = try XCTUnwrap(items.first { $0.displayName == "gone.zip" })
        try FileManager.default.removeItem(at: gone.url)

        let remaining = temporary.reader.loadBatches().flatMap(\.items)
        XCTAssertEqual(remaining.map(\.displayName), ["kept.zip"])
        XCTAssertTrue(FileManager.default.fileExists(atPath: destination.path))
    }

    func testRemovingOneItemRewritesTheManifestAndKeepsTheRest() throws {
        try temporary.commitBatch(names: ["a.zip", "b.zip"])
        let items = temporary.reader.loadBatches().flatMap(\.items)
        let first = try XCTUnwrap(items.first { $0.displayName == "a.zip" })

        try temporary.reader.discard(item: first)

        let remaining = temporary.reader.loadBatches().flatMap(\.items)
        XCTAssertEqual(remaining.map(\.displayName), ["b.zip"])
        XCTAssertFalse(FileManager.default.fileExists(atPath: first.url.path))
    }

    func testRemovingTheLastItemRemovesTheBatchDirectory() throws {
        let destination = try temporary.commitBatch(names: ["only.zip"])
        let item = try XCTUnwrap(temporary.reader.loadBatches().flatMap(\.items).first)

        try temporary.reader.discard(item: item)

        XCTAssertEqual(temporary.reader.loadBatches(), [])
        XCTAssertFalse(FileManager.default.fileExists(atPath: destination.path))
    }

    func testDiscardAllEmptiesReady() throws {
        try temporary.commitBatch(names: ["a.zip"])
        try temporary.commitBatch(names: ["b.zip", "c.zip"])

        try temporary.reader.discardAll()

        XCTAssertEqual(temporary.reader.loadBatches(), [])
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: temporary.inbox.ready.path), [])
    }
}
