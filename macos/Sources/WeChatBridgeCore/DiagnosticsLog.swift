import Foundation

/// A small ring of redacted import records.
///
/// A successful batch keeps its own `diagnostics.json`, but a rejected share
/// leaves no batch at all — and a rejection is exactly the case where the
/// registered type identifiers need to be recoverable afterwards. Capped so an
/// app that is never opened cannot accumulate records without bound.
public enum DiagnosticsLog {
    public static let retainedRecords = 20

    public static func record(
        _ diagnostics: ImportDiagnostics,
        in inbox: Inbox,
        fileManager: FileManager = .default
    ) {
        guard let payload = diagnostics.encoded() else { return }
        try? fileManager.createDirectory(at: inbox.diagnostics, withIntermediateDirectories: true)
        let name = "\(diagnostics.succeeded ? "ok" : "failed")-\(diagnostics.batchID.uuidString).json"
        try? payload.write(to: inbox.diagnostics.appendingPathComponent(name), options: .atomic)
        prune(in: inbox, fileManager: fileManager)
    }

    static func prune(in inbox: Inbox, fileManager: FileManager = .default) {
        let keys: [URLResourceKey] = [.contentModificationDateKey]
        let files = (try? fileManager.contentsOfDirectory(
            at: inbox.diagnostics,
            includingPropertiesForKeys: keys,
            options: [.skipsHiddenFiles]
        )) ?? []
        guard files.count > retainedRecords else { return }

        let sorted = files.sorted { lhs, rhs in
            let left = (try? lhs.resourceValues(forKeys: Set(keys)))?.contentModificationDate ?? .distantPast
            let right = (try? rhs.resourceValues(forKeys: Set(keys)))?.contentModificationDate ?? .distantPast
            return left > right
        }
        for stale in sorted.dropFirst(retainedRecords) {
            try? fileManager.removeItem(at: stale)
        }
    }
}
