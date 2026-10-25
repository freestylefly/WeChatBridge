import SwiftUI
import UIKit
import WeChatBridgeCore

/// The entry point of both extensions: 「微信流」 in the share row
/// (`com.apple.share-services`) and 「分批收集到微信流」 in the action list
/// (`com.apple.ui-services`, `CollectMarker.declared`). Copy what was shared
/// into the group container and stop. Only the chat's TXT is read, for the
/// receipt and the search anchor; nothing is delivered here — an
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
            // A signing problem, not the user's: the group id goes to the log only.
            NSLog("WeChatBridge share: %@", InboxError.containerUnavailable(identifier: AppGroup.identifier).errorDescription ?? "")
            model.state = .failed(Self.installBroken)
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
                // WeChat never exports an empty file; one arriving means the
                // export failed upstream, and an empty card would only hide it.
                if imported.manifestItem.byteCount == 0 {
                    failure = EmptyFile(name: imported.manifestItem.displayName)
                }
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

        let joinsCollection = CollectMarker.declared
        let committed: URL
        do {
            if joinsCollection { try CollectMarker.mark(staging.directory) }
            committed = try staging.commit(
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

        let chat = Self.readChat(manifestItems, in: committed)
        // Writing the clipboard asks nothing of the user; only reading does.
        let anchor = chat.flatMap { joinsCollection || $0.count == SearchAnchor.forwardLimit ? $0.keyword : nil }
        if let anchor { UIPasteboard.general.string = anchor }
        if joinsCollection {
            model.state = .collected(ReceiptModel.Collected(
                number: CollectMarker.batchNumber(in: inbox),
                count: chat?.count,
                start: chat?.start,
                end: chat?.end,
                anchor: anchor
            ))
        } else {
            model.state = .saved(
                names: manifestItems.map(\.displayName),
                byteCount: manifestItems.reduce(0) { $0 + $1.byteCount },
                anchor: anchor
            )
        }
        let names = manifestItems.map(\.displayName)
        // The next batch follows at once; a notification per batch would only nag.
        if !joinsCollection {
            await QuickDeliver.post(
            batchID: staging.batchID,
            head: names.count == 1 ? names[0] : "\(names.count) 个文件",
                defaults: UserDefaults(suiteName: AppGroup.identifier) ?? .standard
            )
        }
        // Long enough to read one line, short enough that the user is back in
        // WeChat before they think about it. The button closes it sooner.
        // The search line needs a moment longer to read.
        try? await Task.sleep(nanoseconds: anchor == nil ? 1_200_000_000 : 2_000_000_000)
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

    struct Chat {
        let count: Int
        let start: Date?
        let end: Date?
        let keyword: String?
    }

    /// The one WeChat export in the share, read from the mapped file: only the
    /// central directory and the TXT entry are touched, never the media.
    /// Anything else, or a TXT that does not parse, gives nil.
    static func readChat(_ items: [ManifestItem], in batch: URL) -> Chat? {
        let archives = items.filter { ($0.displayName as NSString).pathExtension.lowercased() == "zip" }
        guard archives.count == 1,
              let data = try? Data(contentsOf: batch.appendingPathComponent(archives[0].relativePath), options: .alwaysMapped),
              let transcript = try? WeChatNativeArchive.transcript(data),
              let records = transcript.records, !records.isEmpty else { return nil }
        return Chat(count: records.count, start: transcript.start, end: transcript.end, keyword: SearchAnchor.keyword(records))
    }

    static let installBroken = "微信流没装好，请重新安装后再试。"

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

private struct EmptyFile: LocalizedError {
    let name: String
    var errorDescription: String? { "「\(name)」是空文件，没有存入。请回到微信重新导出。" }
}
