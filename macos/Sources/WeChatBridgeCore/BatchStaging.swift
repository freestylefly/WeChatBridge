import Foundation

/// The extension's half of the inbox protocol.
///
/// Deliberately a value type holding only URLs: `NSItemProvider` delivers its
/// payload on its own queues, and the copy has to happen inside that callback
/// before the temporary file is deleted. Handing a `Sendable` destination across
/// is safe; handing a mutable writer object across would not be.
public struct BatchStaging: Sendable, Hashable {
    public let batchID: UUID
    /// `Staging/<batch-id>.partial`
    public let directory: URL

    public static let partialSuffix = "partial"
    public static let manifestFileName = "manifest.json"
    public static let itemsDirectoryName = "items"

    public var itemsDirectory: URL {
        directory.appendingPathComponent(Self.itemsDirectoryName, isDirectory: true)
    }

    public static func create(
        in inbox: Inbox,
        batchID: UUID = UUID(),
        fileManager: FileManager = .default
    ) throws -> BatchStaging {
        try inbox.prepareDirectories(fileManager: fileManager)
        let directory = inbox.staging
            .appendingPathComponent("\(batchID.uuidString).\(partialSuffix)", isDirectory: true)
        try fileManager.createDirectory(
            at: directory.appendingPathComponent(itemsDirectoryName, isDirectory: true),
            withIntermediateDirectories: true
        )
        return BatchStaging(batchID: batchID, directory: directory)
    }

    /// Creates `items/<item-id>/` and returns the file URL to copy onto.
    ///
    /// One directory per item is what makes two attachments called
    /// `聊天记录.zip` coexist without either being renamed behind the user's
    /// back.
    public func destination(
        for itemID: UUID,
        displayName: String,
        fileManager: FileManager = .default
    ) throws -> URL {
        let itemDirectory = itemsDirectory.appendingPathComponent(itemID.uuidString, isDirectory: true)
        try fileManager.createDirectory(at: itemDirectory, withIntermediateDirectories: true)
        return itemDirectory.appendingPathComponent(displayName, isDirectory: false)
    }

    public func relativePath(for itemID: UUID, displayName: String) -> String {
        "\(Self.itemsDirectoryName)/\(itemID.uuidString)/\(displayName)"
    }

    /// Publishes the batch. Returns the `Ready/<batch-id>` directory.
    ///
    /// The manifest is completed inside the staging directory first, so the
    /// tree that gets moved is already self-consistent. `moveItem` between two
    /// directories of the same group container is a plain rename, which is the
    /// atomic step the app relies on.
    @discardableResult
    public func commit(
        manifest: BatchManifest,
        diagnostics: ImportDiagnostics?,
        intent: BatchIntent?,
        in inbox: Inbox,
        fileManager: FileManager = .default
    ) throws -> URL {
        // `.atomic` is the rename: Foundation writes a sibling temporary file
        // and renames it into place, so a manifest is never observed truncated.
        try BatchManifest.encoder().encode(manifest).write(
            to: directory.appendingPathComponent(Self.manifestFileName),
            options: .atomic
        )

        // Written before the rename, so the app can never observe a published
        // batch whose requested action has not arrived yet.
        if let intent, let payload = try? BatchManifest.encoder().encode(intent) {
            try payload.write(
                to: directory.appendingPathComponent(BatchIntent.fileName),
                options: .atomic
            )
        }

        if let payload = diagnostics?.encoded() {
            // Diagnostics are best-effort: losing them must never cost the user
            // the files they just shared.
            try? payload.write(
                to: directory.appendingPathComponent(ImportDiagnostics.fileName),
                options: .atomic
            )
        }

        try fileManager.createDirectory(at: inbox.ready, withIntermediateDirectories: true)
        let destination = inbox.ready.appendingPathComponent(batchID.uuidString, isDirectory: true)
        try fileManager.moveItem(at: directory, to: destination)
        return destination
    }

    /// Best-effort cleanup for a share the user cancelled or that failed
    /// part-way. `pruneStaging` is the backstop if the process dies first.
    public func discard(fileManager: FileManager = .default) {
        try? fileManager.removeItem(at: directory)
    }
}
