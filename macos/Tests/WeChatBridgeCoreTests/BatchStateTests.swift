import WeChatBridgeCore
import Foundation
import XCTest

/// The app-owned half of a batch: what became of the share. All of it is on
/// disk, because the answer has to survive a relaunch.
final class BatchStateTests: XCTestCase {
    private var temporary: TemporaryInbox!

    override func setUp() {
        super.setUp()
        temporary = TemporaryInbox()
    }

    override func tearDown() {
        temporary.tearDown()
        super.tearDown()
    }

    // MARK: - Manifest

    func testAManifestWithoutAnActionFieldReadsAsClipboard() throws {
        let directory = try temporary.commitBatch(names: ["旧的.zip"], action: .claude)
        try stripActionFromManifest(at: directory)

        let batch = try XCTUnwrap(temporary.reader.loadBatches().first)
        XCTAssertEqual(batch.action, .clipboard)
        XCTAssertEqual(batch.items.map(\.action), [.clipboard])
    }

    func testAnActionSurvivesTheManifestRoundTrip() throws {
        let manifest = BatchManifest(
            batchID: UUID(),
            createdAt: Date(timeIntervalSince1970: 1_772_000_000),
            items: [],
            action: .codex
        )
        let data = try BatchManifest.encoder().encode(manifest)
        XCTAssertEqual(try BatchManifest.decoder().decode(BatchManifest.self, from: data), manifest)
    }

