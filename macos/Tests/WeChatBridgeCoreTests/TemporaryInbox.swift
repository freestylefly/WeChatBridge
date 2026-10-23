import WeChatBridgeCore
import Foundation

/// A throwaway inbox on disk.
///
/// The inbox protocol is about real filesystem behaviour — atomic renames,
/// same-name collisions, debris after a crash — so the tests exercise a real
/// directory rather than a mocked file manager. Removal is set to `.delete` so a
/// test run never puts fixtures in the user's Trash.
struct TemporaryInbox {
    let root: URL
    let inbox: Inbox

    init(function: String = #function) {
        root = URL(fileURLWithPath: NSTemporaryDirectory())
            .appendingPathComponent("wechatbridge-tests-\(UUID().uuidString)", isDirectory: true)
        inbox = Inbox(root: root.appendingPathComponent("Inbox", isDirectory: true))
        try? inbox.prepareDirectories()
    }

    var reader: InboxReader { InboxReader(inbox: inbox, removal: .delete) }

    func tearDown() {
        try? FileManager.default.removeItem(at: root)
    }

    /// Writes `contents` to a temporary source file, the way an item provider
    /// would hand one over.
    func makeSourceFile(named name: String, bytes: Int = 16) throws -> URL {
        let directory = root.appendingPathComponent("sources/\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let url = directory.appendingPathComponent(name)
        try Data(repeating: 0x41, count: bytes).write(to: url)
        return url
    }

    /// Commits one batch with the given display names, all with identical
    /// contents, and returns its `Ready/<batch-id>` directory.
    @discardableResult
    func commitBatch(
        names: [String],
        createdAt: Date = Date(),
        action: ShareAction = .clipboard,
        intent: BatchIntent? = nil
    ) throws -> URL {
        let staging = try BatchStaging.create(in: inbox)
        var items: [ManifestItem] = []
        for (index, name) in names.enumerated() {
            let itemID = UUID()
            let source = try makeSourceFile(named: name)
            let destination = try staging.destination(for: itemID, displayName: name)
            try FileManager.default.copyItem(at: source, to: destination)
            items.append(
                ManifestItem(
                    id: itemID,
                    displayName: name,
                    relativePath: staging.relativePath(for: itemID, displayName: name),
                    contentType: "public.zip-archive",
                    byteCount: 16,
                    itemIndex: 0,
                    attachmentIndex: index,
                    loadStrategy: .fileRepresentation
                )
            )
        }
        return try staging.commit(
            manifest: BatchManifest(
                batchID: staging.batchID,
                createdAt: createdAt,
                items: items,
                action: action
            ),
            diagnostics: nil,
            intent: intent,
            in: inbox
        )
    }

    /// The batch ID of a committed batch, which is its directory name.
    func batchID(of directory: URL) -> UUID {
        UUID(uuidString: directory.lastPathComponent)!
    }
}
