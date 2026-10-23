import AppKit
import WeChatBridgeCore
import Foundation

/// Carries out what the user picked in the Share menu.
///
/// The extension never does this itself: pasting into another app needs the
/// Accessibility permission, which an app extension can never hold, and the
/// extension is killed seconds after it finishes. So the extension commits the
/// files and records the request; this runs it, in the app, where a failure has
/// somewhere to be reported.
///
/// Success is silent. Every paste destination ends with the result in front of
/// the user — the share sheet's own 「已复制」 or the pasted files in the target
/// app — so the only thing left to report is a failure. The knowledge
/// destinations are the exception: nothing appears on screen when a note lands
/// in a folder or a vault, so each gets the one success report — a system
/// notification carrying the way in, which shows even while this app sits in
/// the background where a self-drawn capsule cannot (see `DeliveryNotifier`).
/// 「沉淀到 Obsidian」 used to open the note itself instead — claiming the
/// foreground on every delivery and racing Obsidian's indexing of the
/// brand-new file — and now behaves like its sibling: quiet, one tap away.
@MainActor
final class ActionRunner {
    /// Opens 设置 → 入口, which is the only place the 「发送到自定义」 list can be
    /// filled in. Offered as the one action on the toast a share raises when
    /// that list is empty.
    var openEntries: (() -> Void)?
    var openSkills: (() -> Void)?

    private let model: AppModel
    private let authorization: AccessibilityAuthorization
    /// The user's own destinations, read when 「发送到自定义」 arrives without one
    /// — which is every share, now that the extension no longer asks.
    private let targets: ForwardTargets
    /// For scene selection and the Obsidian destination.
    private let preferences: Preferences
    private let sceneCoordinator: SceneCoordinator
    private let skills: SkillLibrary
    private let toast = ToastPresenter()
    private let notifier: DeliveryNotifier
    private let picker = TargetPickerPanel()
    /// The forward currently in flight, so the next one waits for it.
    ///
    /// `forward` writes the clipboard and then suspends for seconds — launching
    /// the target, waiting up to 4 s for it to come frontmost, settling before
    /// ⌘V — and every one of those awaits hands the MainActor back. Two
    /// arrivals in one `reload()` is the ordinary case whenever the app was not
    /// running when the shares were made, so two unserialised forwards
    /// interleave: the second one's `FilePasteboard.write` lands while the
    /// first is still waiting to paste, and the first then pastes the second's
    /// files while both batches record 已送达. Chaining means the pasteboard is
    /// only ever written for the forward that is about to press ⌘V.
    private var pending: Task<Void, Never>?

    init(
        model: AppModel,
        authorization: AccessibilityAuthorization,
        targets: ForwardTargets,
        preferences: Preferences,
        sceneCoordinator: SceneCoordinator,
        skills: SkillLibrary,
        notifier: DeliveryNotifier? = nil
    ) {
        self.model = model
        self.authorization = authorization
        self.targets = targets
        self.preferences = preferences
        self.sceneCoordinator = sceneCoordinator
        self.skills = skills
        self.notifier = notifier ?? DeliveryNotifier()
    }

    func enqueueExclusive(_ operation: @escaping @MainActor () async -> Void) {
        let previous = pending
        pending = Task { await previous?.value; await operation() }
    }

    func configureNotifications() {
        notifier.configure()
    }

