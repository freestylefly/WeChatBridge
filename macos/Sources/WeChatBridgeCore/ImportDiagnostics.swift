import Foundation

/// A redacted record of what the host actually handed over.
///
/// The whole P0 question — which UTIs WeChat registers, how many attachments a
/// multi-chat forward produces, whether a suggested name exists — is answered by
/// this file. It deliberately stores no file contents and no full names: only
/// the extension, so a report can be pasted into an issue without leaking a chat
/// title.
/// Where the file's visible name came from. The system's temporary file is
/// named after the *type* ("Zip归档.zip"), not after what the user shared, so
/// which source won is the difference between a recognisable name and a generic
/// one.
public enum NameSource: String, Codable, Sendable {
    case suggested
    case fileURL
    case temporaryFile
}

public struct AttachmentDiagnostic: Codable, Sendable, Hashable {
    public let itemIndex: Int
    public let attachmentIndex: Int
    public let registeredTypeIdentifiers: [String]
    public let hasSuggestedName: Bool
    public let suggestedNameExtension: String?
    public let nameSource: NameSource?
    public let selectedTypeIdentifier: String?
    public let loadStrategy: LoadStrategy?
    public let byteCount: Int64?
    public let failure: String?

    public init(
        itemIndex: Int,
        attachmentIndex: Int,
        registeredTypeIdentifiers: [String],
        hasSuggestedName: Bool,
        suggestedNameExtension: String?,
        nameSource: NameSource?,
        selectedTypeIdentifier: String?,
        loadStrategy: LoadStrategy?,
        byteCount: Int64?,
        failure: String?
    ) {
        self.itemIndex = itemIndex
        self.attachmentIndex = attachmentIndex
        self.registeredTypeIdentifiers = registeredTypeIdentifiers
        self.hasSuggestedName = hasSuggestedName
        self.suggestedNameExtension = suggestedNameExtension
        self.nameSource = nameSource
        self.selectedTypeIdentifier = selectedTypeIdentifier
        self.loadStrategy = loadStrategy
        self.byteCount = byteCount
        self.failure = failure
    }
}

public struct ImportDiagnostics: Codable, Sendable, Hashable {
    public static let currentSchemaVersion = 1
    public static let fileName = "diagnostics.json"

    public let schemaVersion: Int
    public let batchID: UUID
    public let createdAt: Date
    /// The extension build that produced the record. A field report that does
    /// not say which build it came from cannot be acted on.
    public let extensionVersion: String?
    /// Which Share-menu entry ran. Five bundles share one executable, so a
    /// report that does not say which entry produced it cannot be read.
    public let action: ShareAction
    public let inputItemCount: Int
    public let succeeded: Bool
    public let attachments: [AttachmentDiagnostic]

    public init(
        batchID: UUID,
        createdAt: Date,
        extensionVersion: String?,
        action: ShareAction,
        inputItemCount: Int,
        succeeded: Bool,
        attachments: [AttachmentDiagnostic],
        schemaVersion: Int = currentSchemaVersion
    ) {
        self.schemaVersion = schemaVersion
        self.batchID = batchID
        self.createdAt = createdAt
        self.extensionVersion = extensionVersion
        self.action = action
        self.inputItemCount = inputItemCount
        self.succeeded = succeeded
        self.attachments = attachments
    }

    public func encoded() -> Data? {
        try? BatchManifest.encoder().encode(self)
    }
}
