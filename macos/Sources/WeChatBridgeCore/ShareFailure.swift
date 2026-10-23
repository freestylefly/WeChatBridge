import Foundation

/// A share the extension could not complete, left on disk for the app to say
/// out loud.
///
/// The extension has no interface any more (ADR-0005, 2026-09-05 evening): it
/// runs invisibly inside somebody else's share sheet and is killed seconds
/// later, so there is nowhere for it to report a rejected attachment or a
/// container it could not write to. It writes one of these instead and starts
/// the app, which owns every other message the user ever sees from WeChatBridge.
///
/// Deliberately not a batch: nothing was committed, so there is no history row
/// to attach this to. It is a one-shot message, consumed by the read.
public struct ShareFailure: Codable, Sendable, Hashable {
    public static let directoryName = "Failures"

    public let at: Date
    /// Which Share-menu entry the user picked. Not shown anywhere yet — the
    /// message already names what went wrong — but a field report is worth
    /// little without knowing which of the nine entries produced it.
    public let action: ShareAction
    public let message: String

    public init(at: Date, action: ShareAction, message: String) {
        self.at = at
        self.action = action
        self.message = message
    }

    /// Best effort, exactly like `DiagnosticsLog`: a share that has already
    /// failed must not fail a second time over its own error report.
    public static func record(
        _ failure: ShareFailure,
        in inbox: Inbox,
        fileManager: FileManager = .default
    ) {
        guard let payload = try? BatchManifest.encoder().encode(failure) else { return }
        let directory = inbox.failures
        try? fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        // A UUID rather than a timestamp: two extensions can fail in the same
        // second, and one overwriting the other's report would hide a failure
        // the user is waiting to hear about.
        try? payload.write(
            to: directory.appendingPathComponent("\(UUID().uuidString).json"),
            options: .atomic
        )
    }
}