    func handle(_ arrival: ArrivedBatch) {
        switch arrival.action {
        case .collect:
            return
        case .clipboard:
            // Reached from 记录's own 复制到剪贴板 and from an intent an older
            // extension build wrote. A share made with this build never gets
            // here: the extension copies it itself.
            enqueueExclusive { [weak self] in
                FilePasteboard.write(arrival.urls)
                self?.model.recordDelivery(urls: arrival.urls, action: .clipboard)
            }
            // Nothing is shown: the share sheet said 「已复制到剪贴板」 a moment
            // ago and is still on screen. A second capsule saying it again is
            // WeChatBridge talking over the system.
        case .codex, .claude, .doubao, .qwen, .workBuddy, .weSight, .deepSeekHarness, .obsidian, .folder, .custom:
            // Shares and WeChat captures share the same clipboard queue.
            //
            // Read here rather than where the panel opens. This forward may wait
            // behind another one; once it reaches the panel, the pointer is
            // wherever the user last left it. This is still the moment their
            // gesture is closest to the request.
            let pointer = NSEvent.mouseLocation
            let previous = pending
            pending = Task { [weak self] in
                await previous?.value
                guard let self else { return }
                // Checked again here, not only when the intent came off disk.
                // A forward can wait in this queue for as long as the one in
                // front of it takes — an unanswered picker, a target app that
                // never comes frontmost — and `BatchIntent.freshnessWindow`
                // exists precisely so that a request the user has stopped
                // thinking about does not suddenly paste into whatever they
                // have open now. The files are on the clipboard either way.
                guard arrival.isFresh else {
                    self.model.recordExpired(urls: arrival.urls)
                    return
                }
                // The knowledge destinations paste nothing, so there is no
                // prompt to attach and no scene to pick.
                let context = await self.sceneCoordinator.prepare(
                    enabled: arrival.action != .obsidian && arrival.action != .folder
                        && !self.preferences.scenes.enabledScenes.isEmpty,
                    groupName: nil,
                    captureTitle: arrival.capturesGroupName,
                    urls: arrival.urls,
                    pointer: pointer,
                    allowDefault: false
                )
                switch context {
                case .expired:
                    self.model.recordExpired(urls: arrival.urls)
                case .ready(let context):
                    await self.deliver(arrival, askingNear: pointer, context: context)
                }
            }
        }
    }

    /// A collection already chose its scene and target in one panel. Reuse the
    /// paste queue, without inspecting WeChat or presenting another picker.
    func deliverCollection(_ arrival: ArrivedBatch, scene: WeChatScene?, completion: @escaping (Bool, String?) -> Void) {
        enqueueExclusive { [weak self] in
            guard let self else { return }
            // Batch outcomes use ISO8601 seconds on disk. Match that precision
            // when checking a fast delivery that completes in the same second.
            let started = Date(timeIntervalSince1970: floor(Date().timeIntervalSince1970))
            guard arrival.isFresh else {
                self.model.recordExpired(urls: arrival.urls)
                completion(false, L10n.text("等待交付超时，原始文件已保留。请重新选择目标。"))
                return
            }
            guard let conversations = self.model.collectionConversations(for: arrival.urls) else {
                completion(false, L10n.text("请先补充每批的群名或聊天人。"))
                return
            }
            var context = SceneCoordinator.Selection(
                scenes: scene.map { [$0] } ?? [],
                groupName: conversations.count == 1 ? conversations[0].name : nil
            )
            if scene != nil {
                context.insights = await Task.detached(priority: .utility) {
                    (try? WeChatBatchInsightsReader.read(urls: arrival.urls)) ?? WeChatBatchInsights()
                }.value
            }
            if arrival.action == .obsidian || arrival.action == .folder {
                for conversation in conversations {
                    var namedContext = context
                    namedContext.groupName = conversation.name
                    let namedArrival = ArrivedBatch(
                        action: arrival.action,
                        target: arrival.target,
                        urls: conversation.urls,
                        requestedAt: arrival.requestedAt
                    )
                    await self.deliver(namedArrival, askingNear: NSEvent.mouseLocation, context: namedContext)
                }
            } else {
                await self.deliver(arrival, askingNear: NSEvent.mouseLocation, context: context)
            }
            let outcomes = self.model.batchIDs(for: arrival.urls).compactMap { self.model.batch(id: $0)?.outcome }
            let success = !outcomes.isEmpty && outcomes.count == self.model.batchIDs(for: arrival.urls).count
                && outcomes.allSatisfy { $0.kind == .delivered && $0.at >= started }
            completion(success, outcomes.first(where: { $0.kind == .failed || $0.kind == .expired })?.detail)
        }
    }

