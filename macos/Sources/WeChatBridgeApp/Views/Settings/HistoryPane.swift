import WeChatBridgeCore
import SwiftUI

/// Every batch still on disk, newest first.
///
/// A batch stays here after it has been forwarded, because "did that actually
/// arrive?" is the question the record answers.
struct HistoryPane: View {
    @ObservedObject var model: AppModel
    @ObservedObject var targets: ForwardTargets
    @ObservedObject var preferences: Preferences
    let actions: SettingsActions

    @State private var query = ""
    @State private var confirmingClear = false

    var body: some View {
        VStack(alignment: .leading, spacing: Space.l) {
            header

            if let failure = model.inboxFailure {
                Notice(failure, tone: .bad)
            }

            ForEach(filteredCollections) { collection in
                CollectionHistoryCard(model: model, collection: collection, actions: actions)
            }

            if model.batches.isEmpty && filteredCollections.isEmpty {
                emptyState(
                    title: L10n.text("还没有记录"),
                    detail: L10n.text("从微信转发到任意一个 WeChatBridge 入口后，这里会列出来。")
                )
            } else if filteredBatches.isEmpty && filteredCollections.isEmpty {
                emptyState(
                    title: L10n.text("没有匹配的记录"),
                    detail: L10n.text("试试按群聊、场景或文件名搜索。")
                )
            } else {
                recordList
            }
        }
        .frame(maxWidth: 1120, alignment: .leading)
        .frame(maxWidth: .infinity, alignment: .leading)
        .alert(L10n.text("清空全部记录？"), isPresented: $confirmingClear) {
            Button(L10n.text("取消"), role: .cancel) {}
            Button(L10n.text("移到废纸篓"), role: .destructive) {
                model.discardHistory()
            }
        } message: {
            Text(
                L10n.format("将 %d 条记录（%@）移到废纸篓，可从废纸篓恢复。",
                    model.batches.count,
                    ByteFormat.string(model.historyByteCount)
                )
            )
        }
    }

    private var header: some View {
        ViewThatFits(in: .horizontal) {
            HStack(spacing: Space.s) {
                summary.fixedSize(horizontal: true, vertical: false)
                Spacer(minLength: Space.s)
                searchField
                finderButton
                moreMenu
            }

            VStack(alignment: .leading, spacing: Space.s) {
                HStack(spacing: Space.s) {
                    summary
                    Spacer(minLength: Space.s)
                    moreMenu
                }
                HStack(spacing: Space.s) {
                    searchField
                    finderButton
                    Spacer(minLength: 0)
                }
            }
        }
    }

    private var summary: some View {
        Text(
            L10n.format("共 %d 条 · 占用 %@ · %@",
                model.historyEntryCount,
                ByteFormat.string(model.historyByteCount),
                retentionText
            )
        )
        .font(Typo.paneCaption)
        .foregroundStyle(Theme.inkSecondary)
        .lineLimit(1)
    }

    private var retentionText: String {
        preferences.historyRetentionDays == 0
            ? L10n.text("永久保留")
            : L10n.format("默认保留 %d 天", preferences.historyRetentionDays)
    }

    private var searchField: some View {
        HistorySearchField(text: $query)
            .frame(width: 260)
    }

    private var finderButton: some View {
        Button(L10n.text("在 Finder 中显示")) { model.revealInbox() }
            .buttonStyle(SettingsActionButtonStyle())
            .fixedSize()
    }

    private var moreMenu: some View {
        SettingsMoreActionsButton(
            items: [
                SettingsAction(id: "clear", title: L10n.text("清空记录…")) {
                    confirmingClear = true
                }
            ],
            identifier: "history.more"
        )
        .disabled(!model.hasDiscardableHistory)
    }

    private var filteredBatches: [ReadyBatch] {
        let grouped = Set(model.collectionLedger.collections.flatMap(\.batchIDs))
        return model.batches.filter { !grouped.contains($0.id) && HistoryLabel.matches($0, query: query) }
    }

