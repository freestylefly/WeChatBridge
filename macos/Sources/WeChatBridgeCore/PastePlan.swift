import Foundation

/// One thing the pasteboard holds for one ⌘V.
public enum PastePayload: Hashable, Sendable {
    case files([URL])
    case text(String)
}

/// What a forward pastes, in the order it pastes it.
///
/// A prompt and a file cannot share one ⌘V: a pasteboard that offers both
/// text and file URLs makes the target pick, and a chat app picks the files.
/// So a prompt before the files is two pastes. A terminal takes only text, so
/// there the prompt and the quoted paths become one line.
///
/// The prompt always goes first: an instruction read after its attachments is
/// an instruction the agent has already started guessing at.
public enum PastePlan {
    public static func make(urls: [URL], pathOnly: Bool, prompt: String?) -> [PastePayload] {
        let prompt = prompt?.trimmingCharacters(in: .whitespacesAndNewlines)
        guard pathOnly else {
            guard let prompt, !prompt.isEmpty else { return [.files(urls)] }
            return [.text(prompt), .files(urls)]
        }
        let paths = FilePasteboard.shellLine(for: urls)
        guard let prompt, !prompt.isEmpty else { return [.text(paths)] }
        // A newline pasted into a shell is Return: it would run whatever was
        // typed so far, with the paths still to come. One line, always.
        let flat = prompt.components(separatedBy: .newlines)
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
            .joined(separator: " ")
        return [.text("\(flat) \(paths)")]
    }

    /// What a manual ⌘V should produce when the automated paste is refused:
    /// the files, or the one line a terminal would have received.
    public static func manualPayload(_ plan: [PastePayload]) -> PastePayload? {
        plan.first { if case .files = $0 { return true } else { return false } } ?? plan.first
    }
}
