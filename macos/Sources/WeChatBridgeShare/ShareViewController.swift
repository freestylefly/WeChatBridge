import AppKit
import WeChatBridgeCore
import Foundation

/// The `com.apple.share-services` entry point, and as close to nothing as the
/// platform allows.
///
/// This used to present a panel in the host's share sheet saying what WeChatBridge was
/// doing. Measured on the signed build and rejected by the user (2026-09-05
/// evening): the sheet arrives with the system's own zoom animation, sits over
/// WeChat for the best part of a second and reports something the user can
/// already see happen. A copy step has nothing to say.
///
/// **Measured, not assumed**: the interface cannot be removed outright.
/// `NSExtensionPrincipalClass` was pointed at an `NSExtensionRequestHandling`
/// object with no view at all, and both attempts (main-actor and detached,
/// 2026-09-05 17:00 and 17:01, `~/Library/Logs/DiagnosticReports/WeChatBridgeShare-*.ips`)
/// died the same way: `-[NSSharingUIExtensionContext viewController]` asserts
/// and the process is killed with SIGTRAP a few seconds in, after the bytes
/// have been copied and before the batch is committed. `com.apple.share-services`
/// requires a view controller.
///
/// So this is §12.1's documented fallback, and the honest description of it is:
/// there is still a view, it is 1×1 and transparent, and the host still opens
/// and closes a sheet around it. The import starts in `viewDidLoad` — before
/// the sheet is on screen, not after — and the request is completed the
/// instant the files are durable, with no pause for a confirmation that no
/// longer exists.
///
/// The Objective-C name is pinned because `NSExtensionPrincipalClass` is
/// resolved through the Objective-C runtime: a Swift class would otherwise be
/// looked up under its mangled, module-qualified name, which changes if the
/// target is ever renamed.
@objc(DKShareViewController)
final class ShareViewController: NSViewController {
    /// Which Share-menu entry the user picked. Every extension bundle runs this
    /// same class and is told apart only by its own Info.plist.
    private let action = ShareAction.declared
    /// `loadView` is not promised to run once, and the import is not idempotent.
    private var hasStarted = false

    override func loadView() {
        // One transparent point. ShareKit demands a view controller; nothing
        // says the view has to be worth looking at.
        let view = NSView(frame: NSRect(x: 0, y: 0, width: 1, height: 1))
        view.alphaValue = 0
        self.view = view
        preferredContentSize = .zero
    }

    /// Not `viewDidAppear`: that is one presentation animation later, and the
    /// whole point is for the request to be finished before the sheet has
    /// settled.
    override func viewDidLoad() {
        super.viewDidLoad()
        guard !hasStarted, let context = extensionContext else { return }
        hasStarted = true
        Task { @MainActor in await Self.run(action: action, context: context) }
    }

    @MainActor
    private static func run(action: ShareAction, context: NSExtensionContext) async {
        let sources = flatten(inputItems: context.inputItems)

        // Resolved before the emptiness check so that even a share with nothing
        // in it has somewhere to leave its report.
        let inbox = try? Inbox.resolve()
        guard !sources.isEmpty else {
            fail(InboxError.emptyShare, action: action, inbox: inbox, context: context)
            return
        }
        guard let inbox else {
            fail(
                InboxError.containerUnavailable(identifier: AppGroup.identifier),
                action: action,
                inbox: nil,
                context: context
            )
            return
        }
        // Two different failures, two different sentences. These used to share
        // one `guard`, so a staging directory that could not be created — a full
        // disk, a permissions problem under `Inbox/Staging` — was reported as
        // 「无法访问共享容器」, pointing the only diagnostic channel this process
        // has at the entitlement, which is the one thing that demonstrably
        // worked.
        let staging: BatchStaging
        do {
            staging = try BatchStaging.create(in: inbox)
        } catch {
            fail(error, action: action, inbox: inbox, context: context)
            return
        }

        var manifestItems: [ManifestItem] = []
        if action == .collect {
            try? Data().write(to: staging.directory.appendingPathComponent("collection-import"))
            InboxSignal.post()
            launchContainingApp()
        }
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
            extensionVersion: bundleVersion,
            action: action,
            inputItemCount: (context.inputItems as? [NSExtensionItem])?.count ?? 0,
            succeeded: failure == nil,
            attachments: diagnostics
        )

        // A partially copied share is never published: the user asked for these
        // files together, and half of them arriving silently is the failure mode
        // the atomic batch exists to prevent.
        if let failure {
            staging.discard()
            DiagnosticsLog.record(record, in: inbox)
            fail(failure, action: action, inbox: inbox, context: context)
            return
        }

        let committed: URL
        do {
            committed = try staging.commit(
                manifest: BatchManifest(
                    batchID: staging.batchID,
                    createdAt: record.createdAt,
                    items: manifestItems,
                    // The manifest outlives the intent, and the app's history
                    // has to keep saying which entry the user picked long after
                    // the one-shot request has been consumed.
                    action: action
                ),
                diagnostics: record,
                // A forward is a request the app has to carry out once, and only
                // once. The clipboard is written below by this very process —
                // `ShareAction.needsIntent` is the whole of that rule.
                // 「发送到自定义」 carries no destination any more: this process
                // cannot ask which app without drawing a panel, so the app
                // decides — from one target, from a picker, or from a message
                // saying the list is empty. See `CustomForwardDecision`.
                intent: action.needsIntent ? BatchIntent(action: action, requestedAt: Date()) : nil,
                in: inbox
            )
        } catch {
            staging.discard()
            fail(error, action: action, inbox: inbox, context: context)
            return
        }

