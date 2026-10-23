import Foundation

/// What became of a batch, as far as the user is concerned.
///
/// One record per batch, overwritten as the batch's story moves on: a forward
/// that failed is recorded as `failed`. `detail` carries the failure text so the
/// history can say *why* something did not arrive, which is the only part of a
/// failure the user can act on.
public struct BatchOutcome: Codable, Sendable, Hashable {
    public enum Kind: String, Codable, Sendable {
        /// Activated the target app and pressed ⌘V for the user.
        case delivered
        /// Written to the clipboard and nothing else.
        case copied
        /// A forward that did not happen. The files are on the clipboard.
        case failed
        /// The request was found too late to carry out — the Mac was asleep or
        /// WeChatBridge never started — so nothing was done with it at all.
        case expired

        /// The retired shelf outcome decodes as expired: those files were never
        /// delivered, and the old state file should not make the record unreadable.
        public init(from decoder: any Decoder) throws {
            let container = try decoder.singleValueContainer()
            let raw = try container.decode(String.self)
            if raw == "shelved" {
                self = .expired
                return
            }
            guard let kind = Kind(rawValue: raw) else {
                throw DecodingError.dataCorruptedError(
                    in: container,
                    debugDescription: "Unknown batch outcome: \(raw)"
                )
            }
            self = kind
        }

        public func encode(to encoder: any Encoder) throws {
            var container = encoder.singleValueContainer()
            try container.encode(rawValue)
        }
    }

    public let kind: Kind
    public let detail: String?
    public let at: Date

    public init(kind: Kind, detail: String? = nil, at: Date) {
        self.kind = kind
        self.detail = detail
        self.at = at
    }
}

/// The app's own notes about a batch: `Ready/<batch-id>/state.json`.
///
/// Kept beside the manifest instead of inside it because the two have different
/// writers. The extension owns the manifest and never reads this file; the app
/// owns this file and never rewrites the manifest except to drop an item. That
/// split is what lets the app record an outcome without racing an extension that
/// may be committing another batch at the same moment.
public struct BatchState: Codable, Sendable, Hashable {
    public static let fileName = "state.json"
    public static let currentSchemaVersion = 1

    public let schemaVersion: Int
    /// Nil only while a forward has been noticed but not yet carried out.
    public let outcome: BatchOutcome?
    /// Which Share-menu entry actually produced this batch.
    ///
    /// Recorded here as well as in the manifest because a manifest written
    /// before the `action` field existed may need `intent.json` to recover the
    /// request, and that file is consumed the instant the batch is first
    /// processed. Keeping the resolved action here preserves history.
    public let action: ShareAction?
    /// Which app a 「发送到自定义」 batch was pointed at, as it was named on
    /// screen at the time.
    ///
    /// Kept here because `intent.json` is consumed the instant the batch is
    /// first processed, and 记录 still has to read 「发给 Cursor」 a week later —
    /// 「发送到自定义」 on its own would say nothing about where the files went.
    /// Nil for every other entry, and nil in every state file written before
    /// this field existed.
    public let targetName: String?
    /// Snapshot of the group title recognised when the batch arrived. Kept in
    /// the app-owned state so 记录 does not have to re-OCR a window that may no
    /// longer exist.
    public let chatName: String?
    /// The scene's stable id and its name at the time of use. The name is a
    /// snapshot because renaming or deleting a scene later must not rewrite
    /// history.
    public let sceneID: String?
    public let sceneName: String?

    public init(
        outcome: BatchOutcome?,
        action: ShareAction? = nil,
        targetName: String? = nil,
        chatName: String? = nil,
        sceneID: String? = nil,
        sceneName: String? = nil,
        schemaVersion: Int = currentSchemaVersion
    ) {
        self.schemaVersion = schemaVersion
        self.outcome = outcome
        self.action = action
        self.targetName = targetName
        self.chatName = chatName
        self.sceneID = sceneID
        self.sceneName = sceneName
    }

    /// The state a batch gets the first time the app ever sees it.
    ///
    /// The outcome is stamped with the batch's own creation time rather than now,
    /// so a batch found after a week away is not backdated to this launch.
    /// `requestedAction` is what `intent.json` asked for, and it wins over the
    /// manifest's older default.
    public static func initial(
        for manifest: BatchManifest,
        requestedAction: ShareAction? = nil,
        targetName: String? = nil
    ) -> BatchState {
        let action = requestedAction ?? manifest.action
        switch action {
        case .clipboard:
            // Already done: the extension wrote the pasteboard before it exited,
            // and the app is not even launched for it. Left nil, the first
            // launch after a copy found "a request that never ran" and wrote
            // 未执行 over a copy that had worked.
            return BatchState(
                outcome: BatchOutcome(kind: .copied, at: manifest.createdAt),
                action: action,
                targetName: targetName
            )
        case .collect, .codex, .claude, .doubao, .qwen, .workBuddy, .weSight, .deepSeekHarness, .obsidian, .folder, .custom:
            return BatchState(outcome: nil, action: action, targetName: targetName)
        }
    }

    /// `targetName` defaults to "leave it alone": most outcomes are recorded by
    /// code that has no opinion about the destination, and passing nil there
    /// must not erase the name a custom forward already wrote.
    public func withOutcome(_ outcome: BatchOutcome?, targetName: String? = nil) -> BatchState {
        BatchState(
            outcome: outcome,
            action: action,
            targetName: targetName ?? self.targetName,
            chatName: chatName,
            sceneID: sceneID,
            sceneName: sceneName,
            schemaVersion: schemaVersion
        )
    }

    /// Records scene/group metadata without clearing anything a previous step
    /// already wrote. Nil means "leave the existing value alone", matching the
    /// optional-field convention used by `targetName`.
    public func withContext(
        chatName: String? = nil,
        sceneID: String? = nil,
        sceneName: String? = nil
    ) -> BatchState {
        BatchState(
            outcome: outcome,
            action: action,
            targetName: targetName,
            chatName: chatName ?? self.chatName,
            sceneID: sceneID ?? self.sceneID,
            sceneName: sceneName ?? self.sceneName,
            schemaVersion: schemaVersion
        )
    }

    private enum CodingKeys: String, CodingKey {
        case schemaVersion
        case outcome
        case action
        case targetName
        case chatName
        case sceneID
        case sceneName
    }

    public init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        schemaVersion = try container.decode(Int.self, forKey: .schemaVersion)
        outcome = try container.decodeIfPresent(BatchOutcome.self, forKey: .outcome)
        action = try container.decodeIfPresent(ShareAction.self, forKey: .action)
        targetName = try container.decodeIfPresent(String.self, forKey: .targetName)
        chatName = try container.decodeIfPresent(String.self, forKey: .chatName)
        sceneID = try container.decodeIfPresent(String.self, forKey: .sceneID)
        sceneName = try container.decodeIfPresent(String.self, forKey: .sceneName)
    }
}
