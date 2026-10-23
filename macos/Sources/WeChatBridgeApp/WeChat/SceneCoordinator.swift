import AppKit
import WeChatBridgeCore
import Foundation

/// Resolves and remembers scene choices for incoming share batches.
@MainActor
final class SceneCoordinator {
    struct Selection: Sendable {
        var scenes: [WeChatScene]
        var groupName: String?
        var previousSummaryAt: Date?
        var insights = WeChatBatchInsights()

        init(
            scenes: [WeChatScene] = [],
            groupName: String? = nil,
            previousSummaryAt: Date? = nil,
            insights: WeChatBatchInsights = WeChatBatchInsights()
        ) {
            self.scenes = scenes
            self.groupName = groupName
            self.previousSummaryAt = previousSummaryAt
            self.insights = insights
        }

        var scene: WeChatScene? { scenes.first }
    }

    enum Answer: Sendable {
        case ready(Selection)
        case expired
    }

    private let preferences: Preferences
    private let picker = ScenePickerPanel()
    private let worker = DispatchQueue(
        label: "dev.wechatflow.scene-reader",
        qos: .userInitiated,
        attributes: .concurrent
    )

    init(preferences: Preferences) {
        self.preferences = preferences
    }

    func prepare(
        enabled: Bool,
        groupName: String?,
        captureTitle: Bool,
        urls: [URL],
        pointer: NSPoint,
        allowDefault: Bool = true
    ) async -> Answer {
        var selection = Selection()
        selection.groupName = groupName
        guard enabled || captureTitle else { return .ready(selection) }

        let insightsTask = enabled ? Task {
            await withCheckedContinuation { continuation in
                worker.async {
                    continuation.resume(
                        returning: (try? WeChatBatchInsightsReader.read(urls: urls)) ?? WeChatBatchInsights()
                    )
                }
            }
        } : nil

        let title: GroupTitleParser.Title?
        if captureTitle {
            title = await withCheckedContinuation { continuation in
                worker.async {
                    continuation.resume(returning: try? WeChatTitleReader.read())
                }
            }
        } else {
            title = nil
        }
        selection.groupName = title?.name ?? selection.groupName
        guard enabled else { return .ready(selection) }

        selection.insights = await insightsTask?.value ?? WeChatBatchInsights()

        if let sceneID = preferences.consumePendingSceneID(),
           let scene = preferences.scenes.scene(id: sceneID) {
            selection.scenes = [scene]
            if let groupName = selection.groupName {
                selection.previousSummaryAt = preferences.groupMemory[GroupName.normalize(groupName)]?.lastSummaryAt
            }
            return .ready(selection)
        }

        var resolution = SceneResolver.resolve(
            groupName: selection.groupName,
            settings: preferences.scenes,
            memories: preferences.groupMemory,
            senders: selection.insights.senders,
            allowDefault: false
        )
        if resolution.source != .binding && resolution.source != .fingerprint {
            resolution = SceneResolution(
                scenes: [],
                groupName: resolution.groupName,
                usedFingerprint: resolution.usedFingerprint,
                source: .none
            )
        }
        selection.groupName = resolution.groupName ?? selection.groupName
        let hasBinding = selection.groupName
            .map(GroupName.normalize)
            .flatMap { preferences.groupMemory[$0] }
            .map { !preferences.scenes.scenes(ids: $0.boundSceneIDs).isEmpty } ?? false

        if resolution.scenes.count > 1,
           selection.groupName != nil {
            let answer = await picker.choose(from: resolution.scenes) { size in
                FloatingCapsule.near(pointer, size: size)
            }
            switch answer {
            case .picked(let scene):
                resolution = SceneResolution(
                    scenes: [scene],
                    groupName: selection.groupName,
                    usedFingerprint: resolution.usedFingerprint,
                    source: resolution.source
                )
            case .cancelled:
                resolution = SceneResolution(
                    scenes: [],
                    groupName: selection.groupName,
                    usedFingerprint: resolution.usedFingerprint,
                    source: .none
                )
            case .expired:
                return .expired
            }
        }

        if resolution.scenes.isEmpty,
           !hasBinding,
           selection.groupName != nil,
           !preferences.scenes.enabledScenes.isEmpty {
            let answer = await picker.choose(from: preferences.scenes.enabledScenes) { size in
                FloatingCapsule.near(pointer, size: size)
            }
            switch answer {
            case .picked(let scene):
                resolution = SceneResolution(
                    scenes: [scene],
                    groupName: selection.groupName,
                    usedFingerprint: false,
                    source: .binding
                )
                rememberBinding(scenes: [scene], groupName: selection.groupName)
            case .cancelled:
                break
            case .expired:
                return .expired
            }
        }

        if resolution.scenes.isEmpty, allowDefault, let fallback = preferences.scenes.defaultScene() {
            resolution = SceneResolution(
                scenes: [fallback],
                groupName: selection.groupName,
                usedFingerprint: false,
                source: .defaultScene
            )
        }

        selection.scenes = resolution.scenes
        if let groupName = selection.groupName {
            selection.previousSummaryAt = preferences.groupMemory[GroupName.normalize(groupName)]?.lastSummaryAt
        }
        return .ready(selection)
    }

    func advance(_ selection: Selection) {
        guard let groupName = selection.groupName else { return }
        let key = GroupName.normalize(groupName)
        guard !key.isEmpty else { return }
        if let scene = selection.scenes.first, let end = selection.insights.end {
            preferences.groupMemory[key] = GroupMemory.advancing(
                preferences.groupMemory[key],
                displayName: groupName,
                sceneID: scene.id,
                senders: selection.insights.senders,
                end: end
            )
        } else {
            var memory = preferences.groupMemory[key] ?? GroupMemory(displayName: groupName)
            memory.senders.formUnion(selection.insights.senders)
            memory.updatedAt = Date()
            preferences.groupMemory[key] = memory
        }
    }

    private func rememberBinding(scenes: [WeChatScene], groupName: String?) {
        guard let groupName else { return }
        let key = GroupName.normalize(groupName)
        guard !key.isEmpty else { return }
        var memory = preferences.groupMemory[key] ?? GroupMemory(displayName: groupName)
        memory.boundSceneIDs = scenes.map(\.id)
        memory.updatedAt = Date()
        preferences.groupMemory[key] = memory
    }
}
