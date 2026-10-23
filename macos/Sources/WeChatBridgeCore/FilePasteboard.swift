import AppKit
import Foundation

/// Puts staged files on the general pasteboard as real file URLs.
///
/// Both halves of WeChatBridge write it, for different reasons: the extension writes it
/// the moment the copy lands, so ⌘V works even if nothing else does, and the app
/// rewrites it immediately before an automated paste so what gets pasted is
/// exactly what the batch contains.
///
/// The targets WeChatBridge pastes into are not sandboxed, so a plain file URL pointing
/// into the group container is readable by them as-is.
public enum FilePasteboard {
    @discardableResult
    public static func write(_ urls: [URL], to pasteboard: NSPasteboard = .general) -> Bool {
        guard !urls.isEmpty else { return false }
        pasteboard.clearContents()
        return pasteboard.writeObjects(urls.map { $0 as NSURL })
    }

    /// The paths as one line of plain text, and nothing else on the
    /// pasteboard: a target that asked for this (`ForwardTarget.pastesPathOnly`)
    /// is one that would take the file over the text if both were offered, and
    /// then do nothing with it.
    @discardableResult
    public static func writeShellPaths(_ urls: [URL], to pasteboard: NSPasteboard = .general) -> Bool {
        guard !urls.isEmpty else { return false }
        return writeText(shellLine(for: urls), to: pasteboard)
    }

    /// Plain text and nothing else: a prompt, or a shell line with a prompt
    /// folded into it.
    @discardableResult
    public static func writeText(_ text: String, to pasteboard: NSPasteboard = .general) -> Bool {
        guard !text.isEmpty else { return false }
        pasteboard.clearContents()
        return pasteboard.setString(text, forType: .string)
    }

    /// One payload of a `PastePlan`.
    @discardableResult
    public static func write(_ payload: PastePayload, to pasteboard: NSPasteboard = .general) -> Bool {
        switch payload {
        case .files(let urls): return write(urls, to: pasteboard)
        case .text(let text): return writeText(text, to: pasteboard)
        }
    }

    /// What dropping the same files on a terminal window would have typed.
    ///
    /// Single quotes rather than backslashes: they are the one quoting every
    /// shell reads the same way, and they leave a Chinese filename with spaces
    /// legible where `聊天记录\ 2026.zip` is not. Each path ends in a space so
    /// the user can carry on typing the command.
    public static func shellLine(for urls: [URL]) -> String {
        urls.map { shellQuoted($0.path) + " " }.joined()
    }

    /// POSIX single-quoting: the only character that needs anything is the
    /// quote itself, which is closed, escaped and reopened.
    public static func shellQuoted(_ path: String) -> String {
        "'" + path.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }
}
