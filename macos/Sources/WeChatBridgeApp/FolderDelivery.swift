import Darwin
import WeChatBridgeCore
import Foundation

enum FolderDelivery {
    enum Failure: LocalizedError {
        case invalidFolder, invalidSource
        case cleanupFailed(Error)

        var errorDescription: String? {
            switch self {
            case .invalidFolder: return L10n.text("请选择一个可写入的文件夹。")
            case .invalidSource: return L10n.text("待保存的文件无法读取，请重新导出。")
            case .cleanupFailed(let error): return L10n.format("保存未完成，部分文件可能已留在目标文件夹：%@", error.localizedDescription)
            }
        }
    }

    static func validateFolder(_ folder: URL) throws {
        guard folder.isFileURL else { throw Failure.invalidFolder }
        let resolved = folder.resolvingSymlinksInPath()
        let values = try resolved.resourceValues(forKeys: [.isDirectoryKey])
        guard values.isDirectory == true, FileManager.default.isWritableFile(atPath: resolved.path) else {
            throw Failure.invalidFolder
        }
    }

    /// Copy first, then publish complete files with exclusive renames. On
    /// cancellation or failure only this call's files are rolled back; inbox
    /// originals and every pre-existing destination remain untouched.
    static func save(_ urls: [URL], to folder: URL, checkCancellation: () throws -> Void) throws -> [URL] {
        try checkCancellation()
        let scoped = folder.startAccessingSecurityScopedResource()
        defer { if scoped { folder.stopAccessingSecurityScopedResource() } }
        try validateFolder(folder)
        guard !urls.isEmpty else { return [] }
        let destination = folder.resolvingSymlinksInPath()
        let staging = try makeStagingDirectory(in: destination, checkCancellation: checkCancellation)
        defer { try? FileManager.default.removeItem(at: staging) }
        var published: [(url: URL, identity: Identity)] = []

        do {
            var prepared: [(url: URL, name: String, identity: Identity)] = []
            for (index, source) in urls.enumerated() {
                try checkCancellation()
                let staged = staging.appendingPathComponent(String(index))
                let identity = try copy(source, to: staged, checkCancellation: checkCancellation)
                var name = DisplayName.sanitize(source.lastPathComponent)
                // Reserve room for collision suffixes even for unusually long
                // extensions, which DisplayName intentionally keeps attached.
                while name.utf8.count > 200 { name.removeLast() }
                prepared.append((staged, name, identity))
            }
            for file in prepared {
                var number = 1
                while true {
                    try checkCancellation()
                    let candidate = destination.appendingPathComponent(uniqueName(file.name, number: number))
                    let result = file.url.withUnsafeFileSystemRepresentation { source in
                        candidate.withUnsafeFileSystemRepresentation { target in
                            renamex_np(source!, target!, UInt32(RENAME_EXCL))
                        }
                    }
                    if result == 0 {
                        published.append((candidate, file.identity))
                        break
                    }
                    let code = errno
                    guard code == EEXIST || code == ENOTEMPTY else { throw posixError(code, url: candidate) }
                    number += 1
                }
            }
            try checkCancellation()
            return published.map(\.url)
        } catch {
            var cleanupFailed = false
            for file in published.reversed() {
                // Someone may replace an output while a save is being stopped.
                // Never remove that replacement, nor recursively remove a dir.
                var info = stat()
                let result = file.url.withUnsafeFileSystemRepresentation { lstat($0!, &info) }
                if result == 0 {
                    guard Identity(info) == file.identity else { continue }
                    if file.url.withUnsafeFileSystemRepresentation({ unlink($0!) }) != 0, errno != ENOENT {
                        cleanupFailed = true
                    }
                } else if errno != ENOENT { cleanupFailed = true }
            }
            if cleanupFailed { throw Failure.cleanupFailed(error) }
            throw error
        }
    }

    private struct Identity: Equatable {
        let device: dev_t
        let inode: ino_t
        init(_ info: stat) { device = info.st_dev; inode = info.st_ino }
    }

    private static func copy(_ source: URL, to staged: URL, checkCancellation: () throws -> Void) throws -> Identity {
        guard source.isFileURL else { throw Failure.invalidSource }
        let sourceFD = source.withUnsafeFileSystemRepresentation { open($0!, O_RDONLY | O_NOFOLLOW | O_NONBLOCK) }
        guard sourceFD >= 0 else { throw posixError(errno, url: source) }
        let input = FileHandle(fileDescriptor: sourceFD, closeOnDealloc: true)
        defer { try? input.close() }
        var info = stat()
        guard fstat(sourceFD, &info) == 0, info.st_mode & S_IFMT == S_IFREG else { throw Failure.invalidSource }

        let outputFD = staged.withUnsafeFileSystemRepresentation { open($0!, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, 0o600) }
        guard outputFD >= 0 else { throw posixError(errno, url: staged) }
        let output = FileHandle(fileDescriptor: outputFD, closeOnDealloc: true)
        defer { try? output.close() }
        while true {
            try checkCancellation()
            guard let data = try input.read(upToCount: 1_048_576), !data.isEmpty else { break }
            try checkCancellation()
            try output.write(contentsOf: data)
        }
        try checkCancellation()
        try output.synchronize()
        guard fstat(outputFD, &info) == 0 else { throw posixError(errno, url: staged) }
        try output.close()
        return Identity(info)
    }

    private static func makeStagingDirectory(in folder: URL, checkCancellation: () throws -> Void) throws -> URL {
        while true {
            try checkCancellation()
            let staging = folder.appendingPathComponent(".wechatbridge-forward-" + UUID().uuidString, isDirectory: true)
            if staging.withUnsafeFileSystemRepresentation({ mkdir($0!, 0o700) }) == 0 { return staging }
            let code = errno
            guard code == EEXIST else { throw posixError(code, url: staging) }
        }
    }

    private static func uniqueName(_ name: String, number: Int) -> String {
        guard number > 1 else { return name }
        let ext = (name as NSString).pathExtension
        let stem = (name as NSString).deletingPathExtension
        return stem + " (\(number))" + (ext.isEmpty ? "" : "." + ext)
    }

    private static func posixError(_ code: Int32, url: URL) -> NSError {
        NSError(domain: NSPOSIXErrorDomain, code: Int(code), userInfo: [NSFilePathErrorKey: url.path])
    }
}
