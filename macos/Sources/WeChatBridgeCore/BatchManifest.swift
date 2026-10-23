import Foundation

/// How the file was pulled out of the host's `NSItemProvider`. Recorded because
/// the two paths have different failure modes, and a P0 field report is useless
/// without knowing which one ran.
public enum LoadStrategy: String, Codable, Sendable {
    /// `loadFileRepresentation(forTypeIdentifier:completionHandler:)`
    case fileRepresentation
    /// `loadObject(ofClass: NSURL.self)` — the provider only offered a file URL.
    case fileURL
}

public struct ManifestItem: Codable, Sendable, Hashable, Identifiable {
    public let id: UUID
    /// Sanitised, human-readable name. Also the on-disk filename inside the
    /// item's UUID directory, which is what a drop receives.
    public let displayName: String
    /// Relative to the batch directory. Absolute paths are never persisted: the
    /// group container root is resolved fresh on every launch.
    public let relativePath: String
    public let contentType: String?
    public let byteCount: Int64
    /// Position in the original `inputItems[].attachments[]` traversal, kept so
    /// history and delivery preserve the order the host sent.
    public let itemIndex: Int
    public let attachmentIndex: Int
    public let loadStrategy: LoadStrategy

    public init(
        id: UUID,
        displayName: String,
        relativePath: String,
        contentType: String?,
        byteCount: Int64,
        itemIndex: Int,
        attachmentIndex: Int,
        loadStrategy: LoadStrategy
    ) {
        self.id = id
        self.displayName = displayName
        self.relativePath = relativePath
        self.contentType = contentType
        self.byteCount = byteCount
        self.itemIndex = itemIndex
        self.attachmentIndex = attachmentIndex
        self.loadStrategy = loadStrategy
    }
}

public struct BatchManifest: Codable, Sendable, Hashable {
    public static let currentSchemaVersion = 1

    public let schemaVersion: Int
    public let batchID: UUID
    public let createdAt: Date
    /// Which Share-menu entry produced this batch. It belongs in the manifest
    /// rather than only in `intent.json` because the intent is consumed once and
    /// deleted, while the history has to keep saying where a batch came from for
    /// as long as its files exist.
    public let action: ShareAction
    public let items: [ManifestItem]

    public init(
        batchID: UUID,
        createdAt: Date,
        items: [ManifestItem],
        action: ShareAction = .clipboard,
        schemaVersion: Int = currentSchemaVersion
    ) {
        self.schemaVersion = schemaVersion
        self.batchID = batchID
        self.createdAt = createdAt
        self.action = action
        self.items = items
    }

    /// `action` was added after the first builds shipped, so it is decoded as
    /// optional and defaults to the clipboard copy. The schema version stays 1
    /// on purpose: an older app reading a newer manifest simply ignores the
    /// field, which is not a reason to hide a batch the user can still see on disk.
    public init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        schemaVersion = try container.decode(Int.self, forKey: .schemaVersion)
        batchID = try container.decode(UUID.self, forKey: .batchID)
        createdAt = try container.decode(Date.self, forKey: .createdAt)
        action = try container.decodeIfPresent(ShareAction.self, forKey: .action) ?? .clipboard
        items = try container.decode([ManifestItem].self, forKey: .items)
    }

    /// ISO-8601 dates keep a manifest readable during a P0 field investigation;
    /// a bare `Double` would need the app to interpret it.
    public static func encoder() -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]
        return encoder
    }

    public static func decoder() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return decoder
    }
}
