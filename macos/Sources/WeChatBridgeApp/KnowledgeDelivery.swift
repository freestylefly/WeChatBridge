import WeChatBridgeCore
import Foundation

/// Writes a WeChat archive into a folder as Markdown plus a durable copy of
/// the original ZIP. Keeping the archive makes a parsing change or a future
/// converter able to rebuild the note without asking WeChat again.
///
/// Two destinations share this assembly: 「沉淀到 Obsidian」 lands the note in
/// a subfolder of the user's vault, and 「沉淀到文件夹」 lands it in a subfolder
/// of the folder the user picked — the same shape either way. The note shape —
/// `聊天名.md` plus `附件/` — is the same too.
enum KnowledgeDelivery {
    enum Failure: LocalizedError {
        case notConfigured
        case unreadableArchive

        var errorDescription: String? {
            switch self {
            case .notConfigured: return L10n.text("还没有选择 Obsidian 知识库文件夹。")
            case .unreadableArchive: return L10n.text("微信导出的文件无法读取，原始文件已保留。")
            }
        }
    }

    /// The Obsidian entry: the vault is what the user configured, and the note
    /// goes into a `微信流` (or named) subfolder of it.
    @discardableResult
    static func deliver(
        urls: [URL],
        vaultPath: String,
        subfolder: String,
        chatName: String?,
        sceneName: String?
    ) throws -> [URL] {
        let vault = URL(fileURLWithPath: vaultPath, isDirectory: true)
        try FolderDelivery.validateFolder(vault)

        let subfolderPath = DisplayName.subfolderPath(subfolder)
        let folderName = subfolderPath.isEmpty ? "微信流" : subfolderPath
        return try write(
            urls: urls,
            root: vault.appendingPathComponent(folderName, isDirectory: true),
            chatName: chatName,
            sceneName: sceneName
        )
    }

    /// The folder entry: the folder is what the user configured, and the note
    /// goes into a `微信流` (or named) subfolder of it — the same landing
    /// shape the Obsidian entry writes inside its vault.
    @discardableResult
    static func deliver(
        urls: [URL],
        folderPath: String,
        subfolder: String,
        chatName: String?,
        sceneName: String?
    ) throws -> [URL] {
        let folder = URL(fileURLWithPath: folderPath, isDirectory: true)
        try FolderDelivery.validateFolder(folder)

        let subfolderPath = DisplayName.subfolderPath(subfolder)
        let folderName = subfolderPath.isEmpty ? "微信流" : subfolderPath
        return try write(
            urls: urls,
            root: folder.appendingPathComponent(folderName, isDirectory: true),
            chatName: chatName,
            sceneName: sceneName
        )
    }

    /// The shared assembly: one `聊天名.md` (merged when a same-named note is
    /// already there, uniquely numbered otherwise) and `附件/` holding the
    /// original archive plus any media unpacked from it. `root` is created if
    /// absent; its nearest existing parent must already be writable.
    @discardableResult
    private static func write(
        urls: [URL],
        root: URL,
        chatName: String?,
        sceneName: String?
    ) throws -> [URL] {
        guard !urls.isEmpty else { throw Failure.unreadableArchive }
        let attachments = root.appendingPathComponent("附件", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: attachments, withIntermediateDirectories: true)

        var written: [URL] = []
        for url in urls {
            let data = try Data(contentsOf: url, options: .mappedIfSafe)
            let transcript = try? WeChatNativeArchive.transcript(data)
            guard let archive = try FolderDelivery.save(
                [url],
                to: attachments,
                checkCancellation: {}
            ).first else { throw Failure.unreadableArchive }
            let media = extractMedia(
                from: data,
                transcript: transcript,
                to: attachments
            )

            let title = ObsidianNote.title(
                chatName: chatName,
                transcript: transcript,
                archiveName: url.lastPathComponent
            )
            let markdown = ObsidianNote.render(
                title: title,
                chatName: chatName,
                sceneName: sceneName,
                createdAt: Date(),
                transcript: transcript,
                archiveName: archive.lastPathComponent,
                attachments: media
            )
            let preferred = root.appendingPathComponent(DisplayName.sanitize(title) + ".md")
            if let transcript,
               let existing = try? String(contentsOf: preferred, encoding: .utf8) {
                switch ObsidianNote.merge(
                    existingMarkdown: existing,
                    transcript: transcript,
                    attachments: media,
                    archiveName: archive.lastPathComponent,
                    chatName: chatName,
                    sceneName: sceneName,
                    mergedAt: Date()
                ) {
                case .merged(let merged):
                    try Data(merged.utf8).write(to: preferred, options: .atomic)
                    written.append(preferred)
                    continue
                case .nothingNew:
                    written.append(preferred)
                    continue
                case .notApplicable:
                    break
                }
            }
            let note = uniqueURL(
                in: root,
                name: DisplayName.sanitize(title) + ".md"
            )
            try Data(markdown.utf8).write(to: note, options: .atomic)
            written.append(note)
        }
        return written
    }

    private static func extractMedia(
        from data: Data,
        transcript: WeChatNativeArchive.Transcript?,
        to attachments: URL
    ) -> [String: String] {
        guard transcript != nil else { return [:] }
        let staging = FileManager.default.temporaryDirectory
            .appendingPathComponent("wechatbridge-media-" + UUID().uuidString, isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: staging, withIntermediateDirectories: true)
            defer { try? FileManager.default.removeItem(at: staging) }
            let names = try WeChatNativeArchive.extract(data, to: staging)
                .filter { $0 != transcript?.path }
            let sources = names.map { staging.appendingPathComponent($0) }
            let saved = try FolderDelivery.save(sources, to: attachments, checkCancellation: {})
            var result: [String: String] = [:]
            for (source, destination) in zip(sources, saved) {
                result[source.lastPathComponent] = destination.lastPathComponent
            }
            return result
        } catch {
            return [:]
        }
    }

    private static func uniqueURL(in folder: URL, name: String) -> URL {
        let ext = (name as NSString).pathExtension
        let stem = (name as NSString).deletingPathExtension
        var number = 1
        while true {
            let suffix = number == 1 ? "" : " \(number)"
            let candidate = folder.appendingPathComponent("\(stem)\(suffix).\(ext)")
            if !FileManager.default.fileExists(atPath: candidate.path) { return candidate }
            number += 1
        }
    }
}
