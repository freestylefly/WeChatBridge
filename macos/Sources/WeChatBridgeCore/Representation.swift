import Foundation
import UniformTypeIdentifiers

/// Which representation of an attachment WeChatBridge asks the host for.
public struct Representation: Sendable, Hashable {
    public let typeIdentifier: String
    public let strategy: LoadStrategy

    public init(typeIdentifier: String, strategy: LoadStrategy) {
        self.typeIdentifier = typeIdentifier
        self.strategy = strategy
    }

    /// Picks the representation that loses the least information.
    ///
    /// Registered identifiers arrive most-specific first by convention, so the
    /// first match in each tier wins. `public.file-url` is handled as its own
    /// tier even though it conforms to `public.data`, because asking for a
    /// *file representation of a URL type* can produce a file containing the
    /// URL rather than the file it points at.
    public static func choose(from identifiers: [String]) -> Representation? {
        let types = identifiers.compactMap { identifier in UTType(identifier).map { (identifier, $0) } }

        if let zip = types.first(where: { $0.1 == .zip }) {
            return Representation(typeIdentifier: zip.0, strategy: .fileRepresentation)
        }
        if let archive = types.first(where: { $0.1.conforms(to: .archive) && !$0.1.conforms(to: .url) }) {
            return Representation(typeIdentifier: archive.0, strategy: .fileRepresentation)
        }
        if let data = types.first(where: { $0.1.conforms(to: .data) && !$0.1.conforms(to: .url) }) {
            return Representation(typeIdentifier: data.0, strategy: .fileRepresentation)
        }
        if let fileURL = types.first(where: { $0.1.conforms(to: .fileURL) }) {
            return Representation(typeIdentifier: fileURL.0, strategy: .fileURL)
        }
        return nil
    }

    /// The name the user should see in history.
    ///
    /// Measured on macOS 15: the temporary file `loadFileRepresentation` vends
    /// is named after the *type* — "Zip归档.zip" — so it is a reliable source of
    /// an extension and a terrible source of a name. A name recovered from the
    /// provider wins; the temporary file only fills in what is missing.
    public static func displayName(
        preferredName: String?,
        temporaryURL: URL,
        typeIdentifier: String
    ) -> String {
        let raw = (preferredName?.isEmpty == false) ? preferredName : temporaryURL.lastPathComponent
        let fallbackExtension = temporaryURL.pathExtension.isEmpty
            ? UTType(typeIdentifier)?.preferredFilenameExtension
            : temporaryURL.pathExtension
        return DisplayName.sanitize(raw, fallbackExtension: fallbackExtension)
    }
}
