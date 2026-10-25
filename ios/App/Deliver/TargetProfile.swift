import Foundation

enum DeliveryFormat: String, Codable, CaseIterable {
    /// The original WeChat ZIP, untouched.
    case archive
    /// `聊天记录.txt` plus the images unpacked from the archive.
    case transcriptAndImages
    /// A Markdown note and the archive, written into the Obsidian vault.
    case knowledgeNote
    /// The same note shape, written into a folder the user picked.
    case folder

    var label: String {
        switch self {
        case .archive: "发送 ZIP"
        case .transcriptAndImages: "发送聊天记录和图片"
        case .knowledgeNote: "写入 Obsidian"
        case .folder: "写入文件夹"
        }
    }

    /// Whether this format ends in the system share sheet.
    var usesShareSheet: Bool { self == .archive || self == .transcriptAndImages }
}

/// What one destination can receive. iOS cannot hand content to a named app,
/// so choosing a target decides the *shape* of what is shared; the user still
/// taps the app in the share sheet, and `hint` tells them which.
struct TargetProfile: Identifiable, Hashable, Codable {
    let id: String
    let displayName: String
    var format: DeliveryFormat
    /// nil means no known limit. Filled from device tests (R2).
    let maxAttachments: Int?
    let symbol: String

    var hint: String {
        switch format {
        case .archive, .transcriptAndImages:
            id == "other" ? "请在下一屏选择要发送到的 App" : "请在下一屏选择「\(displayName)」"
        case .knowledgeNote, .folder:
            ""
        }
    }

    /// Asset name of the app's own icon (from `shared/AppLogos`); nil for
    /// targets that have none and fall back to `symbol`.
    var logo: String? {
        ["chatgpt", "doubao", "qwen", "workbuddy", "claude", "obsidian"].contains(id) ? "Logo-\(id)" : nil
    }

    static let pasteHint = "提示词已复制，在输入框长按粘贴"

    /// Seen on device (R2, 2026-10-24): 豆包 drops the text in a share and
    /// 千问 answers a text-plus-file share with 「格式不支持」, so the prompt
    /// reaches these apps only through the clipboard.
    var dropsSharedText: Bool { id == "doubao" || id == "qwen" }

    /// What to show before the share sheet; the paste line only when there is
    /// a prompt the target will not receive.
    func sendHint(hasPrompt: Bool) -> String {
        guard hasPrompt, dropsSharedText, !hint.isEmpty else { return hint }
        return "\(hint)\n\(Self.pasteHint)"
    }

    /// Order is the default grid order. Formats follow the device tests of
    /// 2026-10-24: ChatGPT and WorkBuddy took the ZIP, 豆包 and 千问 refused it
    /// but took text and images. Claude and DeepSeek are untested and start on
    /// the route every app tested so far accepts.
    static let builtIn: [TargetProfile] = [
        TargetProfile(id: "chatgpt", displayName: "ChatGPT", format: .archive, maxAttachments: nil, symbol: "bubble.left.and.text.bubble.right"),
        TargetProfile(id: "doubao", displayName: "豆包", format: .transcriptAndImages, maxAttachments: nil, symbol: "sparkles"),
        TargetProfile(id: "qwen", displayName: "千问", format: .transcriptAndImages, maxAttachments: nil, symbol: "wand.and.stars"),
        TargetProfile(id: "workbuddy", displayName: "WorkBuddy", format: .archive, maxAttachments: nil, symbol: "person.2"),
        TargetProfile(id: "claude", displayName: "Claude", format: .transcriptAndImages, maxAttachments: nil, symbol: "asterisk"),
        TargetProfile(id: "deepseek", displayName: "DeepSeek", format: .transcriptAndImages, maxAttachments: nil, symbol: "magnifyingglass"),
        TargetProfile(id: "obsidian", displayName: "Obsidian", format: .knowledgeNote, maxAttachments: nil, symbol: "diamond"),
        TargetProfile(id: "folder", displayName: "文件夹", format: .folder, maxAttachments: nil, symbol: "folder"),
        TargetProfile(id: "other", displayName: "其他 App", format: .archive, maxAttachments: nil, symbol: "square.and.arrow.up"),
    ]

    /// Only share-sheet targets can switch format; the two writers are fixed.
    var canChangeFormat: Bool { format.usesShareSheet }

    /// The user's view of the list: stored order first (unknown ids keep their
    /// built-in place after it), hidden ones removed, format overrides applied,
    /// and the last-used target lifted to the front.
    static func arranged(
        _ profiles: [TargetProfile],
        order: [String],
        hidden: Set<String>,
        overrides: [String: String],
        lastUsed: String?
    ) -> [TargetProfile] {
        var ordered = order.compactMap { id in profiles.first { $0.id == id } }
        ordered += profiles.filter { !order.contains($0.id) }
        ordered = ordered.filter { !hidden.contains($0.id) }.map { profile in
            var profile = profile
            if profile.canChangeFormat,
               let raw = overrides[profile.id],
               let format = DeliveryFormat(rawValue: raw),
               format.usesShareSheet {
                profile.format = format
            }
            return profile
        }
        if let lastUsed, let index = ordered.firstIndex(where: { $0.id == lastUsed }) {
            ordered.insert(ordered.remove(at: index), at: 0)
        }
        return ordered
    }
}
