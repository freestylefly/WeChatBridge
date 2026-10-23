import AppKit
import SwiftUI
import UniformTypeIdentifiers
import WeChatBridgeCore

struct SkillsPane: View {
    @ObservedObject var skills: SkillLibrary
    @ObservedObject var preferences: Preferences
    @ObservedObject var router: SettingsRouter

    @State private var query = ""
    @State private var expandedSkillIDs: Set<String> = []
    @State private var notice: SkillNotice?
    @State private var removeRequest: RemoveSkillRequest?
    @State private var importing = false

    var body: some View {
        let records = makeRecords()
        let visibleRecords = scopedRecords(records)

        VStack(alignment: .leading, spacing: Space.l) {
            Text(L10n.text("发现、安装和管理各 Agent 可用的技能；场景用 {{skill:id}} 引用技能库中的副本。"))
                .font(Typo.paneBody)
                .foregroundStyle(Theme.inkSecondary)

            metrics(records)
            controls

            if let error = skills.loadError {
                Notice(error, tone: .warn)
            }

            if let issue = skills.libraryIssue {
                Notice(issue, tone: .warn)
            }

            if visibleRecords.isEmpty {
                emptyState
            } else {
                VStack(spacing: Space.s) {
                    ForEach(visibleRecords) { record in
                        SkillCard(
                            record: record,
                            sceneCount: sceneCount(for: record.skill),
                            showScenes: { showScenes(referencing: record.skill.id) },
                            expanded: expandedSkillIDs.contains(record.id),
                            toggleDetails: { toggleDetails(record.id) },
                            removeSkill: {
                                removeRequest = RemoveSkillRequest(
                                    skill: record.skill,
                                    sceneCount: sceneCount(for: record.skill)
                                )
                            }
                        )
                    }
                }
            }

            if let notice {
                Notice(notice.message, tone: notice.tone)
            }
        }
        .frame(maxWidth: 980, alignment: .leading)
        .fileImporter(
            isPresented: $importing,
            allowedContentTypes: [.zip]
        ) { result in
            switch result {
            case .success(let url):
                importArchive(url)
            case .failure(let error):
                notice = SkillNotice(error.localizedDescription, tone: .bad)
            }
        }
        // Windows RemoveSkill_Click: warn before the copy moves to backups,
        // naming the scenes that would lose their skill file.
        .alert(
            removeRequest.map { L10n.format("移除技能「%@」？", $0.skill.name) } ?? "",
            isPresented: Binding(
                get: { removeRequest != nil },
                set: { if !$0 { removeRequest = nil } }
            )
        ) {
            Button(L10n.text("取消"), role: .cancel) { removeRequest = nil }
            Button(L10n.text("移除"), role: .destructive) {
                guard let request = removeRequest else { return }
                removeRequest = nil
                removeSkill(request.skill)
            }
        } message: {
            if let request = removeRequest {
                Text(request.warningText)
            }
        }
    }

    private func metrics(_ records: [SkillRecord]) -> some View {
        HStack(spacing: Space.m) {
            SkillMetricCard(
                systemImage: "shippingbox",
                value: L10n.format("%d 个技能", records.count),
                tone: Theme.brandPrimary
            )
            SkillMetricCard(
                systemImage: "square.and.arrow.down",
                value: L10n.format("%d 个导入", records.filter(\.isUserSkill).count),
                tone: Theme.systemBlue
            )
            SkillMetricCard(
                systemImage: "square.stack.3d.up",
                value: L10n.format("被 %d 个场景引用", referencedSkillCount),
                tone: Theme.warning
            )
        }
    }

    /// How many stored scenes reference `id` — the 用于 N 个场景 badge.
    private func sceneCount(for skill: OfficialSkill) -> Int {
        preferences.scenes.scenes.filter {
            $0.effectiveSkillIDs.contains(skill.id)
        }.count
    }

    /// How many skills at least one scene references — the 被 N 个场景引用 metric.
    private var referencedSkillCount: Int {
        let referenced = Set(preferences.scenes.scenes.flatMap(\.effectiveSkillIDs))
        return skills.allSkills.filter { referenced.contains($0.id) }.count
    }