    func testALegacyShelfActionDecodesAsClipboard() throws {
        let data = Data(#""shelf""#.utf8)
        XCTAssertEqual(try BatchManifest.decoder().decode(ShareAction.self, from: data), .clipboard)
    }

    // MARK: - Initialisation

    func testAForwardBatchStartsWithoutAnOutcome() throws {
        try temporary.commitBatch(names: ["a.zip"], action: .claude)

        let batch = try XCTUnwrap(temporary.reader.loadBatches().first)
        XCTAssertNil(batch.outcome)
        XCTAssertTrue(
            FileManager.default.fileExists(
                atPath: batch.directory.appendingPathComponent(BatchState.fileName).path
            ),
            "the state must be on disk, not only in the returned value"
        )
    }

    func testABatchIsOnlyEverFirstSeenOnce() throws {
        try temporary.commitBatch(names: ["a.zip"], action: .claude)

        XCTAssertEqual(temporary.reader.loadBatches().map(\.isFirstSeen), [true])
        XCTAssertEqual(temporary.reader.loadBatches().map(\.isFirstSeen), [false])
    }

    func testAClipboardOutcomeUsesTheShareTime() throws {
        let created = Date(timeIntervalSince1970: 1_700_000_000)
        try temporary.commitBatch(names: ["a.zip"], createdAt: created, action: .clipboard)

        let batch = try XCTUnwrap(temporary.reader.loadBatches().first)
        XCTAssertEqual(batch.outcome?.kind, .copied)
        XCTAssertEqual(batch.outcome?.at, created)
    }

    // MARK: - Outcome

    func testAnOutcomeSurvivesAReload() throws {
        try temporary.commitBatch(names: ["a.zip"], action: .clipboard)
        let batch = try XCTUnwrap(temporary.reader.loadBatches().first)
        let at = Date(timeIntervalSince1970: 1_772_000_000)

        try temporary.reader.recordOutcome(BatchOutcome(kind: .copied, at: at), for: batch.id)

        let reloaded = try XCTUnwrap(temporary.reader.loadBatches().first)
        XCTAssertEqual(reloaded.outcome?.kind, .copied)
        XCTAssertEqual(reloaded.outcome?.at, at)
        XCTAssertNil(reloaded.outcome?.detail)
    }

    func testTrashingOneItemKeepsTheRest() throws {
        try temporary.commitBatch(names: ["a.zip", "b.zip"], action: .claude)
        let batch = try XCTUnwrap(temporary.reader.loadBatches().first)

        try temporary.reader.discard(item: batch.items[0])

        let reloaded = try XCTUnwrap(temporary.reader.loadBatches().first)
        XCTAssertEqual(reloaded.items.map(\.displayName), ["b.zip"])
    }

    // MARK: - Pruning

    func testPruningRemovesOnlyBatchesPastTheWindow() throws {
        let now = Date()
        let week: TimeInterval = 7 * 24 * 60 * 60
        try temporary.commitBatch(
            names: ["old.zip"],
            createdAt: now.addingTimeInterval(-week - 60),
            action: .claude
        )
        try temporary.commitBatch(
            names: ["fresh.zip"],
            createdAt: now.addingTimeInterval(-60),
            action: .claude
        )

        XCTAssertEqual(temporary.reader.pruneHistory(olderThan: week, now: now), 1)
        XCTAssertEqual(
            temporary.reader.loadBatches().flatMap(\.items).map(\.displayName),
            ["fresh.zip"]
        )
    }

    func testABatchExactlyAtTheWindowIsKept() throws {
        let now = Date(timeIntervalSince1970: 1_772_000_000)
        let day: TimeInterval = 24 * 60 * 60
        try temporary.commitBatch(names: ["edge.zip"], createdAt: now.addingTimeInterval(-day), action: .codex)

        XCTAssertEqual(temporary.reader.pruneHistory(olderThan: day, now: now), 0)
        XCTAssertEqual(temporary.reader.loadBatches().count, 1)
    }

    func testAZeroWindowMeansKeepForever() throws {
        try temporary.commitBatch(
            names: ["ancient.zip"],
            createdAt: Date(timeIntervalSince1970: 0),
            action: .clipboard
        )

        XCTAssertEqual(temporary.reader.pruneHistory(olderThan: 0), 0)
        XCTAssertEqual(temporary.reader.loadBatches().count, 1)
    }

    // MARK: - Legacy manifests

    func testALegacyManifestWithALiveIntentUsesThatIntent() throws {
        let directory = try temporary.commitBatch(
            names: ["转发过的.zip"],
            action: .claude,
            intent: BatchIntent(action: .claude, requestedAt: Date())
        )
        try stripActionFromManifest(at: directory)

        let batch = try XCTUnwrap(temporary.reader.loadBatches().first)
        XCTAssertEqual(batch.action, .claude)
    }

    func testTheResolvedActionOutlivesTheConsumedIntent() throws {
        let directory = try temporary.commitBatch(
            names: ["转发过的.zip"],
            action: .codex,
            intent: BatchIntent(action: .codex, requestedAt: Date())
        )
        try stripActionFromManifest(at: directory)

        let batch = try XCTUnwrap(temporary.reader.loadBatches().first)
        _ = temporary.reader.consumeIntent(forBatch: batch.id)

        XCTAssertEqual(temporary.reader.loadBatches().first?.action, .codex)
    }

    // MARK: - Debris

    func testAnUnreadableBatchIsSweptOnceItAges() throws {
        let directory = try temporary.commitBatch(names: ["坏掉的.zip"], action: .clipboard)
        try Data("{".utf8).write(to: directory.appendingPathComponent("manifest.json"))

        let later = Date().addingTimeInterval(3600)
        XCTAssertEqual(temporary.reader.pruneHistory(olderThan: 60, now: later), 1)
        XCTAssertFalse(FileManager.default.fileExists(atPath: directory.path))
    }

    func testABatchFromANewerSchemaIsNeverSwept() throws {
        let directory = try temporary.commitBatch(
            names: ["未来的.zip"],
            createdAt: Date(timeIntervalSince1970: 0),
            action: .clipboard
        )
        let url = directory.appendingPathComponent("manifest.json")
        var json = try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as! [String: Any]
        json["schemaVersion"] = BatchManifest.currentSchemaVersion + 1
        try JSONSerialization.data(withJSONObject: json).write(to: url)

        XCTAssertEqual(temporary.reader.pruneHistory(olderThan: 60, now: Date().addingTimeInterval(3600)), 0)
        XCTAssertTrue(FileManager.default.fileExists(atPath: directory.path))
    }

    func testCountingBytesDoesNotSpendTheFirstSeenSignal() throws {
        try temporary.commitBatch(names: ["未读的.zip"], action: .clipboard)

        XCTAssertEqual(temporary.reader.totalByteCount(), 16)
        XCTAssertEqual(temporary.reader.pruneHistory(olderThan: 24 * 60 * 60), 0)
        XCTAssertEqual(temporary.reader.loadBatches().first?.isFirstSeen, true)
    }

    private func stripActionFromManifest(at directory: URL) throws {
        let url = directory.appendingPathComponent("manifest.json")
        var json = try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as! [String: Any]
        json.removeValue(forKey: "action")
        try JSONSerialization.data(withJSONObject: json).write(to: url)
    }

    // MARK: - Size

    func testTotalByteCountAddsUpEveryBatch() throws {
        try temporary.commitBatch(names: ["a.zip", "b.zip"], action: .clipboard)
        try temporary.commitBatch(names: ["c.zip"], action: .codex)

        XCTAssertEqual(temporary.reader.totalByteCount(), 48)
    }

    func testTotalByteCountIsZeroOnAnEmptyInbox() {
        XCTAssertEqual(temporary.reader.totalByteCount(), 0)
    }
}