        // Order matters: the files are durable before anything is told about
        // them, so every step below is an optimisation that may fail freely.
        DiagnosticsLog.record(record, in: inbox)
        inbox.pruneStaging()

        // Written here as well as in the app, so ⌘V works immediately even if
        // the automated paste is refused or WeChatBridge never starts.
        let urls = manifestItems.map { committed.appendingPathComponent($0.relativePath) }
        if action != .collect { FilePasteboard.write(urls) }

        InboxSignal.post()
        // The clipboard entry is finished at this point and does not need the
        // app for anything; the others do.
        if action != .clipboard {
            launchContainingApp()
        }

        // Straight away, and this is the one line that matters most here: the
        // 700 ms sleep that used to sit above it existed only so the user could
        // read a confirmation. There is no confirmation, so the sheet closes as
        // soon as the files are durable.
        context.completeRequest(returningItems: nil, completionHandler: nil)
    }

    /// Both levels are arrays: one share can carry several `NSExtensionItem`s,
    /// each with several attachments. Indices preserve the host's order.
    static func flatten(inputItems: [Any]) -> [AttachmentSource] {
        var sources: [AttachmentSource] = []
        for (itemIndex, raw) in inputItems.enumerated() {
            guard let item = raw as? NSExtensionItem else { continue }
            for (attachmentIndex, provider) in (item.attachments ?? []).enumerated() {
                sources.append(
                    AttachmentSource(
                        provider: provider,
                        itemIndex: itemIndex,
                        attachmentIndex: attachmentIndex
                    )
                )
            }
        }
        return sources
    }

    /// The only way this process can report anything: leave the message in the
    /// app group and start the app, which has a screen to say it on.
    ///
    /// `cancelRequest` rather than `completeRequest` because nothing was
    /// delivered; the host is entitled to know its share did not happen, and
    /// the error carries the same sentence the app is about to show.
    @MainActor
    private static func fail(
        _ error: Error,
        action: ShareAction,
        inbox: Inbox?,
        context: NSExtensionContext
    ) {
        let message = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
        if let inbox {
            ShareFailure.record(
                ShareFailure(at: Date(), action: action, message: message),
                in: inbox
            )
            // A running app watches `Ready`, not `Failures`, so the ping is what
            // makes it look — otherwise the message would wait for the next
            // share to arrive.
            InboxSignal.post()
        }
        launchContainingApp()
        context.cancelRequest(
            withError: NSError(
                domain: "com.xiangming.wechatbridge.share",
                code: 1,
                userInfo: [NSLocalizedDescriptionKey: message]
            )
        )
    }

    /// Best effort, never a precondition.
    ///
    /// Apple does not promise that a share extension can start its containing
    /// app, so this runs after the batch is already committed: if it is refused,
    /// delayed, or the app is not installed where expected, the share is still
    /// complete and the app picks the batch up on its next launch.
    ///
    /// Dispatched and not awaited. `openApplication` hands the request to Launch
    /// Services and calls back when the app is *up*, which is a cold launch away
    /// — and the host holds its share-sheet container open for as long as the
    /// request is unfinished. Measured on the signed build (shots-r4/
    /// no-extension-window.log): waiting kept the host's windows on screen
    /// ~600 ms per share, of which the durable work is the first fraction.
    /// Returning immediately still launches the app — measured cold, with WeChatBridge
    /// not running — because the request is submitted before this returns, not
    /// when the callback fires.
    private static func launchContainingApp() {
        guard let appURL = containingAppURL else { return }

        let configuration = NSWorkspace.OpenConfiguration()
        // The user is still in WeChat. Taking focus here would be the single
        // most annoying thing this app could do — and for a forward, the app
        // WeChatBridge is about to activate is the one that should get focus, not
        // WeChatBridge itself. 「发送到自定义」 included: the picker it may open is a
        // floating panel that takes the keyboard without taking the app forward.
        configuration.activates = false
        configuration.addsToRecentItems = false
        // Tells a cold-launched app this is not a person opening it, so a
        // first run stays silent here and shows its guide when the user
        // actually comes to WeChatBridge.
        configuration.arguments = [LaunchArgument.background]

        NSWorkspace.shared.openApplication(at: appURL, configuration: configuration)
    }

    /// `.../WeChatBridge.app/Contents/PlugIns/<name>.appex` — three levels up. Nil when
    /// the appex is not nested in an app, which is what a bare build directory
    /// looks like.
    private static var containingAppURL: URL? {
        let url = Bundle.main.bundleURL
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
        return url.pathExtension == "app" ? url : nil
    }

    private static var bundleVersion: String? {
        let short = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String
        let build = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String
        guard let short else { return build }
        guard let build else { return short }
        return "\(short) (\(build))"
    }
}
