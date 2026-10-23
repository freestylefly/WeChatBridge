import WeChatBridgeCore
import Foundation
import XCTest

/// 「发送到自定义」: the one Share-menu entry whose destination is not known
/// until the user picks it.
///
/// Everything here is about the seam that carries that pick — the list the
/// settings pane writes, the intent a share arrives with, and the batch state
/// that has to keep naming the app after the intent is gone. The intent is
/// still schema 1, so an app and an extension of different vintages have to
/// keep working together; that is what most of these assert.
final class ForwardTargetTests: XCTestCase {
    private var temporary: TemporaryInbox!

    override func setUp() {
        super.setUp()
        temporary = TemporaryInbox()
    }

    override func tearDown() {
        temporary.tearDown()
        super.tearDown()
    }

    /// Whole seconds, because `addedAt` is written as ISO-8601 — a readable
    /// timestamp is worth more here than the microseconds `Date()` carries, and
    /// a test built on `Date()` would compare a value against its own rounding.
    private func fixedDate(_ offset: TimeInterval = 0) -> Date {
        Date(timeIntervalSince1970: 1_772_000_000 + offset)
    }

    // MARK: - The value

    func testATargetSurvivesTheCodingRoundTrip() throws {
        let target = ForwardTarget(
            bundleIdentifier: "com.apple.TextEdit",
            displayName: "TextEdit",
            addedAt: Date(timeIntervalSince1970: 1_772_000_000)
        )
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601

        let data = try encoder.encode(target)
        XCTAssertEqual(try decoder.decode(ForwardTarget.self, from: data), target)
        // The identity the whole feature is keyed on: two rows for one app would
        // both be the same destination.
        XCTAssertEqual(target.id, target.bundleIdentifier)
    }

    // MARK: - The shared list

    /// The blob in the app group's preferences, exactly as an older build that
    /// shared it with the extension wrote it.
    func testTheListSurvivesTheBlobTheTwoProcessesShare() throws {
        let targets = [
            ForwardTarget(bundleIdentifier: "com.apple.TextEdit", displayName: "TextEdit", addedAt: fixedDate()),
            ForwardTarget(bundleIdentifier: "com.apple.Notes", displayName: "备忘录", addedAt: fixedDate(60)),
        ]
        let data = try XCTUnwrap(ForwardTargetStore.encode(targets))
        XCTAssertEqual(ForwardTargetStore.decode(data), targets)
        // Written by a build that predates a field, or truncated by a crash: the
        // share panel still has to open.
        XCTAssertTrue(ForwardTargetStore.decode(Data("not json".utf8)).isEmpty)
        XCTAssertTrue(ForwardTargetStore.decode(Data()).isEmpty)
    }

    /// The list a build before 只粘贴文件路径 wrote has no such key, and it
    /// meant "paste the files" — which is what the missing key has to mean.
    func testAListWrittenBeforePathOnlyExistedStillDecodes() throws {
        let json = """
        [{"addedAt":"2026-02-25T06:13:20Z","bundleIdentifier":"com.apple.TextEdit","displayName":"TextEdit"}]
        """
        let decoded = ForwardTargetStore.decode(Data(json.utf8))
        XCTAssertEqual(decoded.map(\.bundleIdentifier), ["com.apple.TextEdit"])
        XCTAssertEqual(decoded.map(\.pastesPathOnly), [false])

        // And once set, it survives the blob like everything else does.
        let terminal = ForwardTarget(
            bundleIdentifier: "com.apple.Terminal",
            displayName: "终端",
            addedAt: fixedDate(),
            pastesPathOnly: true
        )
        let data = try XCTUnwrap(ForwardTargetStore.encode([terminal]))
        XCTAssertEqual(ForwardTargetStore.decode(data), [terminal])
        XCTAssertEqual(ForwardTargetStore.decode(data).first?.pastesPathOnly, true)
    }

    /// What a terminal receives instead of a file: the path, quoted the way a
    /// drop on the same window would have typed it, with room to keep typing.
    func testAPathIsQuotedForAShell() {
        let plain = URL(fileURLWithPath: "/tmp/a.zip")
        let awkward = URL(fileURLWithPath: "/Users/q/Group Containers/聊天记录 (1)/it's.zip")

        XCTAssertEqual(FilePasteboard.shellLine(for: [plain]), "'/tmp/a.zip' ")
        XCTAssertEqual(
            FilePasteboard.shellLine(for: [plain, awkward]),
            "'/tmp/a.zip' '/Users/q/Group Containers/聊天记录 (1)/it'\\''s.zip' "
        )
        XCTAssertEqual(FilePasteboard.shellLine(for: []), "")
    }

    /// The panel opens on the app used last, and Return picks the first row.
    func testTheLastUsedTargetIsMovedToTheTop() {
        let first = ForwardTarget(bundleIdentifier: "a.one", displayName: "One", addedAt: fixedDate())
        let second = ForwardTarget(bundleIdentifier: "a.two", displayName: "Two", addedAt: fixedDate(60))

        XCTAssertEqual(ForwardTargetStore.ordered([first, second], lastUsed: nil), [first, second])
        XCTAssertEqual(ForwardTargetStore.ordered([first, second], lastUsed: "a.two"), [second, first])
        // A pointer at an app that has since been removed from the list must not
        // drop it, duplicate it, or reorder anything.
        XCTAssertEqual(ForwardTargetStore.ordered([first], lastUsed: "a.two"), [first])
        XCTAssertTrue(ForwardTargetStore.ordered([], lastUsed: "a.two").isEmpty)
    }

