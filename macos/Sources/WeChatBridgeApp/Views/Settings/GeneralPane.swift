import AppKit
import WeChatBridgeCore
import SwiftUI

struct GeneralPane: View {
    @ObservedObject var preferences: Preferences
    @ObservedObject var loginItem: LoginItem
    @ObservedObject var authorization: AccessibilityAuthorization
    @ObservedObject var screenRecording: ScreenRecordingAuthorization
    @ObservedObject var updater: AppUpdater
    let actions: SettingsActions

    @StateObject private var entries = ShareEntryProbe()
    @State private var accessibilityRequested = false
    @State private var screenRecordingRequested = false

    var body: some View {
        VStack(alignment: .leading, spacing: Space.l) {
            Text(L10n.text("微信流状态与应用偏好"))
                .font(Typo.paneBody)
                .foregroundStyle(Theme.inkSecondary)

            GeneralCard(title: L10n.text("运行状态")) {
                VStack(alignment: .leading, spacing: Space.l) {
                    readiness
                    if missingEnhancementCount > 0 { enhancements }
                }
            }

            GeneralCard(title: L10n.text("应用行为")) {
                VStack(spacing: 0) {
                    GeneralSettingRow(
                        systemImage: "power",
                        title: L10n.text("登录时自动启动"),
                        detail: L10n.text("开机后静默运行，随时响应微信转发。")
                    ) {
                        Toggle(L10n.text("登录时自动启动"), isOn: Binding(
                            get: { loginItem.isEnabled },
                            set: { loginItem.setEnabled($0) }
                        ))
                        .toggleStyle(SwitchToggleStyle())
                        .labelsHidden()
                    }
                    .padding(.vertical, 11)

                    if loginItem.needsApproval {
                        Notice(text: L10n.text("还需要在系统设置里允许。"), tone: .warn) {
                            Button(L10n.text("打开登录项设置")) { LoginItem.openLoginItemsSettings() }
                                .buttonStyle(SettingsActionButtonStyle())
                        }
                        .padding(.bottom, Space.m)
                    }
                    if let error = loginItem.lastError {
                        Notice(error, tone: .bad)
                            .padding(.bottom, Space.m)
                    }

                    GeneralDivider()

                    GeneralSettingRow(
                        systemImage: "dock.rectangle",
                        title: L10n.text("在 Dock 中显示"),
                        detail: L10n.text("从 Dock 快速打开微信流设置。")
                    ) {
                        Toggle(L10n.text("在 Dock 中显示"), isOn: $preferences.showInDock)
                            .toggleStyle(SwitchToggleStyle())
                            .labelsHidden()
                    }
                    .padding(.vertical, 11)
                }
            }

            GeneralCard(title: L10n.text("软件与帮助")) {
                VStack(spacing: 0) {
                    GeneralSettingRow(
                        systemImage: "arrow.triangle.2.circlepath",
                        title: L10n.text("自动检查更新"),
                        detail: L10n.text("每天检查一次安全更新。")
                    ) {
                        Toggle(L10n.text("自动检查更新"), isOn: Binding(
                            get: { updater.automaticallyChecksForUpdates },
                            set: { updater.automaticallyChecksForUpdates = $0 }
                        ))
                        .toggleStyle(SwitchToggleStyle())
                        .labelsHidden()
                    }
                    .padding(.vertical, 11)

                    GeneralDivider()

                    GeneralSettingRow(
                        systemImage: "info.circle",
                        title: L10n.text("当前版本"),
                        subtitle: AppVersion.short + " (" + AppVersion.build + ")"
                    ) {
                        Button(L10n.text("检查更新…")) { updater.checkForUpdates() }
                            .buttonStyle(SettingsActionButtonStyle())
                            .disabled(!updater.canCheckForUpdates)
                    }
                    .padding(.vertical, 11)

                    GeneralDivider()

                    Button {
                        actions.restartOnboarding()
                    } label: {
                        HStack(spacing: Space.m) {
                            Image(systemName: "sparkles")
                                .font(.system(size: 13, weight: .medium))
                                .foregroundStyle(Theme.ink)
                                .frame(width: 22)
                                .accessibilityHidden(true)
                            Text(L10n.text("重新运行设置向导"))
                                .font(Typo.rowTitle)
                                .foregroundStyle(Theme.ink)
                            Spacer(minLength: Space.m)
                            Image(systemName: "chevron.right")
                                .font(.system(size: 12, weight: .semibold))
                                .foregroundStyle(Theme.inkTertiary)
                                .accessibilityHidden(true)
                        }
                        .padding(.vertical, 11)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(PlainPressButtonStyle(staticFeedback: true))
                    .accessibilityIdentifier("general.setup-guide")
                }
            }
        }
        .onAppear(perform: refreshStatus)
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            refreshStatus()
        }
    }

