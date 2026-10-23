import Foundation

/// The shared inbox on disk.
///
/// ```text
/// <group container>/Inbox/
///   Staging/<batch-id>.partial/   ← only the extension writes here
///     items/<item-id>/<display name>
///     manifest.json.partial
///   Ready/<batch-id>/             ← only the app writes here (deletes)
///     items/<item-id>/<display name>
///     manifest.json
///     diagnostics.json
///   Failures/<uuid>.json          ← the extension writes, the app reads once
/// ```
///
/// A batch becomes visible by a single `rename(2)` from `Staging` to `Ready`,
/// so a share that is interrupted at any point leaves debris in `Staging` and
/// nothing half-formed in front of the user.
public struct Inbox: Sendable, Hashable {
    public let root: URL

    public init(root: URL) {
        self.root = root
    }

    public static func resolve(
        appGroupIdentifier: String = AppGroup.identifier,
        storageMode: AppGroup.StorageMode = .current,
        sharedRoot: URL = AppGroup.StorageMode.sharedRoot,
        fileManager: FileManager = .default
    ) throws -> Inbox {
        if storageMode == .sharedDirectory {
            return Inbox(root: sharedRoot.appendingPathComponent("Inbox", isDirectory: true))
        }
        guard let container = fileManager.containerURL(
            forSecurityApplicationGroupIdentifier: appGroupIdentifier
        ) else {
            throw InboxError.containerUnavailable(identifier: appGroupIdentifier)
        }
        return Inbox(root: container.appendingPathComponent("Inbox", isDirectory: true))
    }

    public var staging: URL { root.appendingPathComponent("Staging", isDirectory: true) }
    public var ready: URL { root.appendingPathComponent("Ready", isDirectory: true) }
    /// Redacted import records, including for shares that were rejected and so
    /// never produced a batch. This is where a P0 field report comes from.
    public var diagnostics: URL { root.appendingPathComponent("Diagnostics", isDirectory: true) }
    /// One file per share the extension could not complete. The extension has
    /// no interface to report into, so it leaves the message here and the app
    /// reads it — and deletes it — on its next scan.
    public var failures: URL {
        root.appendingPathComponent(ShareFailure.directoryName, isDirectory: true)
    }

    public func prepareDirectories(fileManager: FileManager = .default) throws {
        for directory in [staging, ready, diagnostics, failures] {
            try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        }
    }

    /// Removes staging directories that no live extension can still be filling.
    ///
    /// Age is measured from the newest modification date anywhere in the tree,
    /// not from the directory itself: a 1 GB copy touches only the file being
    /// written, and pruning by the parent's timestamp would delete a share that
    /// is still in progress.
    @discardableResult
    public func pruneStaging(
        olderThan interval: TimeInterval = 30 * 60,
        now: Date = Date(),
        fileManager: FileManager = .default
    ) -> Int {
        let contents = (try? fileManager.contentsOfDirectory(
            at: staging,
            includingPropertiesForKeys: [.contentModificationDateKey, .isDirectoryKey],
            options: [.skipsHiddenFiles]
        )) ?? []

        var removed = 0
        for candidate in contents {
            let newest = Self.newestModificationDate(at: candidate, fileManager: fileManager) ?? .distantPast
            guard now.timeIntervalSince(newest) > interval else { continue }
            if (try? fileManager.removeItem(at: candidate)) != nil { removed += 1 }
        }
        return removed
    }

    static func newestModificationDate(at url: URL, fileManager: FileManager) -> Date? {
        let keys: [URLResourceKey] = [.contentModificationDateKey]
        var newest = (try? url.resourceValues(forKeys: Set(keys)))?.contentModificationDate
        guard let enumerator = fileManager.enumerator(
            at: url,
            includingPropertiesForKeys: keys,
            options: [.skipsHiddenFiles]
        ) else { return newest }

        for case let child as URL in enumerator {
            guard let date = (try? child.resourceValues(forKeys: Set(keys)))?.contentModificationDate else { continue }
            if newest == nil || date > newest! { newest = date }
        }
        return newest
    }
}
