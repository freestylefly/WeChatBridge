import Foundation
import Photos
import UniformTypeIdentifiers
import WeChatBridgeCore

/// Copies a chat's attachments into the app's Documents folder, which Files
/// shows as 「我的iPhone › 微信流」, and its images and videos into Photos.
/// 千问 takes documents only from its file picker and media only from 相册,
/// and neither through a share beside the TXT.
enum FilesExport {
    static var root: URL { URL.documentsDirectory }

    /// Files lists 「微信流」 only once Documents holds something; on a phone
    /// that never exported, the user would find no folder to open.
    static func placeGuide(in root: URL = root) {
        let guide = root.appendingPathComponent("使用说明.txt")
        let text = "投递到豆包、千问时，聊天记录发过去（PDF 里带图片，或只发 TXT，可在「设置」里选）；视频存到「照片」，其他附件放进这里以聊天命名的文件夹。\n需要时在对方 App 里点「相册」选图片和视频，点「添加文件」→ 我的iPhone → 微信流 选附件。也可以在中转站长按一条聊天，选「存图片和附件」。\n"
        // Rewritten when the wording changes, so an older guide never lingers.
        guard (try? Data(contentsOf: guide)) != Data(text.utf8) else { return }
        try? Data(text.utf8).write(to: guide)
    }

