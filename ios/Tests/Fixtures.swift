import Foundation
import WeChatBridgeCore
import XCTest

/// Builds `ReadyBatch` values from the committed fixture ZIPs, each in its
/// own temporary batch directory, the way `InboxReader` would hand them out.
final class FixtureBatches {
    let root: URL

    init() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("FixtureBatches-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    deinit {
        try? FileManager.default.removeItem(at: root)
    }

    static func url(_ name: String) throws -> URL {
        let bundle = Bundle(for: FixtureBatches.self)
        let url = try XCTUnwrap(bundle.resourceURL).appendingPathComponent("Fixtures/\(name)")
        guard FileManager.default.fileExists(atPath: url.path) else {
            throw CocoaError(.fileNoSuchFile, userInfo: [NSFilePathErrorKey: url.path])
        }
        return url
    }

    func batch(_ names: String..., chatName: String? = nil) throws -> ReadyBatch {
        let id = UUID()
        let directory = root.appendingPathComponent(id.uuidString, isDirectory: true)
        let files = directory.appendingPathComponent("files", isDirectory: true)
        try FileManager.default.createDirectory(at: files, withIntermediateDirectories: true)
        let items = try names.map { name -> ReadyItem in
            let destination = files.appendingPathComponent(name)
            try FileManager.default.copyItem(at: Self.url(name), to: destination)
            let size = try FileManager.default.attributesOfItem(atPath: destination.path)[.size] as? Int64 ?? 0
            return ReadyItem(
                id: UUID(), batchID: id, displayName: name, url: destination,
                byteCount: size, contentType: "public.zip-archive", createdAt: Date(), action: .collect
            )
        }
        return ReadyBatch(
            id: id, directory: directory, createdAt: Date(), action: .collect,
            items: items, outcome: nil, chatName: chatName, isFirstSeen: false
        )
    }
}