    /// One step before `forward`: 「发送到自定义」 arrives without a destination
    /// and has to be given one.
    ///
    /// Every other entry — and every 发给 ▸ menu inside WeChatBridge, which names the
    /// app in the row the user clicked — arrives knowing where it is going and
    /// goes straight through.
    /// `askingNear` is where the pointer was when the share arrived — see
    /// `handle`, which reads it before this forward can be delayed by another.
    private func deliver(
        _ arrival: ArrivedBatch,
        askingNear pointer: NSPoint,
        context: SceneCoordinator.Selection
    ) async {
        model.recordContext(
            chatName: context.groupName,
            sceneID: context.scene?.id,
            sceneName: context.scene?.name,
            urls: arrival.urls
        )
        if arrival.action == .obsidian {
            await deliverToObsidian(arrival, context: context)
            return
        }
        if arrival.action == .folder {
            await deliverToFolder(arrival, context: context)
            return
        }
        guard arrival.action == .custom, arrival.target == nil else {
            await forward(arrival, context: context)
            return
        }

        switch CustomForwardDecision.decide(targets: targets.orderedTargets) {
        case .none:
            // Nothing to send to and nothing to ask. The share is not lost —
            // the files are on the clipboard — so this is a message with a way
            // out of it, not an error.
            fallBack(
                arrival,
                message: L10n.text("还没有添加自定义应用。"),
                action: ToastPresenter.Action(title: L10n.text("添加应用")) { [weak self] in
                    self?.openEntries?()
                }
            )
        case .single(let target):
            // One app is not a choice. The user's requirement, and the reason
            // no panel appears here.
            await forward(arrival, to: target, context: context)
        case .choose(let list):
            // Written before the panel opens rather than after a pick, so that
            // cancelling still leaves the user one ⌘V from their files.
            FilePasteboard.write(arrival.urls)
            // Where the user was pointing when this arrived. The panel answers
            // that gesture and stays put afterwards, so the pointer is read once,
            // in `handle`, and never again while the panel is open. Parking it in
            // a fixed corner put it on the menu-bar screen — the wrong display
            // for anyone whose WeChat is on the other one.
            let answer = await picker.choose(from: list) { size in
                FloatingCapsule.near(pointer, size: size)
            }
            switch answer {
            case .picked(let target):
                await forward(arrival, to: target, context: context)
            case .cancelled:
                // Cancelling is an answer, not a fault: no toast — the user
                // just dismissed a panel and knows they did — but the history
                // has to say why nothing was delivered.
                model.recordFailure(L10n.text("已取消"), urls: arrival.urls)
            case .expired:
                // Nobody answered for a minute and a half. Same record as an
                // intent that outlived its window, because that is what it is:
                // 未执行, files on the clipboard, nothing pasted anywhere.
                model.recordExpired(urls: arrival.urls)
            }
        }
    }

    /// Remembers the pick, then forwards. The next 「发送到自定义」 share opens
    /// the panel on this app, and Return takes it.
    private func forward(
        _ arrival: ArrivedBatch,
        to target: ForwardTarget,
        context: SceneCoordinator.Selection
    ) async {
        targets.recordUse(target)
        await forward(
            ArrivedBatch(
                action: arrival.action,
                target: target,
                urls: arrival.urls,
                requestedAt: arrival.requestedAt,
                capturesGroupName: false
            ),
            context: context
        )
    }

