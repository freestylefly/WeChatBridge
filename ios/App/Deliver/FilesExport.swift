import Foundation
import WeChatBridgeCore

/// Copies a chat into the app's Documents folder, which Files shows as
/// 「我的iPhone › 微信流」. Apps that add attachments through the file picker
/// (千问) can then take the original files, which a share cannot carry to them.
enum FilesExport {
    static var root: URL { URL.documentsDirectory }

    /// Files lists 「微信流」 only once Documents holds something; on a phone
    /// that never exported, the user would find no folder to open.
    static func placeGuide(in root: URL = root) {
        let guide = root.appendingPathComponent("使用说明.txt")
        guard !FileManager.default.fileExists(atPath: guide.path) else { return }
        let text = "在中转站长按一条聊天，选「放到「文件」」，这里会多出一个以聊天命名的文件夹，里面是聊天记录和全部原文件。\n在千问等 App 里点「添加文件」→ 我的iPhone → 微信流，进文件夹勾选要发的文件。\n"
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

    /// One flat folder per chat: the transcript and every file the ZIP held,
    /// under their own names. Exporting the same chat again replaces it.
    static func export(_ batch: ReadyBatch, name: String, into root: URL = root, fileManager: FileManager = .default) throws -> URL {
        let folder = root.appendingPathComponent(name, isDirectory: true)
        try? fileManager.removeItem(at: folder)
        try fileManager.createDirectory(at: folder, withIntermediateDirectories: true)
        let staging = fileManager.temporaryDirectory.appendingPathComponent("export-\(UUID().uuidString)", isDirectory: true)
        defer { try? fileManager.removeItem(at: staging) }

        var used: Set<String> = []
        func place(_ file: URL, as leaf: String) throws {
            try fileManager.copyItem(at: file, to: folder.appendingPathComponent(DeliveryPlanner.uniqueName(leaf, used: &used)))
        }
        for item in batch.items {
            guard item.url.pathExtension.lowercased() == "zip",
                  let data = try? Data(contentsOf: item.url, options: .mappedIfSafe),
                  (try? WeChatNativeArchive.transcript(data)) != nil else {
                // Not a WeChat export: the file itself is what the user has.
                try place(item.url, as: item.displayName)
                continue
            }
            let unpacked = staging.appendingPathComponent(item.id.uuidString, isDirectory: true)
            try fileManager.createDirectory(at: unpacked, withIntermediateDirectories: true)
            for path in try WeChatNativeArchive.extract(data, to: unpacked) {
                let leaf = (path as NSString).lastPathComponent
                var isDirectory: ObjCBool = false
                let file = unpacked.appendingPathComponent(path)
                guard !leaf.hasPrefix("."), !path.contains("__MACOSX"),
                      fileManager.fileExists(atPath: file.path, isDirectory: &isDirectory), !isDirectory.boolValue else { continue }
                try place(file, as: leaf)
            }
        }
        return folder
    }
}
