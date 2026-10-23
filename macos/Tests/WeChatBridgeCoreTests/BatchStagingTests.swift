import WeChatBridgeCore
import Foundation
import XCTest

final class BatchStagingTests: XCTestCase {
    private var temporary: TemporaryInbox!

    override func setUp() {
        super.setUp()
        temporary = TemporaryInbox()
    }

    override func tearDown() {
        temporary.tearDown()
        super.tearDown()
    }

    func testCommitMovesTheWholeBatchIntoReady() throws {
        let destination = try temporary.commitBatch(names: ["聊天记录.zip"])

        XCTAssertTrue(FileManager.default.fileExists(atPath: destination.path))
        XCTAssertEqual(destination.deletingLastPathComponent(), temporary.inbox.ready)
        XCTAssertTrue(
            FileManager.default.fileExists(
                atPath: destination.appendingPathComponent("manifest.json").path
            )
        )
        // Nothing is left behind for the app to trip over.
        let staging = try FileManager.default.contentsOfDirectory(
            atPath: temporary.inbox.staging.path
        )
        XCTAssertEqual(staging, [])
    }

    func testTwoAttachmentsWithTheSameNameDoNotOverwriteEachOther() throws {
        try temporary.commitBatch(names: ["聊天记录.zip", "聊天记录.zip"])

        let items = temporary.reader.loadBatches().flatMap(\.items)
        XCTAssertEqual(items.count, 2)
        XCTAssertEqual(Set(items.map(\.displayName)), ["聊天记录.zip"])
        // Same visible name, different UUID directories, both present on disk.
        XCTAssertEqual(Set(items.map(\.url)).count, 2)
        for item in items {
            XCTAssertTrue(FileManager.default.fileExists(atPath: item.url.path))
        }
    }

    func testDiscardedStagingIsNeverVisibleInReady() throws {
        let staging = try BatchStaging.create(in: temporary.inbox)
        let itemID = UUID()
        let source = try temporary.makeSourceFile(named: "half.zip")
        let destination = try staging.destination(for: itemID, displayName: "half.zip")
        try FileManager.default.copyItem(at: source, to: destination)

        staging.discard()

        XCTAssertEqual(temporary.reader.loadBatches(), [])
        XCTAssertFalse(FileManager.default.fileExists(atPath: staging.directory.path))
    }

    func testAnUncommittedBatchIsInvisibleEvenWithItsFilesInPlace() throws {
        let staging = try BatchStaging.create(in: temporary.inbox)
        let source = try temporary.makeSourceFile(named: "half.zip")
        let destination = try staging.destination(for: UUID(), displayName: "half.zip")
        try FileManager.default.copyItem(at: source, to: destination)

        // This is the state an extension killed mid-copy leaves behind.
        XCTAssertEqual(temporary.reader.loadBatches(), [])
    }

    func testCommitWritesDiagnosticsAlongsideTheManifest() throws {
        let staging = try BatchStaging.create(in: temporary.inbox)
        let diagnostics = ImportDiagnostics(
            batchID: staging.batchID,
            createdAt: Date(),
            extensionVersion: "0.1.0 (1)",
            action: .codex,
            inputItemCount: 1,
            succeeded: true,
            attachments: []
        )
        let destination = try staging.commit(
            manifest: BatchManifest(batchID: staging.batchID, createdAt: Date(), items: []),
            diagnostics: diagnostics,
            intent: nil,
            in: temporary.inbox
        )

        let url = destination.appendingPathComponent(ImportDiagnostics.fileName)
        let decoded = try BatchManifest.decoder().decode(
            ImportDiagnostics.self,
            from: Data(contentsOf: url)
        )
        XCTAssertEqual(decoded.batchID, staging.batchID)
        XCTAssertEqual(decoded.extensionVersion, "0.1.0 (1)")
    }

    func testManifestSurvivesACodingRoundTrip() throws {
        let manifest = BatchManifest(
            batchID: UUID(),
            createdAt: Date(timeIntervalSince1970: 1_772_000_000),
            items: [
                ManifestItem(
                    id: UUID(),
                    displayName: "聊天记录.zip",
                    relativePath: "items/x/聊天记录.zip",
                    contentType: "public.zip-archive",
                    byteCount: 4096,
                    itemIndex: 0,
                    attachmentIndex: 1,
                    loadStrategy: .fileURL
                ),
            ]
        )
        let data = try BatchManifest.encoder().encode(manifest)
        XCTAssertEqual(try BatchManifest.decoder().decode(BatchManifest.self, from: data), manifest)
    }
}