    private var controls: some View {
        VStack(alignment: .leading, spacing: Space.s) {
            SkillSearchField(text: $query)

            HStack(spacing: Space.s) {
                Button(L10n.text("导入 ZIP…")) { importing = true }
                    .buttonStyle(SettingsActionButtonStyle(width: nil))

                Text(L10n.text("技能由微信流统一管理，场景用 {{skill:id}} 引用，转发时由目标 Agent 直接读取。"))
                    .font(Typo.paneCaption)
                    .foregroundStyle(Theme.inkTertiary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private var emptyState: some View {
        VStack(spacing: Space.s) {
            Image(systemName: "puzzlepiece.extension")
                .font(.system(size: 26, weight: .regular))
                .foregroundStyle(Theme.inkTertiary)
            Text(L10n.text("没有找到匹配的技能。"))
                .font(Typo.paneBodyStrong)
                .foregroundStyle(Theme.ink)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 54)
    }

    private func makeRecords() -> [SkillRecord] {
        skills.allSkills.map { skill in
            SkillRecord(
                skill: skill,
                isUserSkill: skills.userSkills.contains { $0.id == skill.id },
                libraryState: skills.libraryState(for: skill),
                libraryFile: skills.libraryFile(for: skill)
            )
        }
    }

    private func scopedRecords(_ records: [SkillRecord]) -> [SkillRecord] {
        let needle = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !needle.isEmpty else { return records }
        return records.filter { record in
            record.skill.name.localizedCaseInsensitiveContains(needle)
                || record.skill.summary.localizedCaseInsensitiveContains(needle)
                || record.skill.id.localizedCaseInsensitiveContains(needle)
        }
    }

    private func toggleDetails(_ id: String) {
        if expandedSkillIDs.contains(id) {
            expandedSkillIDs.remove(id)
        } else {
            expandedSkillIDs.insert(id)
        }
    }

    /// 用于 N 个场景 badge — jumps to the scenes page and selects the first
    /// scene referencing this skill.
    private func showScenes(referencing skillID: String) {
        router.tab = .scenes
        router.skillFocus = skillID
    }

    /// 导入技能 — the archive is unpacked into the app-owned library; an id
    /// already owned by an official skill is refused.
    private func importArchive(_ url: URL) {
        do {
            let info = try skills.importArchive(at: url)
            notice = SkillNotice(
                L10n.format(
                    "已导入技能「%@」，场景提示词里可用 %@ 引用。",
                    info.displayName,
                    SkillReference.token(info.id)
                ),
                tone: .good
            )
        } catch {
            notice = SkillNotice(
                (error as? LocalizedError)?.errorDescription ?? error.localizedDescription,
                tone: .bad
            )
        }
    }

    /// 从技能库移除 — the copy moves into the backups, so a mis-tap is
    /// recoverable; only user-imported skills offer it.
    private func removeSkill(_ skill: OfficialSkill) {
        do {
            try skills.removeSkill(skill)
            expandedSkillIDs.remove(skill.id)
            notice = SkillNotice(
                L10n.text("技能已从技能库移除，原包在备份目录中可恢复。"),
                tone: .good
            )
        } catch {
            notice = SkillNotice(
                (error as? LocalizedError)?.errorDescription ?? error.localizedDescription,
                tone: .bad
            )
        }
    }
}

private struct SkillMetricCard: View {
    let systemImage: String
    let value: String
    let tone: Color

    var body: some View {
        HStack(spacing: Space.m) {
            Image(systemName: systemImage)
                .font(.system(size: 18, weight: .medium))
                .foregroundStyle(tone)
                .frame(width: 24)
                .accessibilityHidden(true)
            Text(value)
                .font(Typo.paneBodyStrong)
                .foregroundStyle(Theme.ink)
                .lineLimit(1)
                .minimumScaleFactor(0.9)
        }
        .padding(.horizontal, Space.m)
        .frame(maxWidth: .infinity, minHeight: 58, alignment: .leading)
        .background(Theme.surface, in: RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: Radius.card, style: .continuous)
                .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
        )
        .accessibilityElement(children: .combine)
    }
}

private struct SkillSearchField: View {
    @Binding var text: String
    @FocusState private var focused: Bool
    @State private var hovering = false

    var body: some View {
        HStack(spacing: Space.s) {
            Image(systemName: "magnifyingglass")
                .font(.system(size: 12.5, weight: .medium))
                .foregroundStyle(Theme.inkTertiary)
                .accessibilityHidden(true)
            TextField(L10n.text("搜索技能名、描述或标识"), text: $text)
                .textFieldStyle(.plain)
                .font(SettingsControlMetrics.font)
                .foregroundStyle(Theme.ink)
                .focused($focused)
                .focusEffectDisabled()
        }
        .padding(.horizontal, SettingsControlMetrics.inset)
        .frame(height: SettingsControlMetrics.height)
        .background(
            hovering && !focused ? Theme.hover : Theme.sunken,
            in: RoundedRectangle(cornerRadius: SettingsControlMetrics.radius)
        )
        .overlay(
            RoundedRectangle(cornerRadius: SettingsControlMetrics.radius)
                .strokeBorder(
                    focused ? Theme.ink : Theme.inputStroke,
                    lineWidth: focused ? Stroke.focus : Stroke.hairline
                )
        )
        .onHover { hovering = $0 }
        .accessibilityLabel(Text(L10n.text("搜索技能名、描述或标识")))
    }
}

private struct SkillCard: View {
    let record: SkillRecord
    let sceneCount: Int
    let showScenes: () -> Void
    let expanded: Bool
    let toggleDetails: () -> Void
    let removeSkill: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: Space.m) {
            HStack(alignment: .top, spacing: Space.m) {
                Button(action: toggleDetails) {
                    Image(systemName: "chevron.right")
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundStyle(Theme.inkTertiary)
                        .rotationEffect(.degrees(expanded ? 90 : 0))
                        .frame(width: 16, height: 16)
                }
                .buttonStyle(PlainPressButtonStyle(staticFeedback: true))
                .padding(.top, 4)
                .accessibilityLabel(Text(expanded ? L10n.text("收起详情") : L10n.text("查看详情")))

                SkillMark(skill: record.skill)

                VStack(alignment: .leading, spacing: 4) {
                    HStack(alignment: .firstTextBaseline, spacing: Space.s) {
                        Text(record.skill.name)
                            .font(Typo.paneBodyStrong)
                            .foregroundStyle(Theme.ink)
                        Text("v\(record.skill.version)")
                            .font(Typo.paneCaption.monospaced())
                            .foregroundStyle(Theme.inkTertiary)
                    }

                    Text(record.skill.summary)
                        .font(Typo.paneBody)
                        .foregroundStyle(Theme.inkSecondary)
                        .fixedSize(horizontal: false, vertical: true)
                }

                Spacer(minLength: Space.s)

                StatusPill(text: record.statusTitle, tone: record.statusTone)
            }

            HStack(spacing: Space.s) {
                if record.isUserSkill {
                    SkillBadge(title: L10n.text("导入的技能"), systemImage: "square.and.arrow.down")
                } else {
                    SkillBadge(title: L10n.text("官方技能"), systemImage: "checkmark.seal")
                }
                if let libraryBadge = record.libraryBadge {
                    SkillBadge(
                        title: libraryBadge,
                        systemImage: record.libraryState == .conflict
                            ? "exclamationmark.triangle"
                            : "externaldrive.badge.checkmark"
                    )
                }
                if sceneCount > 0 {
                    Button(action: showScenes) {
                        Label(
                            L10n.format("用于 %d 个场景", sceneCount),
                            systemImage: "square.stack.3d.up"
                        )
                        .font(.system(size: 11.5, weight: .medium))
                        .foregroundStyle(Theme.inkSecondary)
                        .padding(.horizontal, 9)
                        .frame(height: 24)
                        .background(Theme.sunken, in: Capsule(style: .continuous))
                        .contentShape(Capsule())
                    }
                    .buttonStyle(PlainPressButtonStyle(staticFeedback: true))
                } else {
                    SkillBadge(
                        title: L10n.format("用于 %d 个场景", sceneCount),
                        systemImage: "square.stack.3d.up"
                    )
                }
            }

            if expanded {
                Divider()

                if record.libraryState == .missing {
                    Notice(
                        record.isUserSkill
                            ? L10n.text("技能库中的副本不可用或被外部修改，重新导入压缩包可恢复。")
                            : L10n.text("技能包尚未随当前构建提供；场景仍可转发，提示词会要求 Agent 在不可用时说明未完成部分。"),
                        tone: .warn
                    )
                } else if record.libraryState == .conflict {
                    Notice(
                        L10n.text("技能库中的副本被外部修改，场景会引用这份修改后的文件。"),
                        tone: .warn
                    )
                }

                VStack(alignment: .leading, spacing: 6) {
                    HStack(spacing: Space.s) {
                        Text(L10n.text("技能 ID"))
                            .font(Typo.paneCaption)
                            .foregroundStyle(Theme.inkTertiary)
                            .frame(width: 64, alignment: .leading)
                        Text(record.skill.id)
                            .font(Typo.paneCaption.monospaced())
                            .foregroundStyle(Theme.inkSecondary)
                            .textSelection(.enabled)
                    }
                    HStack(spacing: Space.s) {
                        Text(L10n.text("场景引用"))
                            .font(Typo.paneCaption)
                            .foregroundStyle(Theme.inkTertiary)
                            .frame(width: 64, alignment: .leading)
                        Text(SkillReference.token(record.skill.id))
                            .font(Typo.paneCaption.monospaced())
                            .foregroundStyle(Theme.inkSecondary)
                            .textSelection(.enabled)
                        Button(L10n.text("复制")) {
                            NSPasteboard.general.clearContents()
                            NSPasteboard.general.setString(
                                SkillReference.token(record.skill.id),
                                forType: .string
                            )
                        }
                        .buttonStyle(.link)
                        .font(Typo.paneCaption)
                    }
                    HStack(spacing: Space.s) {
                        Text(L10n.text("技能库文件"))
                            .font(Typo.paneCaption)
                            .foregroundStyle(Theme.inkTertiary)
                            .frame(width: 64, alignment: .leading)
                        Text(record.libraryFile ?? L10n.text("不可用"))
                            .font(Typo.paneCaption.monospaced())
                            .foregroundStyle(Theme.inkSecondary)
                            .textSelection(.enabled)
                            .lineLimit(1)
                            .truncationMode(.middle)
                    }
                    if record.isUserSkill {
                        Button(L10n.text("从技能库移除"), role: .destructive, action: removeSkill)
                            .buttonStyle(.link)
                            .font(Typo.paneCaption)
                    }
                }
            }
        }
        .padding(Space.m)
        .background(Theme.surface, in: RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: Radius.card, style: .continuous)
                .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
        )
        .accessibilityElement(children: .contain)
    }
}

