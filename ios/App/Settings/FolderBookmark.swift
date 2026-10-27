import Foundation

/// A folder the user picked in Files, kept across launches. iOS bookmarks are
/// implicitly security-scoped; `.withSecurityScope` is a macOS-only option.
enum FolderBookmark {
    enum Failure: LocalizedError {
        case notChosen
        case denied

        var errorDescription: String? {
            switch self {
            case .notChosen: "还没有选择文件夹，请先在设置里选择。"
            case .denied: "没有权限写入这个文件夹，请在设置里重新选择。"
            }
        }
    }

    /// Called with a URL fresh from the document picker.
    static func make(from url: URL) throws -> Data {
        let accessing = url.startAccessingSecurityScopedResource()
        defer { if accessing { url.stopAccessingSecurityScopedResource() } }
        return try url.bookmarkData(options: [], includingResourceValuesForKeys: nil, relativeTo: nil)
    }

    static func name(of bookmark: Data?) -> String? {
        guard let bookmark else { return nil }
        var stale = false
        return (try? URL(resolvingBookmarkData: bookmark, options: [], relativeTo: nil, bookmarkDataIsStale: &stale))?
            .lastPathComponent
    }

    /// Runs `body` with access open. A stale bookmark still resolves; it is
    /// renewed through `refresh` so the next use does not depend on luck.
    static func access<T>(_ bookmark: Data?, refresh: (Data) -> Void, _ body: (URL) throws -> T) throws -> T {
        guard let bookmark else { throw Failure.notChosen }
        var stale = false
        let url = try URL(resolvingBookmarkData: bookmark, options: [], relativeTo: nil, bookmarkDataIsStale: &stale)
        guard url.startAccessingSecurityScopedResource() else { throw Failure.denied }
        defer { url.stopAccessingSecurityScopedResource() }
        if stale, let renewed = try? url.bookmarkData(options: [], includingResourceValuesForKeys: nil, relativeTo: nil) {
            refresh(renewed)
        }
        return try body(url)
    }
}
