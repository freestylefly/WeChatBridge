import WeChatBridgeCore
import Foundation

/// Writes a WeChat archive into an Obsidian vault as Markdown plus a durable
/// copy of the original ZIP. Keeping the archive makes a parsing change or a
/// future converter able to rebuild the note without asking WeChat again.
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

    @discardableResult
    static func deliver(
        urls: [URL],
        vaultPath: String,
        subfolder: String,
        chatName: String?,
        sceneName: String?
    ) throws -> [URL] {
        guard !urls.isEmpty else { throw Failure.unreadableArchive }
        let vault = URL(fileURLWithPath: vaultPath, isDirectory: true)
        try FolderDelivery.validateFolder(vault)

        let subfolderPath = DisplayName.subfolderPath(subfolder)
        let folderName = subfolderPath.isEmpty ? "微信流" : subfolderPath
        let root = vault.appendingPathComponent(folderName, isDirectory: true)
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
