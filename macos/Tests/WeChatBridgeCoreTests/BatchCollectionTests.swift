import Foundation
import XCTest
@testable import WeChatBridgeCore

final class BatchCollectionTests: XCTestCase {
    func testOlderLedgerWithoutDefaultConversationStillLoads() throws {
        let temp = TemporaryInbox()
        defer { temp.tearDown() }
        var ledger = BatchCollectionLedger()
        let id = ledger.append(UUID())
        try ledger.setDefaultChatName(id, name: "研发群")
        let url = temp.inbox.root.appendingPathComponent("collections.json")
        try ledger.save(to: url)
        var json = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
        var groups = try XCTUnwrap(json["collections"] as? [[String: Any]])
        groups[0].removeValue(forKey: "defaultChatName")
        json["collections"] = groups
        try JSONSerialization.data(withJSONObject: json).write(to: url)
        let restored = try BatchCollectionLedger.load(from: url)
        XCTAssertEqual(restored.current?.id, id)
        XCTAssertNil(restored.current?.defaultChatName)
    }

    func testDeliveryFreezesMembersAndLaterShareStartsNewCollection() throws {
        var ledger = BatchCollectionLedger()
        let first = UUID(), second = UUID(), third = UUID()
        let group = ledger.append(first)
        XCTAssertEqual(ledger.append(second), group)
        XCTAssertEqual(try ledger.beginDelivery(group), [first, second])
        XCTAssertThrowsError(try ledger.remove(first, from: group))
        let next = ledger.append(third)
        XCTAssertNotEqual(group, next)
        XCTAssertEqual(ledger.current?.batchIDs, [third])
        try ledger.finishDelivery(group, succeeded: false, target: "Codex", detail: "failure")
        XCTAssertEqual(ledger.collections.first?.status, .retry)
        XCTAssertEqual(ledger.protectedBatchIDs, [first, second, third])
    }

    func testRestartRecoversInterruptedDeliveryWithoutReplayingOrRecollecting() throws {
        let temp = TemporaryInbox()
        defer { temp.tearDown() }
        let file = temp.inbox.root.appendingPathComponent("collections.json")
        var ledger = BatchCollectionLedger()
        let first = UUID(), removed = UUID()
        let id = ledger.append(first)
        ledger.append(removed)
        let position = try ledger.remove(removed, from: id)
        try ledger.save(to: file)
        ledger = try .load(from: file)
        ledger.append(removed)
        XCTAssertEqual(ledger.current?.batchIDs, [first])
        try ledger.restore(removed, to: id, at: position)
        _ = try ledger.beginDelivery(id)
        try ledger.save(to: file)
        ledger = try .load(from: file)
        ledger.recoverInterruptedDeliveries()
        XCTAssertEqual(ledger.collections.first?.status, .retry)
        XCTAssertNil(ledger.current)
        XCTAssertEqual(ledger.collections.first?.batchIDs, [first, removed])
    }

    func testResumingDraftParksTheOtherActiveGroup() throws {
        var ledger = BatchCollectionLedger()
        let first = ledger.append(UUID())
        ledger.parkCurrent()
        let second = ledger.append(UUID())
        try ledger.resume(first)
        XCTAssertEqual(ledger.current?.id, first)
        XCTAssertEqual(ledger.collections.first { $0.id == second }?.status, .draft)
    }

    func testRetentionDoesNotDeleteUnfinishedOrUnclaimedCollections() throws {
        let temp = TemporaryInbox()
        defer { temp.tearDown() }
        let old = Date(timeIntervalSinceNow: -10_000)
        let unclaimed = try temp.commitBatch(names: ["video.zip"], createdAt: old, action: .collect)
        let protected = try temp.commitBatch(names: ["protected.zip"], createdAt: old, action: .collect)
        let finished = try temp.commitBatch(names: ["finished.zip"], createdAt: old, action: .collect)
        for directory in [protected, finished] {
            try temp.reader.recordOutcome(.init(kind: .delivered, at: old), for: temp.batchID(of: directory))
        }
        XCTAssertEqual(temp.reader.pruneHistory(olderThan: 60, excluding: [temp.batchID(of: protected)]), 1)
        XCTAssertTrue(FileManager.default.fileExists(atPath: unclaimed.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: protected.path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: finished.path))
    }

    func testCorruptLedgerFailsClosedAndCollectionHasNoPasteIntent() throws {
        let temp = TemporaryInbox()
        defer { temp.tearDown() }
        let url = temp.inbox.root.appendingPathComponent("collections.json")
        try Data("{invalid".utf8).write(to: url)
        XCTAssertThrowsError(try BatchCollectionLedger.load(from: url))
        XCTAssertFalse(ShareAction.collect.needsIntent)
        let directory = try temp.commitBatch(names: ["original.zip"], action: .collect)
        let batch = try XCTUnwrap(temp.reader.batch(at: directory))
        XCTAssertNil(batch.outcome)
        XCTAssertNil(ShareAction.collect.targetBundleIdentifier)
    }
}