    private var readiness: some View {
        HStack(alignment: .center, spacing: Space.m) {
            readinessIcon

            VStack(alignment: .leading, spacing: 3) {
                Text(readinessTitle)
                    .font(.system(size: 17, weight: .semibold))
                    .foregroundStyle(Theme.ink)
                Text(readinessDetail)
                    .font(Typo.paneBody)
                    .foregroundStyle(Theme.inkSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            Spacer(minLength: Space.m)

            if entries.states.isEmpty {
                ProgressView()
                    .controlSize(.small)
                    .frame(width: SettingsControlMetrics.actionWidth)
            } else {
                Button(readinessAction) { actions.showEntries() }
                    .buttonStyle(SettingsActionButtonStyle())
            }
        }
    }

    private var enhancements: some View {
        VStack(spacing: 0) {
            HStack(spacing: Space.s) {
                Image(systemName: "exclamationmark.circle.fill")
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(Theme.warning)
                    .accessibilityHidden(true)
                Text(L10n.format("还有 %d 项增强设置", missingEnhancementCount))
                    .font(Typo.paneBodyStrong)
                    .foregroundStyle(Theme.warning)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, Space.m)
            .padding(.vertical, Space.m)

            if !authorization.isTrusted {
                GeneralDivider(color: Theme.warning.opacity(0.18))
                enhancementRow(
                    systemImage: "doc.on.clipboard",
                    title: L10n.text("自动粘贴"),
                    detail: L10n.text("开启辅助功能后，可自动切换应用并粘贴。"),
                    requested: accessibilityRequested,
                    action: enableAccessibility
                )
            }

            if !screenRecording.isGranted {
                GeneralDivider(color: Theme.warning.opacity(0.18))
                enhancementRow(
                    systemImage: "person.2",
                    title: L10n.text("识别群聊名称"),
                    detail: L10n.text("开启屏幕录制后，可自动匹配群聊场景。"),
                    requested: screenRecordingRequested,
                    action: enableScreenRecording
                )
            }
        }
        .background(Theme.warningSoft, in: RoundedRectangle(cornerRadius: Radius.control, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: Radius.control, style: .continuous)
                .strokeBorder(Theme.warning.opacity(0.18), lineWidth: Stroke.hairline)
        }
    }

    private func enhancementRow(
        systemImage: String,
        title: String,
        detail: String,
        requested: Bool,
        action: @escaping () -> Void
    ) -> some View {
        GeneralSettingRow(systemImage: systemImage, title: title, detail: detail) {
            Button(requested ? L10n.text("打开系统设置") : L10n.text("开启"), action: action)
                .buttonStyle(SettingsActionButtonStyle())
        }
        .padding(.horizontal, Space.m)
        .padding(.vertical, 9)
    }

    @ViewBuilder
    private var readinessIcon: some View {
        if entries.states.isEmpty {
            ProgressView()
                .controlSize(.small)
                .frame(width: 36, height: 36)
        } else if hasUsableEntry {
            ZStack {
                Circle().fill(Theme.brandPrimary)
                Image(systemName: "checkmark")
                    .font(.system(size: 17, weight: .bold))
                    .foregroundStyle(Theme.onBrand)
            }
            .frame(width: 36, height: 36)
            .accessibilityHidden(true)
        } else {
            ZStack {
                Circle().fill(Theme.warning)
                Image(systemName: "exclamationmark")
                    .font(.system(size: 16, weight: .bold))
                    .foregroundStyle(Theme.onBrand)
            }
            .frame(width: 36, height: 36)
            .accessibilityHidden(true)
        }
    }

    private var hasUsableEntry: Bool {
        ShareAction.allCases.contains { entries.state(of: $0) == .enabled }
    }

    private var readinessTitle: String {
        guard !entries.states.isEmpty else { return L10n.text("正在检查分享入口") }
        return hasUsableEntry ? L10n.text("微信流已可使用") : L10n.text("还差一步即可使用")
    }

    private var readinessDetail: String {
        guard !entries.states.isEmpty else { return L10n.text("正在确认微信「转发到其他应用」里的入口。") }
        return hasUsableEntry
            ? L10n.text("已启用分享入口，微信导出的文件可以安全送达目标应用。")
            : L10n.text("开启一个分享入口后，就可以从微信转发文件。")
    }

    private var readinessAction: String {
        hasUsableEntry ? L10n.text("管理入口") : L10n.text("开启分享入口")
    }

    private var missingEnhancementCount: Int {
        (authorization.isTrusted ? 0 : 1) + (screenRecording.isGranted ? 0 : 1)
    }

    private func refreshStatus() {
        entries.refresh()
        authorization.refresh()
        screenRecording.refresh()
    }

    private func enableAccessibility() {
        authorization.refresh()
        guard !authorization.isTrusted else { return }
        if accessibilityRequested {
            AutoPaste.openAccessibilitySettings()
        } else {
            accessibilityRequested = true
            authorization.guideIfNeeded()
        }
    }

    private func enableScreenRecording() {
        screenRecording.refresh()
        guard !screenRecording.isGranted else { return }
        if screenRecordingRequested {
            screenRecording.openSystemSettings()
        } else {
            screenRecordingRequested = true
            screenRecording.request()
        }
    }
}

private struct GeneralCard<Content: View>: View {
    let title: String
    @ViewBuilder var content: () -> Content

    var body: some View {
        VStack(alignment: .leading, spacing: Space.l) {
            Text(title)
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(Theme.ink)
            content()
        }
        .padding(Space.l)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Theme.surface, in: RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: Radius.card, style: .continuous)
                .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
        }
    }
}