    private var filteredCollections: [BatchCollection] {
        model.collectionLedger.collections.filter { group in
            query.isEmpty || model.collectionName(group).localizedCaseInsensitiveContains(query)
                || group.sceneName?.localizedCaseInsensitiveContains(query) == true
                || model.collectionBatches(group).contains { HistoryLabel.matches($0, query: query) }
        }.sorted { a, b in
            if (a.status == .collecting) != (b.status == .collecting) { return a.status == .collecting }
            return a.createdAt > b.createdAt
        }
    }

    private var groups: [HistoryDay] {
        let calendar = Calendar.current
        let grouped = Dictionary(grouping: filteredBatches) {
            calendar.startOfDay(for: $0.createdAt)
        }
        return grouped
            .map { HistoryDay(day: $0.key, batches: $0.value) }
            .sorted { $0.day > $1.day }
    }

    private var recordList: some View {
        LazyVStack(alignment: .leading, spacing: Space.xl) {
            ForEach(groups) { group in
                VStack(alignment: .leading, spacing: Space.s) {
                    Text(HistoryLabel.sectionTitle(for: group.day))
                        .font(Typo.captionStrong)
                        .foregroundStyle(Theme.inkSecondary)
                        .padding(.leading, Space.xs)

                    ForEach(group.batches) { batch in
                        row(for: batch)
                    }
                }
            }
        }
    }

    private func emptyState(title: String, detail: String) -> some View {
        VStack(spacing: Space.s) {
            Image(systemName: query.isEmpty ? "tray" : "magnifyingglass")
                .font(.system(size: 30, weight: .light))
                .foregroundStyle(Theme.inkTertiary)
                .accessibilityHidden(true)
            Text(title)
                .font(Typo.paneBodyStrong)
                .foregroundStyle(Theme.ink)
            Text(detail)
                .font(Typo.paneCaption)
                .foregroundStyle(Theme.inkSecondary)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 60)
    }

