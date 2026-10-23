import WeChatBridgeCore
import Foundation
import UniformTypeIdentifiers

struct AttachmentSource {
    let provider: NSItemProvider
    let itemIndex: Int
    let attachmentIndex: Int
}

struct ImportedAttachment {
    let manifestItem: ManifestItem
    let diagnostic: AttachmentDiagnostic
}

/// Copies one shared attachment into a staging batch.
///
/// Every rule here follows from one documented fact: the temporary file handed
/// to `loadFileRepresentation` is deleted the moment that completion handler
/// returns. The copy therefore happens *inside* the callback, and the only
/// things crossing the queue boundary are value types.
enum AttachmentImporter {
    private struct CopiedFile: Sendable {
        let displayName: String
        let byteCount: Int64
    }

    enum Failure: Error, LocalizedError {
        case providerReturnedNothing

        var errorDescription: String? {
            switch self {
            case .providerReturnedNothing: return L10n.text("系统没有返回文件。")
            }
        }
    }

    static func importAttachment(
        _ source: AttachmentSource,
        into staging: BatchStaging,
        itemID: UUID = UUID()
    ) async -> Result<ImportedAttachment, Error> {
        let provider = source.provider
        let registered = provider.registeredTypeIdentifiers
        let suggestedName = provider.suggestedName
        let suggestedExtension = (suggestedName as NSString?)?.pathExtension
        // Never probe `public.file-url` just for a name: it points into the
        // host's container and makes the sandboxed extension show the TCC
        // "access other apps' data" prompt on every share.
        let preferredName = suggestedName?.isEmpty == false ? suggestedName : nil

        func diagnostic(
            selected: String?,
            strategy: LoadStrategy?,
            byteCount: Int64?,
            failure: String?
        ) -> AttachmentDiagnostic {
            AttachmentDiagnostic(
                itemIndex: source.itemIndex,
                attachmentIndex: source.attachmentIndex,
                registeredTypeIdentifiers: registered,
                hasSuggestedName: suggestedName?.isEmpty == false,
                suggestedNameExtension: suggestedExtension?.isEmpty == false ? suggestedExtension : nil,
                nameSource: preferredName != nil ? .suggested : (strategy == .fileURL ? .fileURL : .temporaryFile),
                selectedTypeIdentifier: selected,
                loadStrategy: strategy,
                byteCount: byteCount,
                failure: failure
            )
        }

        guard let representation = Representation.choose(from: registered) else {
            return .failure(
                ImportRejection(
                    error: InboxError.unsupportedAttachment(types: registered),
                    diagnostic: diagnostic(
                        selected: nil,
                        strategy: nil,
                        byteCount: nil,
                        failure: "\(InboxError.unsupportedAttachment(types: registered))"
                    )
                )
            )
        }

        do {
            let copied: CopiedFile
            switch representation.strategy {
            case .fileRepresentation:
                copied = try await loadFileRepresentation(
                    provider: provider,
                    typeIdentifier: representation.typeIdentifier,
                    preferredName: preferredName,
                    staging: staging,
                    itemID: itemID
                )
            case .fileURL:
                copied = try await loadFileURL(
                    provider: provider,
                    typeIdentifier: representation.typeIdentifier,
                    preferredName: preferredName,
                    staging: staging,
                    itemID: itemID
                )
            }

            let manifestItem = ManifestItem(
                id: itemID,
                displayName: copied.displayName,
                relativePath: staging.relativePath(for: itemID, displayName: copied.displayName),
                contentType: representation.typeIdentifier,
                byteCount: copied.byteCount,
                itemIndex: source.itemIndex,
                attachmentIndex: source.attachmentIndex,
                loadStrategy: representation.strategy
            )
            return .success(
                ImportedAttachment(
                    manifestItem: manifestItem,
                    diagnostic: diagnostic(
                        selected: representation.typeIdentifier,
                        strategy: representation.strategy,
                        byteCount: copied.byteCount,
                        failure: nil
                    )
                )
            )
        } catch {
            return .failure(
                ImportRejection(
                    error: error,
                    diagnostic: diagnostic(
                        selected: representation.typeIdentifier,
                        strategy: representation.strategy,
                        byteCount: nil,
                        failure: "\(error)"
                    )
                )
            )
        }
    }

