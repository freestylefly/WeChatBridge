import WeChatBridgeCore
import Foundation
import XCTest

final class ShareActionTests: XCTestCase {
    private var temporary: TemporaryInbox!

    override func setUp() {
        super.setUp()
        temporary = TemporaryInbox()
    }

    override func tearDown() {
        temporary.tearDown()
        super.tearDown()
    }

    func testOnlyForwardingActionsNameATargetApp() {
        XCTAssertEqual(ShareAction.allCases.count, 12)
        XCTAssertEqual(ShareAction.codex.targetBundleIdentifier, "com.openai.codex")
        XCTAssertEqual(ShareAction.claude.targetBundleIdentifier, "com.anthropic.claudefordesktop")
        XCTAssertEqual(ShareAction.doubao.targetBundleIdentifier, "com.bot.pc.doubao")
        XCTAssertEqual(ShareAction.qwen.targetBundleIdentifier, "com.alibaba.qwenwork")
        XCTAssertEqual(ShareAction.workBuddy.targetBundleIdentifier, "com.tencent.workbuddy.mac")
        XCTAssertEqual(ShareAction.weSight.targetBundleIdentifier, "ai.wesight.app")
        XCTAssertEqual(ShareAction.deepSeekHarness.targetBundleIdentifier, "com.deepseek.dsh")
        XCTAssertEqual(ShareAction.obsidian.targetBundleIdentifier, "md.obsidian")
        XCTAssertNil(ShareAction.clipboard.targetBundleIdentifier)
        // 「沉淀到文件夹」 delivers to a path, not an app.
        XCTAssertNil(ShareAction.folder.targetBundleIdentifier)
        // 「发送到自定义」 names no app of its own: the one it goes to is chosen
        // in the share panel and travels in the intent.
        XCTAssertNil(ShareAction.custom.targetBundleIdentifier)
    }

    /// The folder entry travels in manifests and intents like every other
    /// action: a build that cannot decode `folder` would drop the request, so
    /// the round trip is pinned here.
    func testFolderActionRoundTripsThroughCodable() throws {
        let data = try JSONEncoder().encode(ShareAction.folder)
        XCTAssertEqual(String(data: data, encoding: .utf8), "\"folder\"")
        XCTAssertEqual(try JSONDecoder().decode(ShareAction.self, from: data), .folder)

        let manifest = BatchManifest(
            batchID: UUID(),
            createdAt: Date(),
            items: [],
            action: .folder
        )
        let manifestData = try BatchManifest.encoder().encode(manifest)
        XCTAssertEqual(try BatchManifest.decoder().decode(BatchManifest.self, from: manifestData).action, .folder)
    }

    func testAnIntentIsReadableExactlyOnce() throws {
        let batchID = try commitBatch(intent: BatchIntent(action: .codex, requestedAt: Date()))

        guard case .ready(let intent) = temporary.reader.consumeIntent(forBatch: batchID) else {
            return XCTFail("expected a fresh intent")
        }
        XCTAssertEqual(intent.action, .codex)
        // The second read is the one that matters: without consumption a rescan,
        // a relaunch or a second window would replay the forward.
        XCTAssertEqual(temporary.reader.consumeIntent(forBatch: batchID), .none)
    }

    func testAStaleIntentIsReportedAsExpiredRatherThanCarriedOut() throws {
        let old = Date(timeIntervalSinceNow: -BatchIntent.freshnessWindow - 60)
        let batchID = try commitBatch(intent: BatchIntent(action: .claude, requestedAt: old))

        // Reported as its own case — pasting into whatever is frontmost hours
        // later is worse than doing nothing, but the history still has to be
        // able to say the forward never happened — and still removed from disk.
        let result = temporary.reader.consumeIntent(forBatch: batchID)
        guard case .expired(let intent) = result else {
            return XCTFail("expected an expired intent, got \(result)")
        }
        XCTAssertEqual(intent.action, .claude)
        XCTAssertFalse(FileManager.default.fileExists(atPath: intentURL(batchID).path))
    }

    /// The clipboard entry writes no request: the extension finishes it before
    /// the app launches.
    func testOnlyForwardsWriteAnIntent() {
        XCTAssertFalse(ShareAction.clipboard.needsIntent)
        for action in [
            ShareAction.codex, .claude, .doubao, .qwen, .workBuddy,
            .weSight, .deepSeekHarness, .obsidian, .folder, .custom,
        ] {
            XCTAssertTrue(action.needsIntent, "\(action)")
        }
    }

    func testABatchWithoutAnIntentReadsAsNoRequest() throws {
        let batchID = try commitBatch(intent: nil)
        XCTAssertEqual(temporary.reader.consumeIntent(forBatch: batchID), .none)
    }

    func testAnIntentFromANewerSchemaIsNotGuessedAt() throws {
        let batchID = try commitBatch(intent: BatchIntent(action: .codex, requestedAt: Date()))
        let url = intentURL(batchID)
        var json = try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as! [String: Any]
        json["schemaVersion"] = BatchIntent.currentSchemaVersion + 1
        try JSONSerialization.data(withJSONObject: json).write(to: url)

        XCTAssertEqual(temporary.reader.consumeIntent(forBatch: batchID), .none)
    }

    func testTheBatchItselfSurvivesItsIntentBeingConsumed() throws {
        let batchID = try commitBatch(intent: BatchIntent(action: .clipboard, requestedAt: Date()))
        _ = temporary.reader.consumeIntent(forBatch: batchID)

        // A forward that already happened must not take the files with it: the
        // same archive is still in the history to send again.
        XCTAssertEqual(temporary.reader.loadBatches().flatMap(\.items).count, 1)
    }

    // MARK: - Helpers

    private func intentURL(_ batchID: UUID) -> URL {
        temporary.inbox.ready
            .appendingPathComponent(batchID.uuidString, isDirectory: true)
            .appendingPathComponent(BatchIntent.fileName)
    }

    private func commitBatch(intent: BatchIntent?) throws -> UUID {
        let directory = try temporary.commitBatch(
            names: ["聊天记录.zip"],
            action: intent?.action ?? .clipboard,
            intent: intent
        )
        return temporary.batchID(of: directory)
    }
}