    private func row(for batch: ReadyBatch) -> some View {
        HStack(spacing: Space.m) {
            if let first = batch.items.first {
                Image(nsImage: IconCache.icon(for: first.url))
                    .resizable()
                    .frame(width: 34, height: 34)
                    .accessibilityHidden(true)
            }

            VStack(alignment: .leading, spacing: 2) {
                Text(HistoryLabel.paneTitle(for: batch))
                    .font(Typo.paneBodyStrong)
                    .foregroundStyle(Theme.ink)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Text(HistoryLabel.paneSubtitle(for: batch))
                    .font(Typo.paneCaption)
                    .foregroundStyle(Theme.inkSecondary)
                    .lineLimit(1)
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            Text(HistoryLabel.clockTime(batch.createdAt))
                .font(.numeral(12, .medium))
                .foregroundStyle(Theme.inkTertiary)
                .lineLimit(1)
                .fixedSize()

            StatusPill(text: statusText(for: batch), tone: statusTone(for: batch))
                .fixedSize()

            HistoryForwardButton(
                items: forwardItems(for: batch),
                identifier: "history.forward"
            )

            menu(for: batch)
        }
        .padding(.horizontal, Space.m)
        .padding(.vertical, 10)
        .background(Theme.sunken, in: RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
        .accessibilityElement(children: .contain)
    }

    private func forwardItems(for batch: ReadyBatch) -> [SettingsAction] {
        let urls = batch.items.map(\.url)
        var items = targets.destinations.map { destination in
            SettingsAction(
                id: "forward.\(destination.id)",
                title: L10n.format("发给 %@", destination.title)
            ) {
                actions.perform(destination.action, destination.target, urls)
            }
        }
        if targets.isEmpty {
            items.append(SettingsAction(
                id: "entries",
                title: L10n.text("添加应用…"),
                isSeparatorBefore: true
            ) {
                actions.showEntries()
            })
        }
        return items
    }

    private func menu(for batch: ReadyBatch) -> some View {
        let urls = batch.items.map(\.url)
        return SettingsMoreActionsButton(
            items: [
                SettingsAction(id: "clipboard", title: L10n.text("复制到剪贴板")) {
                    actions.perform(.clipboard, nil, urls)
                },
                SettingsAction(
                    id: "reveal",
                    title: L10n.text("在 Finder 中显示"),
                    isSeparatorBefore: true
                ) {
                    batch.items.first.map { model.reveal(id: $0.id) }
                },
                SettingsAction(id: "discard", title: L10n.text("移到废纸篓")) {
                    model.discard(batchID: batch.id)
                },
            ],
            identifier: "history.actions"
        )
    }

    private func statusText(for batch: ReadyBatch) -> String {
        switch batch.outcome?.kind {
        case .delivered:
            return "\(HistoryLabel.destinationName(for: batch)) · \(L10n.text("已送达"))"
        case .copied:
            return L10n.text("已复制")
        case .failed:
            guard let detail = batch.outcome?.detail, !detail.isEmpty else {
                return L10n.text("未送达")
            }
            return "\(L10n.text("未送达")) · \(detail)"
        case .expired, nil:
            return L10n.text("未执行")
        }
    }

    private func statusTone(for batch: ReadyBatch) -> StatusPill.Tone {
        switch batch.outcome?.kind {
        case .delivered, .copied: return .live
        case .failed: return .warn
        case .expired, nil: return .neutral
        }
    }
}

private struct HistoryDay: Identifiable {
    let day: Date
    let batches: [ReadyBatch]

    var id: Date { day }
}

private struct HistorySearchField: View {
    @Binding var text: String
    @FocusState private var focused: Bool

    var body: some View {
        HStack(spacing: 7) {
            Image(systemName: "magnifyingglass")
                .font(.system(size: 12, weight: .medium))
                .foregroundStyle(Theme.inkTertiary)
                .accessibilityHidden(true)

            TextField(L10n.text("搜索群聊、场景或文件名"), text: $text)
                .textFieldStyle(.plain)
                .font(Typo.paneCaption)
                .foregroundStyle(Theme.ink)

            if !text.isEmpty {
                Button {
                    text = ""
                } label: {
                    Image(systemName: "xmark.circle.fill")
                        .font(.system(size: 11, weight: .medium))
                        .foregroundStyle(Theme.inkTertiary)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(Text(L10n.text("清除搜索")))
            }
        }
        .padding(.horizontal, SettingsControlMetrics.inset)
        .frame(height: SettingsControlMetrics.height)
        .background(Theme.sunken, in: RoundedRectangle(cornerRadius: SettingsControlMetrics.radius))
        .overlay {
            RoundedRectangle(cornerRadius: SettingsControlMetrics.radius)
                .strokeBorder(focused ? Theme.ink : Theme.inputStroke, lineWidth: focused ? Stroke.focus : Stroke.hairline)
                .allowsHitTesting(false)
        }
        .focused($focused)
        .focusEffectDisabled()
    }
}

private struct HistoryForwardButton: View {
    let items: [SettingsAction]
    let identifier: String

    @State private var expanded = false
    @FocusState private var focused: Bool

    var body: some View {
        Button(L10n.text("发给…")) {
            expanded.toggle()
        }
        .buttonStyle(SettingsActionButtonStyle(width: 72))
        .focused($focused)
        .focusEffectDisabled()
        .modifier(SettingsFocusRing(focused: focused))
        .fixedSize()
        .popover(isPresented: $expanded, arrowEdge: .bottom) {
            SettingsActionList(items: items, identifier: identifier) {
                expanded = false
            }
            .frame(width: 220)
        }
        .onChange(of: expanded) { _, value in
            if !value { focused = true }
        }
        .accessibilityLabel(Text(L10n.text("发给…")))
    }
}