    /// Obsidian is not a paste destination: it needs the archive turned into a
    /// note and copied into the user's vault before the record can say
    /// delivered. The note does not open itself: a delivery never yanks the
    /// user out of whatever they were doing, and the notification's
    /// 「打开笔记」 is the way in — see `DeliveryNotifier` for why the open
    /// could not stay here (Issue #17).
    private func deliverToObsidian(
        _ arrival: ArrivedBatch,
        context: SceneCoordinator.Selection
    ) async {
        guard let vaultPath = preferences.obsidianVaultPath, !vaultPath.isEmpty else {
            fallBack(
                arrival,
                message: KnowledgeDelivery.Failure.notConfigured.localizedDescription,
                action: ToastPresenter.Action(title: L10n.text("设置知识库")) { [weak self] in
                    self?.openEntries?()
                }
            )
            return
        }

        do {
            let notes = try KnowledgeDelivery.deliver(
                urls: arrival.urls,
                vaultPath: vaultPath,
                subfolder: preferences.obsidianSubfolder,
                chatName: context.groupName,
                sceneName: context.scene?.name
            )
            model.recordDelivery(urls: arrival.urls, action: .obsidian)
            sceneCoordinator.advance(context)
            await notifier.notify(savedToVault: vaultPath, notes: notes)
        } catch {
            fallBack(
                arrival,
                message: (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
            )
        }
    }

    /// 「沉淀到文件夹」 is not a paste destination either: the archive becomes a
    /// note in the folder the user picked. Unlike every other destination,
    /// nothing appears on screen when it works, so this is the one delivery
    /// that reports success — a notification with a way to the file (Issue
    /// #19). A notification rather than a capsule because the share extension
    /// puts this app in the background for every delivery, where a self-drawn
    /// window never appears.
    private func deliverToFolder(
        _ arrival: ArrivedBatch,
        context: SceneCoordinator.Selection
    ) async {
        do {
            let notes = try KnowledgeDelivery.deliver(
                urls: arrival.urls,
                folderPath: preferences.folderDeliveryPath,
                subfolder: preferences.folderDeliverySubfolder,
                chatName: context.groupName,
                sceneName: context.scene?.name
            )
            model.recordDelivery(urls: arrival.urls, action: .folder)
            sceneCoordinator.advance(context)
            let folder = URL(
                fileURLWithPath: preferences.folderDeliveryPath,
                isDirectory: true
            )
            await notifier.notify(savedTo: folder.lastPathComponent, revealing: notes)
        } catch {
            fallBack(
                arrival,
                message: (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
            )
        }
    }

    /// A share the extension could not complete. It has no interface of its own
    /// any more, so the message travels through the app group and is said here —
    /// with no action, because there is nothing on disk left to act on.
    func report(_ failure: ShareFailure) {
        toast.show(
            L10n.format("没能接住这次转发：%@", failure.message),
            symbol: "exclamationmark.triangle.fill",
            tone: .warning
        )
    }

    /// A failure from something WeChatBridge is doing that has no window of its own to
    /// report into.
    func notify(_ message: String, action: ToastPresenter.Action? = nil) {
        toast.show(message, symbol: "exclamationmark.triangle.fill", tone: .warning, action: action)
    }

    /// Takes down whatever is in the corner before something else claims it.
    ///
    /// The retry flow is the reason: a failure capsule stands for 8 s with 打开设置
    /// on it, and 再次执行 clicked while it is up puts the HUD in the same corner,
    /// over that button, while the capsule keeps counting down underneath.
    func dismissNotice() { toast.dismiss() }

    /// One path for every destination.
    ///
    /// The fixed entries name their app in `ShareAction`; 「发送到自定义」
    /// arrives here with the app `deliver` resolved, or the one a 发给 ▸ menu
    /// named. Beyond resolving which of the two it is, nothing here knows the
    /// difference — a forward is a bundle identifier, a display name and a ⌘V.
    private func forward(_ arrival: ArrivedBatch, context: SceneCoordinator.Selection) async {
        let action = arrival.action
        let target = arrival.target
        let name = target?.displayName ?? action.targetDisplayName

        // A custom forward with no target should be impossible — `deliver`
        // resolves one or stops — so reaching here means a build mismatch about
        // the intent schema. The files are still on the clipboard, and saying so
        // beats pretending an app is missing.
        guard let bundleIdentifier = target?.bundleIdentifier ?? action.targetBundleIdentifier else {
            fallBack(arrival, message: L10n.text("这条转发没有指定目标 App。"))
            return
        }
        guard let applicationURL = AutoPaste.applicationURL(forBundleIdentifier: bundleIdentifier) else {
            fallBack(arrival, message: AutoPaste.Failure.notInstalled(name: name).localizedDescription)
            return
        }

        // The files, or their paths as text for an app — a terminal — that
        // cannot take a pasted file. The list is asked, not the arrival: the
        // checkbox in 设置 owns this, and an intent stamped by an older
        // extension names the app without it.
        let configuredPathOnly = targets.pastesPathOnly(for: bundleIdentifier) ?? target?.pastesPathOnly ?? false
        // Doubao can read local paths in its agent mode. For a chat archive
        // this avoids the upload picker entirely: give it the original ZIP
        // path, and let its own tools unpack and inspect the file.
        let doubaoReadsLocalArchive = bundleIdentifier == ShareAction.doubao.targetBundleIdentifier
            && arrival.urls.contains { $0.pathExtension.lowercased() == "zip" }
        let pathOnly = configuredPathOnly || doubaoReadsLocalArchive
        let promptURLs = doubaoReadsLocalArchive ? doubaoPathAliases(for: arrival.urls) : arrival.urls
        let agent = AgentID.matching(bundleIdentifier: bundleIdentifier)
        let scenePrompt: String? = context.scene.flatMap { scene -> String? in
            if let agent, !scene.compatibleAgents.contains(agent) {
                return nil
            }
            return ScenePrompt.render(
                scene: scene,
                previousSummaryAt: context.previousSummaryAt,
                currentEnd: context.insights.end,
                skills: scene.effectiveSkillIDs.isEmpty
                    ? nil
                    : skills.promptContext(agent: agent)
            )
        }
        let prompt = doubaoReadsLocalArchive
            ? [
                L10n.text("请直接读取并解压以下本机文件，使用其中的内容完成场景要求。"),
                scenePrompt,
            ].compactMap { $0 }.joined(separator: "\n\n")
            : scenePrompt
        // The prompt, if 入口 attaches one: a first ⌘V before the files, or
        // folded into the one line a terminal gets. See `PastePlan`.
        let plan = PastePlan.make(
            urls: promptURLs,
            pathOnly: pathOnly,
            prompt: prompt
        )
        // Written again here, not just in the extension: this is the process
        // that is about to press ⌘V, so it owns what ⌘V will produce.
        writePasteboard(plan)

        authorization.refresh()
        guard authorization.isTrusted else {
            // Nothing was lost: the files are on the clipboard, so the user is
            // one ⌘V away while they decide about the permission.
            fallBack(
                arrival,
                message: pathOnly
                    ? L10n.format("路径已在剪贴板，去 %@ 按 ⌘V 就行。", name)
                    : L10n.format("文件已在剪贴板，去 %@ 按 ⌘V 就行。", name),
                action: ToastPresenter.Action(title: L10n.text("开启自动粘贴")) { [weak authorization] in
                    authorization?.guideIfNeeded()
                },
                plan: plan
            )
            return
        }

        do {
            if action == .doubao, !pathOnly {
                // Doubao accepts pasted text but ignores file URLs on the
                // pasteboard. Paste the prompt, then use its own upload picker.
                let promptOnly = plan.filter {
                    if case .text = $0 { return true }
                    return false
                }
                try await AutoPaste.activateAndPaste(
                    applicationAt: applicationURL,
                    bundleIdentifier: bundleIdentifier,
                    displayName: name,
                    plan: promptOnly
                )
                try await DoubaoAttachment.attach(urls: arrival.urls)
            } else {
                try await AutoPaste.activateAndPaste(
                    applicationAt: applicationURL,
                    bundleIdentifier: bundleIdentifier,
                    displayName: name,
                    plan: plan
                )
            }
            model.recordDelivery(
                urls: arrival.urls,
                action: action,
                targetName: target?.displayName
            )
            sceneCoordinator.advance(context)
            let missing = missingSkills(for: context.scene, bundleIdentifier: bundleIdentifier)
            if !missing.isEmpty {
                let names = missing.joined(separator: "、")
                toast.show(
                    L10n.format("场景「%@」引用的技能不可用：%@", context.scene?.name ?? "", names),
                    symbol: "puzzlepiece.extension.fill",
                    tone: .warning,
                    action: ToastPresenter.Action(title: L10n.text("查看技能")) { [weak self] in
                        self?.openSkills?()
                    }
                )
            }
            // No confirmation: the user is looking at the target app with their
            // files already pasted into it. Telling them it worked is a capsule
            // over the evidence — removed at the user's request, 2026-09-05.
        } catch {
            fallBack(
                arrival,
                message: (error as? LocalizedError)?.errorDescription ?? error.localizedDescription,
                plan: plan
            )
        }
    }

    /// Display names of the skills a delivered scene references that have no
    /// usable copy in the app-owned skill library.
    private func missingSkills(
        for scene: WeChatScene?,
        bundleIdentifier: String
    ) -> [String] {
        guard let scene, let agent = AgentID.matching(bundleIdentifier: bundleIdentifier),
              !scene.effectiveSkillIDs.isEmpty
        else {
            return []
        }
        return scene.effectiveSkillIDs.compactMap { id in
            let resolved = skills.resolve(id, agent: agent)
            return resolved.mode == .missing ? resolved.displayName : nil
        }
    }

    /// What a manual ⌘V should produce: the files, or the one line a
    /// terminal would have received — never the bare prompt.
    private func writePasteboard(_ plan: [PastePayload]) {
        if let payload = PastePlan.manualPayload(plan) {
            FilePasteboard.write(payload)
        }
    }

    /// The inbox path is deliberately collision-safe and therefore long.
    /// Doubao only needs a stable path it can read, so expose a short symlink
    /// instead. `ponytail:` /tmp aliases rely on the OS cleanup at reboot; add
    /// durable links if users need them across restarts.
    private func doubaoPathAliases(for urls: [URL]) -> [URL] {
        let fileManager = FileManager.default
        let root = URL(fileURLWithPath: "/tmp/wxbridge", isDirectory: true)
        try? fileManager.createDirectory(at: root, withIntermediateDirectories: true)

        return urls.map { url in
            guard url.pathExtension.lowercased() == "zip" else { return url }
            let components = url.pathComponents
            let readyIndex = components.lastIndex(of: "Ready")
            let batchID = readyIndex.flatMap { index in
                components.indices.contains(index + 1) ? components[index + 1] : nil
            } ?? UUID().uuidString
            let shortID = String(batchID.replacingOccurrences(of: "-", with: "").prefix(8))
            let alias = root.appendingPathComponent("\(shortID)-\(url.lastPathComponent)")

            if (try? fileManager.destinationOfSymbolicLink(atPath: alias.path)) != nil {
                try? fileManager.removeItem(at: alias)
            }
            do {
                try fileManager.createSymbolicLink(at: alias, withDestinationURL: url)
                return alias
            } catch {
                return url
            }
        }
    }

    /// Every failure lands here, and every failure ends the same way: the files
    /// are on the clipboard and the history says what went wrong.
    private func fallBack(
        _ arrival: ArrivedBatch,
        message: String,
        action: ToastPresenter.Action? = nil,
        plan: [PastePayload]? = nil
    ) {
        // Whatever the forward was about to paste stays pasteable: a terminal
        // that was going to get a path still gets a path from a manual ⌘V.
        writePasteboard(plan ?? [.files(arrival.urls)])
        // The chosen app is named even when the forward failed. A batch that
        // arrived through 「发送到自定义」 carries it in its intent already, but
        // one sent from 记录's 发给 ▸ has nothing else that remembers who it was
        // aimed at, and 「未送达」 with no destination is half a record.
        model.recordFailure(message, urls: arrival.urls, targetName: arrival.target?.displayName)
        toast.show(message, symbol: "exclamationmark.triangle.fill", tone: .warning, action: action)
    }
}
