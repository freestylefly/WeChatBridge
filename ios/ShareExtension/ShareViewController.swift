import SwiftUI
import UIKit
import WeChatBridgeCore

/// The `com.apple.share-services` entry point: copy what was shared into the
/// group container and stop. Nothing is parsed or delivered here — an
/// extension has a small memory ceiling, cannot present a second share sheet
/// that other apps' extensions reliably appear in, and cannot formally open
/// its containing app. The relay app does all of that later.
///
/// The import is the macOS extension's, step for step (see
/// `macos/Sources/WeChatBridgeShare/ShareViewController.swift`): one atomic
/// batch, never half of one, and the files are durable before anything is
/// told about them.
@objc(ShareViewController)
final class ShareViewController: UIViewController {
    private let model = ReceiptModel()
    /// `viewDidLoad` is not promised to run once, and the import is not idempotent.
    private var hasStarted = false

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .clear

        let host = UIHostingController(rootView: ReceiptView(model: model) { [weak self] in
            self?.finish()
        })
        host.view.backgroundColor = .clear
        addChild(host)
        host.view.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(host.view)
        NSLayoutConstraint.activate([
            host.view.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            host.view.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            host.view.topAnchor.constraint(equalTo: view.topAnchor),
            host.view.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        host.didMove(toParent: self)

        guard !hasStarted, let context = extensionContext else { return }
        hasStarted = true
        Task { @MainActor in
            await run(context: context)
        }
    }

    @MainActor
    private func run(context: NSExtensionContext) async {
        let sources = Self.flatten(inputItems: context.inputItems)
        let inbox = try? Inbox.resolve()
        guard !sources.isEmpty else {
            fail(InboxError.emptyShare, inbox: inbox)
            return
        }
        guard let inbox else {
            fail(InboxError.containerUnavailable(identifier: AppGroup.identifier), inbox: nil)
            return
        }
        let staging: BatchStaging
        do {
            staging = try BatchStaging.create(in: inbox)
        } catch {
            fail(error, inbox: inbox)
            return
        }

        var manifestItems: [ManifestItem] = []
        var diagnostics: [AttachmentDiagnostic] = []
        var failure: Error?
        for source in sources {
            switch await AttachmentImporter.importAttachment(source, into: staging) {
            case .success(let imported):
                manifestItems.append(imported.manifestItem)
                diagnostics.append(imported.diagnostic)
            case .failure(let error):
                if let rejection = error as? ImportRejection {
                    diagnostics.append(rejection.diagnostic)
                }
                failure = error
            }
            if failure != nil { break }
        }

        let record = ImportDiagnostics(
            batchID: staging.batchID,
            createdAt: Date(),
            extensionVersion: Self.bundleVersion,
            action: .collect,
            inputItemCount: (context.inputItems as? [NSExtensionItem])?.count ?? 0,
            succeeded: failure == nil,
            attachments: diagnostics
        )

        // A partially copied share is never published.
        if let failure {
            staging.discard()
            DiagnosticsLog.record(record, in: inbox)
            fail(failure, inbox: inbox)
            return
        }

        do {
            _ = try staging.commit(
                manifest: BatchManifest(
                    batchID: staging.batchID,
                    createdAt: record.createdAt,
                    items: manifestItems,
                    action: .collect
                ),
                diagnostics: record,
                intent: nil,
                in: inbox
            )
        } catch {
            staging.discard()
            fail(error, inbox: inbox)
            return
        }

        DiagnosticsLog.record(record, in: inbox)
        inbox.pruneStaging()
        InboxSignal.post()

        model.state = .saved(
            names: manifestItems.map(\.displayName),
            byteCount: manifestItems.reduce(0) { $0 + $1.byteCount }
        )
        // Long enough to read one line, short enough that the user is back in
        // WeChat before they think about it. The button closes it sooner.
        try? await Task.sleep(nanoseconds: 1_200_000_000)
        finish()
    }

    private var finished = false

    private func finish() {
        guard !finished else { return }
        finished = true
        if case .failed(let message) = model.state {
            extensionContext?.cancelRequest(withError: NSError(
                domain: "com.xiangming.wechatbridge.share",
                code: 1,
                userInfo: [NSLocalizedDescriptionKey: message]
            ))
        } else {
            extensionContext?.completeRequest(returningItems: nil, completionHandler: nil)
        }
    }

    /// The failure stays on screen until dismissed: unlike the Mac there is no
    /// resident app to report it, so this card is the first place it is seen.
    /// It is also left in the group container for the app's next launch.
    private func fail(_ error: Error, inbox: Inbox?) {
        let message = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        if let inbox {
            ShareFailure.record(ShareFailure(at: Date(), action: .collect, message: message), in: inbox)
            InboxSignal.post()
        }
        model.state = .failed(message)
    }

    static func flatten(inputItems: [Any]) -> [AttachmentSource] {
        var sources: [AttachmentSource] = []
        for (itemIndex, raw) in inputItems.enumerated() {
            guard let item = raw as? NSExtensionItem else { continue }
            for (attachmentIndex, provider) in (item.attachments ?? []).enumerated() {
                sources.append(AttachmentSource(
                    provider: provider,
                    itemIndex: itemIndex,
                    attachmentIndex: attachmentIndex
                ))
            }
        }
        return sources
    }

    private static var bundleVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "?"
    }
}