    /// `标题 10-20 09.00`: the chat's own name, and its start so two exports of
    /// one group stay apart. Slashes and colons are not allowed in Files.
    static func folderName(title: String, date: Date) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "zh_CN")
        formatter.dateFormat = "MM-dd HH.mm"
        let clean = title.replacingOccurrences(of: "/", with: "-").replacingOccurrences(of: ":", with: "-")
        return "\(clean) \(formatter.string(from: date))"
    }

    static func folderName(for entry: RelayEntry) -> String {
        folderName(
            title: entry.summary?.title ?? entry.batch.chatName ?? entry.batch.items.first?.displayName ?? "聊天记录",
            date: entry.summary?.start ?? entry.batch.createdAt
        )
    }

    struct Exported {
        /// The chat's folder under 「微信流」; nil when no document went there.
        var folder: URL?
        /// Images and videos, when the caller asked for them apart.
        var media: [URL] = []
    }

    /// Photos takes these; 千问's file picker greys them out and its 相册
    /// picker is the only way they reach it.
    static func isMedia(_ name: String) -> Bool {
        guard let type = UTType(filenameExtension: (name as NSString).pathExtension) else { return false }
        return type.conforms(to: .image) || type.conforms(to: .movie)
    }

    /// One flat folder per chat with the files its TXT names, under their own
    /// names; the TXT itself goes out by share. With `mediaInto`, images and
    /// videos land there instead, for Photos. `inPDF` names the archive's
    /// images already drawn into the shared PDF. The folder is built aside and
    /// replaces the old one only once complete.
    @discardableResult
    static func export(_ batch: ReadyBatch, name: String, into root: URL = root, mediaInto mediaRoot: URL? = nil, inPDF: Set<String> = [], fileManager: FileManager = .default) throws -> Exported {
        let folder = root.appendingPathComponent(name, isDirectory: true)
        let staging = fileManager.temporaryDirectory.appendingPathComponent("export-\(UUID().uuidString)", isDirectory: true)
        defer { try? fileManager.removeItem(at: staging) }
        let built = staging.appendingPathComponent("folder", isDirectory: true)

        var used: Set<String> = []
        var usedMedia: Set<String> = []
        var media: [URL] = []
        func place(_ file: URL, as leaf: String) throws {
            if let mediaRoot, isMedia(leaf) {
                try fileManager.createDirectory(at: mediaRoot, withIntermediateDirectories: true)
                let destination = mediaRoot.appendingPathComponent(DeliveryPlanner.uniqueName(leaf, used: &usedMedia))
                try fileManager.copyItem(at: file, to: destination)
                media.append(destination)
                return
            }
            try fileManager.createDirectory(at: built, withIntermediateDirectories: true)
            try fileManager.copyItem(at: file, to: built.appendingPathComponent(DeliveryPlanner.uniqueName(leaf, used: &used)))
        }
        for item in batch.items {
            guard item.url.pathExtension.lowercased() == "zip",
                  let data = try? Data(contentsOf: item.url, options: .mappedIfSafe),
                  let transcript = try? WeChatNativeArchive.transcript(data) else {
                // Not a WeChat export: the file itself is what the user has.
                try place(item.url, as: item.displayName)
                continue
            }
            let unpacked = staging.appendingPathComponent(item.id.uuidString, isDirectory: true)
            try fileManager.createDirectory(at: unpacked, withIntermediateDirectories: true)
            for path in try WeChatNativeArchive.extract(data, to: unpacked) where path != transcript.path {
                let leaf = (path as NSString).lastPathComponent
                var isDirectory: ObjCBool = false
                let file = unpacked.appendingPathComponent(path)
                guard !leaf.hasPrefix("."), !path.contains("__MACOSX"), !inPDF.contains(leaf),
                      fileManager.fileExists(atPath: file.path, isDirectory: &isDirectory), !isDirectory.boolValue else { continue }
                try place(file, as: leaf)
            }
        }
        try? fileManager.removeItem(at: folder)
        guard fileManager.fileExists(atPath: built.path) else { return Exported(folder: nil, media: media) }
        try fileManager.createDirectory(at: root, withIntermediateDirectories: true)
        try fileManager.moveItem(at: built, to: folder)
        return Exported(folder: folder, media: media)
    }

    /// What one 「存图片和附件」 did, in the words the user reads.
    struct Summary {
        var folders: [String] = []
        var savedMedia = 0
        /// Media Photos refused or had no permission for, kept in the folders.
        var keptMedia = 0
        var photosDenied = false

        var isEmpty: Bool { folders.isEmpty && savedMedia == 0 }

        var message: String {
            var lines: [String] = []
            if savedMedia > 0 { lines.append("\(savedMedia) 个图片/视频已存到「照片」，在千问等 App 里点「相册」选取。") }
            if photosDenied {
                lines.append("没有添加照片的权限，图片和视频放进了下面的文件夹。可在「设置 › 微信流 › 照片」里打开。")
            } else if keptMedia > 0 {
                lines.append("\(keptMedia) 个图片/视频「照片」不收，放进了下面的文件夹。")
            }
            if !folders.isEmpty { lines.append("文件在「我的iPhone › 微信流 › \(folders.joined(separator: "、"))」，点「添加文件」选取。") }
            return lines.isEmpty ? "这条聊天只有文字，没有图片或附件。" : lines.joined(separator: "\n")
        }
    }

    struct Job {
        let batch: ReadyBatch
        let name: String
        var inPDF: Set<String> = []
    }

    /// Exports every job, its media into its own subfolder of `mediaRoot` so
    /// two chats' same-named images never meet. Names are made unique, so two
    /// jobs never replace each other's folder.
    static func stage(_ jobs: [Job], into root: URL = root, mediaRoot: URL) throws -> (names: [String], media: [[URL]]) {
        var used: Set<String> = []
        var names: [String] = []
        var media: [[URL]] = []
        for (index, job) in jobs.enumerated() {
            let name = DeliveryPlanner.uniqueName(job.name, used: &used)
            let exported = try export(job.batch, name: name, into: root, mediaInto: mediaRoot.appendingPathComponent("\(index)"), inPDF: job.inPDF)
            names.append(name)
            media.append(exported.media)
        }
        return (names, media)
    }

    /// Media Photos did not take go into their chat's folder instead.
    static func keep(_ files: [URL], in name: String, root: URL = root, fileManager: FileManager = .default) throws {
        guard !files.isEmpty else { return }
        let folder = root.appendingPathComponent(name, isDirectory: true)
        try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
        var used = Set((try? fileManager.contentsOfDirectory(atPath: folder.path)) ?? [])
        for file in files {
            try fileManager.copyItem(at: file, to: folder.appendingPathComponent(DeliveryPlanner.uniqueName(file.lastPathComponent, used: &used)))
        }
    }

    /// Images and videos into Photos, everything else into 「微信流」 folders.
    static func exportForPicking(_ jobs: [Job]) async throws -> Summary {
        let mediaRoot = FileManager.default.temporaryDirectory.appendingPathComponent("media-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: mediaRoot) }
        let (names, media) = try stage(jobs, mediaRoot: mediaRoot)
        var summary = Summary()
        let all = media.flatMap { $0 }
        if !all.isEmpty {
            let refused: Set<URL>
            switch await PhotosSaver.save(all) {
            case .denied:
                summary.photosDenied = true
                refused = Set(all)
            case .saved(let failed):
                refused = Set(failed)
            }
            summary.savedMedia = all.count - refused.count
            summary.keptMedia = refused.count
            for (name, files) in zip(names, media) {
                try keep(files.filter(refused.contains), in: name)
            }
        }
        summary.folders = names.filter { FileManager.default.fileExists(atPath: root.appendingPathComponent($0).path) }
        return summary
    }
}

enum PhotosSaver {
    enum Outcome { case denied, saved(failed: [URL]) }

    /// Add-only access is enough to save and asks less of the user. Asked
    /// before a share sheet opens, so the one-time prompt never lands on it.
    static func authorize() async -> Bool {
        let status = await PHPhotoLibrary.requestAuthorization(for: .addOnly)
        return status == .authorized || status == .limited
    }

    /// The assets land at the top of 最近项目, where a picker opens.
    static func save(_ files: [URL]) async -> Outcome {
        guard await authorize() else { return .denied }
        var failed: [URL] = []
        // One change each: a format Photos refuses must not sink the rest.
        for file in files {
            do {
                try await PHPhotoLibrary.shared().performChanges {
                    let options = PHAssetResourceCreationOptions()
                    options.originalFilename = file.lastPathComponent
                    let type: PHAssetResourceType = DeliveryPlanner.isImage(file.lastPathComponent) ? .photo : .video
                    PHAssetCreationRequest.forAsset().addResource(with: type, fileURL: file, options: options)
                }
            } catch {
                failed.append(file)
            }
        }
        return .saved(failed: failed)
    }
}