private struct SkillMark: View {
    let skill: OfficialSkill

    var body: some View {
        Image(systemName: symbol)
            .font(.system(size: 21, weight: .semibold))
            .foregroundStyle(tone)
            .frame(width: 48, height: 48)
            .background(tone.opacity(0.12), in: RoundedRectangle(cornerRadius: Radius.control))
            .accessibilityHidden(true)
    }

    private var symbol: String {
        if skill.id.contains("article") { return "newspaper.fill" }
        if skill.id.contains("video") { return "play.rectangle.fill" }
        return "puzzlepiece.extension.fill"
    }

    private var tone: Color {
        if skill.id.contains("article") { return Theme.brandPrimary }
        if skill.id.contains("video") { return Theme.systemBlue }
        return Theme.warning
    }
}

private struct SkillBadge: View {
    let title: String
    let systemImage: String

    var body: some View {
        Label(title, systemImage: systemImage)
            .font(.system(size: 11.5, weight: .medium))
            .foregroundStyle(Theme.inkSecondary)
            .padding(.horizontal, 9)
            .frame(height: 24)
            .background(Theme.sunken, in: Capsule(style: .continuous))
    }
}

private struct SkillRecord: Identifiable {
    let skill: OfficialSkill
    let isUserSkill: Bool
    let libraryState: SkillLibraryState
    let libraryFile: String?