    // MARK: - The intent

    func testAnIntentWithoutTargetFieldsIsStillReadable() throws {
        let batchID = try commitBatch(intent: BatchIntent(action: .codex, requestedAt: Date()))
        let url = intentURL(batchID)
        var json = try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as! [String: Any]
        // Exactly what an intent written by the shipped build looks like: the
        // schema version stayed 1 when the fields were added, so this is the
        // file a mixed install produces every day.
        json.removeValue(forKey: "targetBundleIdentifier")
        json.removeValue(forKey: "targetDisplayName")
        XCTAssertEqual(json["schemaVersion"] as? Int, 1)
        try JSONSerialization.data(withJSONObject: json).write(to: url)

        guard case .ready(let intent) = temporary.reader.consumeIntent(forBatch: batchID) else {
            return XCTFail("expected a fresh intent")
        }
        XCTAssertEqual(intent.action, .codex)
        XCTAssertNil(intent.targetBundleIdentifier)
        XCTAssertNil(intent.target)
    }

    func testACustomIntentCarriesTheChosenApp() throws {
        let target = ForwardTarget(
            bundleIdentifier: "com.apple.TextEdit",
            displayName: "TextEdit",
            addedAt: Date()
        )
        let batchID = try commitBatch(
            intent: BatchIntent(action: .custom, requestedAt: Date(), target: target)
        )

        guard case .ready(let intent) = temporary.reader.consumeIntent(forBatch: batchID) else {
            return XCTFail("expected a fresh intent")
        }
        XCTAssertEqual(intent.action, .custom)
        XCTAssertEqual(intent.targetBundleIdentifier, target.bundleIdentifier)
        XCTAssertEqual(intent.targetDisplayName, target.displayName)
        // Rebuilt rather than stored whole: this is what `ActionRunner` acts on.
        XCTAssertEqual(intent.target?.bundleIdentifier, target.bundleIdentifier)
        XCTAssertEqual(intent.target?.displayName, target.displayName)
    }

    /// A target named only in the intent would be gone the moment the forward
    /// ran, and 记录 would read 「发送到自定义」 for the rest of the batch's life.
    func testTheChosenAppIsCopiedIntoTheBatchStateBeforeTheIntentIsConsumed() throws {
        let target = ForwardTarget(bundleIdentifier: "com.todesktop.cursor", displayName: "Cursor", addedAt: Date())
        try temporary.commitBatch(
            names: ["聊天记录.zip"],
            action: .custom,
            intent: BatchIntent(action: .custom, requestedAt: Date(), target: target)
        )

        let batch = try XCTUnwrap(temporary.reader.loadBatches().first)
        XCTAssertEqual(batch.action, .custom)
        XCTAssertEqual(batch.targetName, "Cursor")
        XCTAssertEqual(HistoryLabel.destination(for: batch), "发给 Cursor")

        // And it outlives the request, which is deleted as it is read.
        _ = temporary.reader.consumeIntent(forBatch: batch.id)
        let reloaded = try XCTUnwrap(temporary.reader.loadBatches().first)
        XCTAssertEqual(reloaded.targetName, "Cursor")
    }

    /// A forward replayed from 记录 has no intent at all; the app names the app
    /// it went to when it records the outcome.
    func testRecordingAnOutcomeCanNameTheAppButNeverClearsIt() throws {
        let directory = try temporary.commitBatch(names: ["聊天记录.zip"], action: .custom)
        let batchID = temporary.batchID(of: directory)

        try temporary.reader.recordOutcome(
            BatchOutcome(kind: .delivered, at: Date()),
            targetName: "Cursor",
            for: batchID
        )
        XCTAssertEqual(temporary.reader.state(forBatch: batchID)?.targetName, "Cursor")

        // Every other caller passes nothing, and must leave the name alone.
        try temporary.reader.recordOutcome(BatchOutcome(kind: .failed, at: Date()), for: batchID)
        XCTAssertEqual(temporary.reader.state(forBatch: batchID)?.targetName, "Cursor")
    }

    func testABatchWithNoTargetIsNamedByItsEntry() throws {
        try temporary.commitBatch(names: ["聊天记录.zip"], action: .clipboard)
        let batch = try XCTUnwrap(temporary.reader.loadBatches().first)
        XCTAssertEqual(HistoryLabel.destination(for: batch), ShareAction.clipboard.entryTitle)
    }

    // MARK: - Helpers

    @discardableResult
    private func commitBatch(intent: BatchIntent?) throws -> UUID {
        let directory = try temporary.commitBatch(names: ["聊天记录.zip"], intent: intent)
        return temporary.batchID(of: directory)
    }

    private func intentURL(_ batchID: UUID) -> URL {
        temporary.inbox.ready
            .appendingPathComponent(batchID.uuidString)
            .appendingPathComponent(BatchIntent.fileName)
    }
}