    private static func loadFileRepresentation(
        provider: NSItemProvider,
        typeIdentifier: String,
        preferredName: String?,
        staging: BatchStaging,
        itemID: UUID
    ) async throws -> CopiedFile {
        try await withCheckedThrowingContinuation { continuation in
            provider.loadFileRepresentation(forTypeIdentifier: typeIdentifier) { temporaryURL, error in
                // Everything below runs before this closure returns. Dispatching
                // the copy elsewhere would race the system deleting the file.
                if let error {
                    continuation.resume(throwing: error)
                    return
                }
                guard let temporaryURL else {
                    continuation.resume(throwing: Failure.providerReturnedNothing)
                    return
                }
                do {
                    continuation.resume(returning: try copy(
                        from: temporaryURL,
                        preferredName: preferredName,
                        typeIdentifier: typeIdentifier,
                        staging: staging,
                        itemID: itemID,
                        coordinated: false
                    ))
                } catch {
                    continuation.resume(throwing: error)
                }
            }
        }
    }

    /// Fallback for a provider that only offers `public.file-url`.
    ///
    /// The URL points into the host's storage, so access is bracketed by a
    /// security scope (when one was granted) and taken through a file
    /// coordinator, which is what Apple requires for an in-place representation.
    private static func loadFileURL(
        provider: NSItemProvider,
        typeIdentifier: String,
        preferredName: String?,
        staging: BatchStaging,
        itemID: UUID
    ) async throws -> CopiedFile {
        try await withCheckedThrowingContinuation { continuation in
            _ = provider.loadObject(ofClass: NSURL.self) { object, error in
                if let error {
                    continuation.resume(throwing: error)
                    return
                }
                guard let url = object as? URL else {
                    continuation.resume(throwing: Failure.providerReturnedNothing)
                    return
                }
                do {
                    continuation.resume(returning: try copy(
                        from: url,
                        preferredName: preferredName ?? url.lastPathComponent,
                        typeIdentifier: typeIdentifier,
                        staging: staging,
                        itemID: itemID,
                        coordinated: true
                    ))
                } catch {
                    continuation.resume(throwing: error)
                }
            }
        }
    }

    private static func copy(
        from source: URL,
        preferredName: String?,
        typeIdentifier: String,
        staging: BatchStaging,
        itemID: UUID,
        coordinated: Bool
    ) throws -> CopiedFile {
        let displayName = Representation.displayName(
            preferredName: preferredName,
            temporaryURL: source,
            typeIdentifier: typeIdentifier
        )
        let destination = try staging.destination(for: itemID, displayName: displayName)

        let scoped = source.startAccessingSecurityScopedResource()
        defer { if scoped { source.stopAccessingSecurityScopedResource() } }

        if coordinated {
            var coordinatorError: NSError?
            var copyError: Error?
            NSFileCoordinator().coordinate(
                readingItemAt: source,
                options: [.withoutChanges],
                error: &coordinatorError
            ) { url in
                do {
                    try FileManager.default.copyItem(at: url, to: destination)
                } catch {
                    copyError = error
                }
            }
            if let coordinatorError { throw coordinatorError }
            if let copyError { throw copyError }
        } else {
            try FileManager.default.copyItem(at: source, to: destination)
        }

        let size = (try? destination.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
        return CopiedFile(displayName: displayName, byteCount: Int64(size))
    }

}

/// Carries the redacted diagnostic alongside the failure so a rejected batch
/// still explains itself in the P0 report.
struct ImportRejection: Error, LocalizedError {
    let error: Error
    let diagnostic: AttachmentDiagnostic

    var errorDescription: String? {
        (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
    }
}