    var id: String { skill.id }

    /// The library badge text; nil hides it (a missing package already has
    /// its explainer).
    var libraryBadge: String? {
        switch libraryState {
        case .ready: L10n.text("技能库已就绪")
        case .conflict: L10n.text("技能库副本已被修改")
        case .missing: nil
        }
    }

    var statusTitle: String {
        switch libraryState {
        case .ready: L10n.text("已就绪")
        case .conflict: L10n.text("副本冲突")
        case .missing: L10n.text("缺技能包")
        }
    }

    var statusTone: StatusPill.Tone {
        switch libraryState {
        case .ready: .live
        case .conflict: .bad
        case .missing: .warn
        }
    }
}

/// The 移除 confirmation's payload: the skill plus how many scenes still
/// reference it, so the dialog can warn about prompts losing the file.
private struct RemoveSkillRequest {
    let skill: OfficialSkill
    let sceneCount: Int

    var warningText: String {
        var text = L10n.text("技能包会先移入备份目录，不会直接删除。")
        if sceneCount > 0 {
            text += "\n" + L10n.format(
                "仍有 %d 个场景引用它，移除后这些场景的提示词将找不到技能文件。",
                sceneCount
            )
        }
        return text
    }
}

private struct SkillNotice {
    enum Tone {
        case good
        case warn
        case bad
    }

    let message: String
    let tone: Notice<EmptyView>.Tone

    init(_ message: String, tone: Tone) {
        self.message = message
        switch tone {
        case .good: self.tone = .good
        case .warn: self.tone = .warn
        case .bad: self.tone = .bad
        }
    }
}
