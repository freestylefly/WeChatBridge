import Foundation

public enum ByteFormat {
    nonisolated(unsafe) private static let formatter: ByteCountFormatter = {
        let formatter = ByteCountFormatter()
        formatter.countStyle = .file
        formatter.allowsNonnumericFormatting = false
        return formatter
    }()
    private static let lock = NSLock()

    /// `ByteCountFormatter` is not thread-safe and history formats from both
    /// the main actor and the import task, so one instance stays behind a lock
    /// rather than being rebuilt per row.
    public static func string(_ byteCount: Int64) -> String {
        lock.lock()
        defer { lock.unlock() }
        return formatter.string(fromByteCount: max(0, byteCount))
    }
}
