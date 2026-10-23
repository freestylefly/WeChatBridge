import AppKit
import Foundation
import XCTest
import SwiftUI
import os
import UserNotifications
@testable import WeChatBridgeApp
import WeChatBridgeCore

final class CollectionAppTests: XCTestCase {
    @MainActor
    func testKnowledgeDeliverySeparatesChatsWithIdenticalParticipants() async throws {
        for action in [ShareAction.obsidian, .folder] {
            let root = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent(UUID().uuidString)
            defer { try? FileManager.default.removeItem(at: root) }
            let prefs = preferences()
            let model = AppModel(preferences: prefs)
            model.start(inbox: Inbox(root: root), watch: false, removal: .delete)
            let destination = root.appendingPathComponent("Notes")
            try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: true)
            prefs.obsidianVaultPath = destination.path
            prefs.folderDeliveryPath = destination.path
            let first = try share(in: model.inbox!, action: .collect)
            let second = try share(in: model.inbox!, action: .collect)
            model.reload()
            XCTAssertTrue(model.setBatchConversation(first, name: "研发群"))
            XCTAssertTrue(model.setBatchConversation(second, name: "客户群"))
            let group = try XCTUnwrap(model.collectionLedger.current)
            let urls = try XCTUnwrap(model.freezeCollection(group.id))
            let runner = collectionRunner(model: model, preferences: prefs)
            let succeeded: Bool = await withCheckedContinuation { continuation in
                runner.deliverCollection(ArrivedBatch(action: action, urls: urls), scene: nil) { success, _ in
                    continuation.resume(returning: success)
                }
            }
            XCTAssertTrue(succeeded, "Successful writes must be recognized even within the same second")
            let notes = destination.appendingPathComponent("微信流")
            let names = try FileManager.default.contentsOfDirectory(atPath: notes.path).filter { $0.hasSuffix(".md") }
            XCTAssertEqual(Set(names), ["研发群的聊天.md", "客户群的聊天.md"])
            for name in ["研发群", "客户群"] {
                let text = try String(contentsOf: notes.appendingPathComponent(name + "的聊天.md"), encoding: .utf8)
                XCTAssertTrue(text.contains("chat: \"" + name + "\""))
                XCTAssertTrue(text.contains("messages: 100"))
            }
            XCTAssertEqual(model.batchConversation(first), "研发群")
            XCTAssertEqual(model.batchConversation(second), "客户群")
        }
    }

    @MainActor
    func testKnowledgeDeliveryMergesBatchesFromTheSameSavedChat() async throws {
        let root = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let prefs = preferences()
        let model = AppModel(preferences: prefs)
        model.start(inbox: Inbox(root: root), watch: false, removal: .delete)
        let destination = root.appendingPathComponent("Notes")
        try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: true)
        prefs.obsidianVaultPath = destination.path
        let first = try share(in: model.inbox!, action: .collect)
        let second = try share(in: model.inbox!, action: .collect, messageCount: 86)
        model.reload()
        XCTAssertTrue(model.setBatchConversation(first, name: "产品讨论群"))
        XCTAssertTrue(model.setBatchConversation(second, name: "产品讨论群"))
        let group = try XCTUnwrap(model.collectionLedger.current)
        let urls = try XCTUnwrap(model.freezeCollection(group.id))
        let runner = collectionRunner(model: model, preferences: prefs)
        let succeeded: Bool = await withCheckedContinuation { continuation in
            runner.deliverCollection(ArrivedBatch(action: .obsidian, urls: urls), scene: nil) { success, _ in
                continuation.resume(returning: success)
            }
        }
        XCTAssertTrue(succeeded)
        let notes = destination.appendingPathComponent("微信流")
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: notes.path).filter { $0.hasSuffix(".md") }, ["产品讨论群的聊天.md"])
        let text = try String(contentsOf: notes.appendingPathComponent("产品讨论群的聊天.md"), encoding: .utf8)
        // Both fixtures share 85 text messages. Their final video messages
        // have different timestamps, so the merged note has 101 unique records.
        XCTAssertTrue(text.contains("messages: 101"))
        XCTAssertEqual(text.components(separatedBy: "讨论消息 1：").count - 1, 1)
    }

    @MainActor
    func testGroupingPreservesSourceOrderAndRequiresEveryChatName() async throws {
        let root = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let model = AppModel(preferences: preferences())
        model.start(inbox: Inbox(root: root), watch: false, removal: .delete)
        let ids = try (0..<3).map { _ in try share(in: model.inbox!, action: .collect) }
        model.reload()
        let urls = try ids.map { try XCTUnwrap(model.batch(id: $0)?.items.first?.url) }
        XCTAssertTrue(model.setBatchConversation(ids[0], name: "研发群"))
        XCTAssertTrue(model.setBatchConversation(ids[2], name: "研发群"))
        XCTAssertNil(model.collectionConversations(for: urls))
        XCTAssertTrue(model.setBatchConversation(ids[1], name: "客户群"))
        let grouped = try XCTUnwrap(model.collectionConversations(for: urls))
        XCTAssertEqual(grouped.map(\.name), ["研发群", "客户群"])
        XCTAssertEqual(grouped.map(\.urls), [[urls[0], urls[2]], [urls[1]]])
        XCTAssertNil(model.collectionConversations(for: [root.appendingPathComponent("missing.zip")]))
    }

    @MainActor
    private func collectionRunner(model: AppModel, preferences: Preferences) -> ActionRunner {
        ActionRunner(model: model, authorization: AccessibilityAuthorization(), targets: ForwardTargets(),
                     preferences: preferences, sceneCoordinator: SceneCoordinator(preferences: preferences),
                     skills: SkillLibrary(), notifier: DeliveryNotifier(center: CollectionTestNotificationCenter()))
    }

    @MainActor
    func testAutomaticConversationRecognitionRunsWithoutForegroundGate() async throws {
        let root = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let model = AppModel(preferences: preferences(), readCollectionSource: { "研发群" })
        model.start(inbox: Inbox(root: root), watch: false)
        let id = try share(in: model.inbox!, action: .collect)
        model.reload()
        XCTAssertTrue(model.recognizingConversations.contains(id))
        try await Task.sleep(nanoseconds: 100_000_000)
        XCTAssertEqual(model.batchConversation(id), "研发群")
        XCTAssertFalse(model.recognizingConversations.contains(id))
    }

    @MainActor
    func testImportSnapshotSurvivesChangingChatsAndFailedRecognitionCanRetry() async throws {
        let root = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let title = OSAllocatedUnfairLock<String?>(initialState: "原始群")
        let model = AppModel(preferences: preferences(), readCollectionSource: { title.withLock { $0 } })
        model.start(inbox: Inbox(root: root), watch: false)
        let staging = try BatchStaging.create(in: model.inbox!)
        try Data().write(to: staging.directory.appendingPathComponent("collection-import"))
        model.reload()
        try await Task.sleep(nanoseconds: 100_000_000)
        title.withLock { $0 = "另一个群" }
        let itemID = UUID(), file = "original.zip"
        try Data("payload".utf8).write(to: staging.destination(for: itemID, displayName: file))
        let item = ManifestItem(id: itemID, displayName: file, relativePath: staging.relativePath(for: itemID, displayName: file), contentType: "public.zip-archive", byteCount: 7, itemIndex: 0, attachmentIndex: 0, loadStrategy: .fileRepresentation)
        _ = try staging.commit(manifest: .init(batchID: staging.batchID, createdAt: Date(), items: [item], action: .collect), diagnostics: nil, intent: nil, in: model.inbox!)
        model.reload()
        try await Task.sleep(nanoseconds: 100_000_000)
        XCTAssertEqual(model.batchConversation(staging.batchID), "原始群")
        title.withLock { $0 = nil }
        let failed = try share(in: model.inbox!, action: .collect)
        model.reload()
        try await Task.sleep(nanoseconds: 100_000_000)
        XCTAssertNil(model.batchConversation(failed))
        XCTAssertFalse(model.recognizingConversations.contains(failed))
        title.withLock { $0 = "当前群" }
        model.recognizeBatchConversation(failed)
        try await Task.sleep(nanoseconds: 100_000_000)
        XCTAssertEqual(model.batchConversation(failed), "当前群")
    }

    @MainActor
    func testReloadCollectsOnlyExplicitSharesAndRestartDoesNotReplayThem() async throws {
        let root = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let prefs = preferences()
        let model = AppModel(preferences: prefs)
        model.start(inbox: Inbox(root: root), watch: false, removal: .delete)
        let first = try share(in: model.inbox!, action: .collect)
        let direct = try share(in: model.inbox!, action: .codex)
        var arrivals = 0
        let subscription = model.didArrive.sink { _ in arrivals += 1 }
        defer { subscription.cancel() }
        model.reload()
        XCTAssertEqual(arrivals, 1)
        XCTAssertEqual(model.collectionLedger.current?.batchIDs, [first])
        XCTAssertFalse(model.collectionLedger.seenBatchIDs.contains(direct))
        model.reload()
        XCTAssertEqual(arrivals, 1)
        let restored = AppModel(preferences: prefs)
        restored.start(inbox: Inbox(root: root), watch: false)
        restored.reload()
        XCTAssertEqual(restored.collectionLedger.collections, model.collectionLedger.collections)
        XCTAssertEqual(restored.collectionLedger.current?.batchIDs, [first])
    }

    @MainActor
    func testMissingPayloadPreventsFreezeAndNewSharesAreIsolatedAfterFreeze() async throws {
        let root = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let model = AppModel(preferences: preferences())
        model.start(inbox: Inbox(root: root), watch: false)
        let first = try share(in: model.inbox!, action: .collect)
        model.reload()
        XCTAssertTrue(model.setBatchConversation(first, name: "测试群"))
        let group = try XCTUnwrap(model.collectionLedger.current)
        let source = try XCTUnwrap(model.batch(id: first)?.items.first?.url)
        let bytes = try Data(contentsOf: source)
        try FileManager.default.removeItem(at: source)
        XCTAssertNil(model.freezeCollection(group.id))
        XCTAssertEqual(model.collection(group.id)?.status, .collecting)
        try bytes.write(to: source)
        let snapshot = try XCTUnwrap(model.freezeCollection(group.id))
        let next = try share(in: model.inbox!, action: .collect)
        model.reload()
        XCTAssertEqual(snapshot, [source])
        XCTAssertEqual(model.collectionLedger.current?.batchIDs, [next])
        XCTAssertEqual(model.collection(group.id)?.batchIDs, [first])
        model.discard(batchID: first)
        XCTAssertTrue(FileManager.default.fileExists(atPath: source.path))
    }

    @MainActor
    func testNativeCollectionPanelsFitAndRender() async throws {
        guard let output = ProcessInfo.processInfo.environment["WECHATBRIDGE_UI_SNAPSHOTS"] else {
            throw XCTSkip("Set WECHATBRIDGE_UI_SNAPSHOTS to verify native panel rendering.")
        }
        _ = NSApplication.shared
        let root = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let prefs = preferences()
        let model = AppModel(preferences: prefs)
        model.start(inbox: Inbox(root: root), watch: false, removal: .delete)
        let targets = ForwardTargets()
        let runner = ActionRunner(model: model, authorization: AccessibilityAuthorization(), targets: targets, preferences: prefs, sceneCoordinator: SceneCoordinator(preferences: prefs), skills: SkillLibrary())
        let coordinator = CollectionCoordinator(model: model, targets: targets, preferences: prefs, runner: runner)
        defer { coordinator.hide() }
        let foreground = NSWorkspace.shared.frontmostApplication?.processIdentifier
        for count in [100, 100, 86] { _ = try share(in: model.inbox!, action: .collect, messageCount: count) }
        model.reload()
        try await Task.sleep(nanoseconds: 250_000_000)
        let id = try XCTUnwrap(model.collectionLedger.current?.id)
        coordinator.show(id)
        try await Task.sleep(nanoseconds: 100_000_000)
        try snapshot("source-required", output: output)
        let firstID = try XCTUnwrap(model.collection(id)?.batchIDs.first)
        XCTAssertTrue(model.setBatchConversation(firstID, name: "产品讨论群", defaultFor: id))
        if let lastID = model.collection(id)?.batchIDs.last { XCTAssertTrue(model.setBatchConversation(lastID, name: "小陈")) }
        XCTAssertEqual(model.collectionLedger.current?.batchIDs.compactMap { model.collectionMetadata[$0]?.count }, [100, 100, 86])
        coordinator.show(id)
        try await Task.sleep(nanoseconds: 150_000_000)
        XCTAssertEqual(NSWorkspace.shared.frontmostApplication?.processIdentifier, foreground)
        try snapshot("collect", output: output)
        coordinator.showAnchors = true
        try await Task.sleep(nanoseconds: 150_000_000)
        try snapshot("anchors", output: output)
        let originalIDs = try XCTUnwrap(model.collection(id)?.batchIDs)
        XCTAssertEqual(coordinator.anchorBatchID, originalIDs.last)
        for (index, batchID) in originalIDs.enumerated() {
            coordinator.anchorBatchID = batchID
            try await Task.sleep(nanoseconds: 100_000_000)
            XCTAssertNotNil(model.batchRange(batchID))
            try snapshot("anchors-batch-\(index + 1)", output: output)
        }
        coordinator.anchorBatchID = originalIDs.first
        _ = try share(in: model.inbox!, action: .collect)
        model.reload()
        XCTAssertEqual(coordinator.anchorBatchID, originalIDs.first, "New shares must not interrupt browsing an earlier batch")
        XCTAssertTrue(model.changeCollections { _ = try $0.remove(originalIDs[0], from: id) })
        try await Task.sleep(nanoseconds: 100_000_000)
        XCTAssertEqual(coordinator.anchorBatchID, model.collection(id)?.batchIDs.last)
        for _ in 0..<3 { _ = try share(in: model.inbox!, action: .collect) }
        model.reload()
        try await Task.sleep(nanoseconds: 150_000_000)
        try snapshot("anchors-many-batches", output: output)
        let deleting = try XCTUnwrap(model.collection(id)?.batchIDs.last)
        XCTAssertTrue(model.deleteCollectionBatch(deleting, from: id))
        try await Task.sleep(nanoseconds: 100_000_000)
        try snapshot("deleted-batch", output: output)
        XCTAssertTrue(model.undoDeletedCollectionBatch())
        try await Task.sleep(nanoseconds: 100_000_000)
        coordinator.show(id, delivery: true)
        try await Task.sleep(nanoseconds: 150_000_000)
        try snapshot("delivery", output: output)
        coordinator.errorMessage = "A long failure message keeps original ZIP files safe. You can retry or choose another destination."
        try await Task.sleep(nanoseconds: 150_000_000)
        try snapshot("failure", output: output)
        let router = SettingsRouter()
        router.tab = .history
        let actions = SettingsActions(perform: { _, _, _ in }, showEntries: {}, openCollection: { id, delivery in coordinator.show(id, delivery: delivery) }, restartOnboarding: {})
        let history = SettingsView(model: model, preferences: prefs, loginItem: LoginItem(), authorization: AccessibilityAuthorization(), screenRecording: ScreenRecordingAuthorization(), router: router, forwardTargets: targets, skills: SkillLibrary(), updater: AppUpdater(), actions: actions)
        let host = NSHostingView(rootView: history)
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1100, height: 700), styleMask: [.titled], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        host.setFrameSize(window.contentLayoutRect.size)
        host.layoutSubtreeIfNeeded()
        try await Task.sleep(nanoseconds: 150_000_000)
        let image = try XCTUnwrap(host.bitmapImageRepForCachingDisplay(in: host.bounds))
        host.cacheDisplay(in: host.bounds, to: image)
        try XCTUnwrap(image.representation(using: .png, properties: [:])).write(to: URL(fileURLWithPath: output).appendingPathComponent("history.png"))
        window.close()
    }

    @MainActor
    func testConversationNamesAreRequiredAndDefaultsNeverOverwriteOtherContacts() async throws {
        let root = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let model = AppModel(preferences: preferences())
        model.start(inbox: Inbox(root: root), watch: false)
        let first = try share(in: model.inbox!, action: .collect)
        let second = try share(in: model.inbox!, action: .collect)
        model.reload()
        let group = try XCTUnwrap(model.collectionLedger.current)
        XCTAssertNil(model.freezeCollection(group.id))
        XCTAssertFalse(model.setBatchConversation(first, name: "  \n"))
        XCTAssertTrue(model.setBatchConversation(second, name: "小陈"))
        XCTAssertTrue(model.setBatchConversation(first, name: "产品讨论群", defaultFor: group.id))
        XCTAssertEqual(model.batchConversation(second), "小陈")
        let third = try share(in: model.inbox!, action: .collect)
        model.reload()
        try await Task.sleep(nanoseconds: 100_000_000)
        XCTAssertEqual(model.batchConversation(third), "产品讨论群")
        let restored = AppModel(preferences: preferences())
        restored.start(inbox: Inbox(root: root), watch: false)
        restored.reload()
        XCTAssertEqual(restored.batchConversation(first), "产品讨论群")
        XCTAssertEqual(restored.batchConversation(second), "小陈")
        XCTAssertEqual(restored.collection(group.id)?.defaultChatName, "产品讨论群")
        XCTAssertNotNil(restored.freezeCollection(group.id))
    }

    @MainActor
    func testQuickDeleteAndUndoPreserveBytesNamesAndCollectionOrder() async throws {
        let root = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let model = AppModel(preferences: preferences())
        model.start(inbox: Inbox(root: root), watch: false, removal: .delete)
        let first = try share(in: model.inbox!, action: .collect)
        let second = try share(in: model.inbox!, action: .collect)
        model.reload()
        let group = try XCTUnwrap(model.collectionLedger.current)
        XCTAssertTrue(model.setBatchConversation(first, name: "同事小李"))
        let source = try XCTUnwrap(model.batch(id: first)?.items.first?.url)
        let bytes = try Data(contentsOf: source)
        XCTAssertTrue(model.deleteCollectionBatch(first, from: group.id))
        XCTAssertFalse(FileManager.default.fileExists(atPath: source.path))
        XCTAssertEqual(model.collection(group.id)?.batchIDs, [second])
        let removed = try XCTUnwrap(model.deletedCollectionBatch)
        XCTAssertTrue(FileManager.default.fileExists(atPath: removed.trashURL.path))
        XCTAssertTrue(model.undoDeletedCollectionBatch())
        XCTAssertEqual(try Data(contentsOf: source), bytes)
        XCTAssertEqual(model.batchConversation(first), "同事小李")
        XCTAssertEqual(model.collection(group.id)?.batchIDs, [first, second])
        XCTAssertNil(model.deletedCollectionBatch)
    }

    @MainActor
    func testFailedTrashOperationRestoresMembershipAndActiveCollection() async throws {
        let root = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let model = AppModel(preferences: preferences())
        model.start(inbox: Inbox(root: root), watch: false, removal: .delete)
        let batchID = try share(in: model.inbox!, action: .collect)
        model.reload()
        let group = try XCTUnwrap(model.collectionLedger.current)
        let batch = try XCTUnwrap(model.batch(id: batchID))
        let payload = batch.items[0].url
        let manifest = batch.directory.appendingPathComponent("manifest.json")
        try Data("invalid".utf8).write(to: manifest)
        XCTAssertFalse(model.deleteCollectionBatch(batchID, from: group.id))
        XCTAssertEqual(model.collection(group.id)?.batchIDs, [batchID])
        XCTAssertEqual(model.collectionLedger.current?.id, group.id)
        XCTAssertTrue(FileManager.default.fileExists(atPath: payload.path))
        XCTAssertNil(model.deletedCollectionBatch)
    }

    @MainActor
    private func snapshot(_ name: String, output: String) throws {
        let window = try XCTUnwrap(NSApplication.shared.windows.first { String(describing: type(of: $0)).contains("CollectionPanel") && $0.isVisible })
        let view = try XCTUnwrap(window.contentView)
        XCTAssertGreaterThan(window.frame.width, 300)
        XCTAssertGreaterThan(window.frame.height, 100)
        XCTAssertLessThanOrEqual(window.frame.height, NSScreen.main!.visibleFrame.height)
        XCTAssertEqual(view.bounds.height, window.contentLayoutRect.height, accuracy: 1)
        view.layoutSubtreeIfNeeded()
        let bitmap = try XCTUnwrap(view.bitmapImageRepForCachingDisplay(in: view.bounds))
        view.cacheDisplay(in: view.bounds, to: bitmap)
        let directory = URL(fileURLWithPath: output)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try XCTUnwrap(bitmap.representation(using: .png, properties: [:])).write(to: directory.appendingPathComponent(name + ".png"))
        print("Native panel \(name): \(window.frame.size)")
    }

    @MainActor
    private func preferences() -> Preferences {
        let prefs = Preferences(defaults: UserDefaults(suiteName: "wechatbridge-tests-" + UUID().uuidString)!)
        prefs.historyRetentionDays = 0
        return prefs
    }

    private func share(in inbox: Inbox, action: ShareAction, messageCount: Int = 100) throws -> UUID {
        let staging = try BatchStaging.create(in: inbox)
        let itemID = UUID()
        let name = "聊天记录.zip"
        let file = try XCTUnwrap(Bundle.module.url(forResource: "collection-\(messageCount)", withExtension: "zip", subdirectory: "Fixtures"))
        let fixture = try Data(contentsOf: file)
        try fixture.write(to: staging.destination(for: itemID, displayName: name))
        let item = ManifestItem(id: itemID, displayName: name, relativePath: staging.relativePath(for: itemID, displayName: name), contentType: "public.zip-archive", byteCount: Int64(fixture.count), itemIndex: 0, attachmentIndex: 0, loadStrategy: .fileRepresentation)
        _ = try staging.commit(manifest: BatchManifest(batchID: staging.batchID, createdAt: Date(), items: [item], action: action), diagnostics: nil, intent: action.needsIntent ? BatchIntent(action: action, requestedAt: Date()) : nil, in: inbox)
        return staging.batchID
    }
}

private final class CollectionTestNotificationCenter: DeliveryNotificationCenter {
    weak var delegate: UNUserNotificationCenterDelegate?
    func setNotificationCategories(_ categories: Set<UNNotificationCategory>) {}
    func requestAuthorization(options: UNAuthorizationOptions) async throws -> Bool { false }
    func add(_ request: UNNotificationRequest) async throws {}
}
