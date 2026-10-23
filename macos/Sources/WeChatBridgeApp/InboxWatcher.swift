import AppKit
import WeChatBridgeCore
import Foundation

/// Notices that the extension published a batch.
///
/// Three independent triggers, because none of them is reliable alone:
/// the distributed notification is fast but is dropped if the app was not
/// running; the directory source catches a batch committed while the app was
/// busy or the notification was lost; and the wake/activate rescans cover a Mac
/// that slept through the whole thing. All three funnel into one debounced
/// rescan, and the directory is always the answer.
@MainActor
final class InboxWatcher {
    private let inbox: Inbox
    private let onChange: () -> Void
    private var source: DispatchSourceFileSystemObject?
    private var descriptor: CInt = -1
    private var debounce: Task<Void, Never>?
    private var observers: [NSObjectProtocol] = []

    init(inbox: Inbox, onChange: @escaping () -> Void) {
        self.inbox = inbox
        self.onChange = onChange
    }

    deinit {
        source?.cancel()
    }

    func start() {
        DistributedNotificationCenter.default().addObserver(
            forName: InboxSignal.didChange,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.schedule() }
        }

        for name in [
            NSApplication.didBecomeActiveNotification,
            NSWorkspace.didWakeNotification,
        ] {
            let center = name == NSWorkspace.didWakeNotification
                ? NSWorkspace.shared.notificationCenter
                : NotificationCenter.default
            observers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.schedule() }
            })
        }

        startDirectorySource()
        schedule()
    }

    /// The watched descriptor is the `Ready` directory itself: committing a
    /// batch is a rename *into* it, which shows up as a write on the directory
    /// rather than on any file inside.
    private func startDirectorySource() {
        try? inbox.prepareDirectories()
        let descriptor = open(inbox.ready.path, O_EVTONLY)
        guard descriptor >= 0 else { return }
        self.descriptor = descriptor

        let source = DispatchSource.makeFileSystemObjectSource(
            fileDescriptor: descriptor,
            eventMask: [.write, .rename, .delete],
            queue: .main
        )
        source.setEventHandler { [weak self] in
            guard let self else { return }
            MainActor.assumeIsolated {
                // A deleted or renamed Ready directory (a user tidying the
                // container by hand) invalidates the descriptor; rebuild rather
                // than watching a vanished inode forever.
                let data = source.data
                if data.contains(.delete) || data.contains(.rename) {
                    self.restartDirectorySource()
                }
                self.schedule()
            }
        }
        source.setCancelHandler { [descriptor] in
            close(descriptor)
        }
        source.resume()
        self.source = source
    }

    private func restartDirectorySource() {
        source?.cancel()
        source = nil
        descriptor = -1
        startDirectorySource()
    }

    /// The extension writes a manifest and renames a directory; several events
    /// can arrive for one share. Coalescing keeps that to a single rescan.
    private func schedule() {
        debounce?.cancel()
        debounce = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 120_000_000)
            guard !Task.isCancelled else { return }
            self?.onChange()
        }
    }
}