private struct GeneralSettingRow<Control: View>: View {
    let systemImage: String
    let title: String
    var subtitle: String?
    var detail: String?
    @ViewBuilder var control: () -> Control

    init(
        systemImage: String,
        title: String,
        subtitle: String? = nil,
        detail: String? = nil,
        @ViewBuilder control: @escaping () -> Control
    ) {
        self.systemImage = systemImage
        self.title = title
        self.subtitle = subtitle
        self.detail = detail
        self.control = control
    }

    var body: some View {
        HStack(alignment: .center, spacing: Space.m) {
            Image(systemName: systemImage)
                .font(.system(size: 13, weight: .medium))
                .foregroundStyle(Theme.ink)
                .frame(width: 22)
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 2) {
                Text(title)
                    .font(Typo.rowTitle)
                    .foregroundStyle(Theme.ink)
                if let subtitle {
                    Text(subtitle)
                        .font(Typo.paneCaption)
                        .foregroundStyle(Theme.inkSecondary)
                }
            }
            .frame(width: 142, alignment: .leading)

            if let detail {
                Text(detail)
                    .font(Typo.paneCaption)
                    .foregroundStyle(Theme.inkSecondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
            } else {
                Spacer(minLength: 0)
            }

            control()
        }
    }
}

private struct GeneralDivider: View {
    var color: Color = Theme.stroke

    var body: some View {
        Rectangle()
            .fill(color)
            .frame(height: Stroke.hairline)
            .accessibilityHidden(true)
    }
}
