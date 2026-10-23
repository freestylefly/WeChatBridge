import WeChatBridgeCore
import Foundation
import XCTest

final class InboxMaintenanceTests: XCTestCase {
    private var temporary: TemporaryInbox!

    override func setUp() {
        super.setUp()
        temporary = TemporaryInbox()
    }

    override func tearDown() {
        temporary.tearDown()
        super.tearDown()
    }

    func testPruningRemovesOnlyStagingOlderThanTheWindow() throws {
        let stale = try BatchStaging.create(in: temporary.inbox)
        let fresh = try BatchStaging.create(in: temporary.inbox)
        let old = Date(timeIntervalSinceNow: -3600)
        try setModificationDate(old, forTreeAt: stale.directory)

        let removed = temporary.inbox.pruneStaging(olderThan: 30 * 60)

        XCTAssertEqual(removed, 1)
        XCTAssertFalse(FileManager.default.fileExists(atPath: stale.directory.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: fresh.directory.path))
    }

    func testSharedStorageUsesTheProvidedRoot() throws {
        let inbox = try Inbox.resolve(storageMode: .sharedDirectory, sharedRoot: temporary.root)

        XCTAssertEqual(inbox.root, temporary.root.appendingPathComponent("Inbox", isDirectory: true))
        try inbox.prepareDirectories()
        XCTAssertTrue(FileManager.default.fileExists(atPath: inbox.ready.path))
    }

    func testAnOldDirectoryStillBeingWrittenIntoIsNotPruned() throws {
        // The failure this guards against: a 1 GB copy only touches the file
        // being written, so judging the batch by its parent directory's
        // timestamp would delete a share that is still arriving.
        let staging = try BatchStaging.create(in: temporary.inbox)
        try setModificationDate(Date(timeIntervalSinceNow: -3600), forTreeAt: staging.directory)
        let source = try temporary.makeSourceFile(named: "big.zip")
        let destination = try staging.destination(for: UUID(), displayName: "big.zip")
        try FileManager.default.copyItem(at: source, to: destination)

        XCTAssertEqual(temporary.inbox.pruneStaging(olderThan: 30 * 60), 0)
        XCTAssertTrue(FileManager.default.fileExists(atPath: staging.directory.path))
    }

    func testPruningLeavesReadyBatchesAlone() throws {
        let destination = try temporary.commitBatch(names: ["kept.zip"])
        try setModificationDate(Date(timeIntervalSinceNow: -86400), forTreeAt: destination)

        temporary.inbox.pruneStaging(olderThan: 60)

        XCTAssertEqual(temporary.reader.loadBatches().count, 1)
    }

    func testDiagnosticsLogKeepsOnlyTheMostRecentRecords() throws {
        for index in 0..<(DiagnosticsLog.retainedRecords + 5) {
            let record = ImportDiagnostics(
                batchID: UUID(),
                createdAt: Date(),
                extensionVersion: "test",
                action: .clipboard,
                inputItemCount: 1,
                succeeded: index.isMultiple(of: 2),
                attachments: []
            )
            DiagnosticsLog.record(record, in: temporary.inbox)
        }

        let files = try FileManager.default.contentsOfDirectory(
            atPath: temporary.inbox.diagnostics.path
        )
        XCTAssertEqual(files.count, DiagnosticsLog.retainedRecords)
    }

    private func setModificationDate(_ date: Date, forTreeAt url: URL) throws {
        let manager = FileManager.default
        var urls = [url]
        if let enumerator = manager.enumerator(at: url, includingPropertiesForKeys: nil) {
            for case let child as URL in enumerator { urls.append(child) }
        }
        // Deepest first: touching a directory after its children would reset it.
        for target in urls.sorted(by: { $0.pathComponents.count > $1.pathComponents.count }) {
            try manager.setAttributes([.modificationDate: date], ofItemAtPath: target.path)
        }
    }
}
