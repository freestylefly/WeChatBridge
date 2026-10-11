import AppKit
import WeChatBridgeCore
import SwiftUI

/// The version out of the bundle. A plain `swift run` build has no Info.plist at
/// all, so both fields carry a placeholder rather than crashing the pane.
enum AppVersion {
    static var short: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.1.0"
    }

    static var build: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "—"
    }

    /// The same two values as numbers, for the update checker's comparison.
    static var components: [Int] {
        short.split(separator: ".").compactMap { Int($0) }
    }

    static var buildNumber: Int {
        Int(build) ?? 0
    }
}

struct AboutPane: View {
    @ObservedObject var updater: AppUpdater
    @State private var showingLicenses = false
    @State private var hoveringLicenses = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            appHeader
            divider

            aboutSection(title: L10n.text("软件更新"), systemImage: "arrow.triangle.2.circlepath") {
                if updater.usesSparkleFeed {
                    paragraph(L10n.text("自动检查并安装新版本。"))
                } else {
                    paragraph(L10n.text("从蓝奏云分享页检查新版本。"))
                    updateStatus
                }
                HStack(spacing: Space.m) {
                    Button(L10n.text("检查更新…")) { updater.checkForUpdates() }
                        .buttonStyle(SettingsActionButtonStyle(width: 150))
                        .disabled(!updater.canCheckForUpdates)
                    if let release = updater.availableUpdate {
                        Button(L10n.text("前往下载")) {
                            AppLinks.open(release.pageURL.absoluteString)
                        }
                        .buttonStyle(SettingsActionButtonStyle(width: 150))
                    }
                }
                if updater.availableUpdate != nil {
                    paragraph(L10n.format("网盘密码：%@", LanzouUpdateChecker.sharePassword))
                }
            }
            .padding(.vertical, Space.xl)
            .task { updater.checkForUpdatesIfIdle() }

            divider

            aboutSection(title: L10n.text("WeChatBridge 做什么"), systemImage: "info.circle") {
                paragraph(L10n.text("在微信的转发菜单里，直接把聊天记录送到你要用的地方。"))
            }
            .padding(.vertical, Space.xl)

            divider

            aboutSection(title: L10n.text("项目与支持"), systemImage: "arrow.up.right.square") {
                paragraph(L10n.text("在 GitHub 上查看项目，访问官网，或向我们提交建议与问题。"))
                HStack(spacing: Space.m) {
                    linkButton(
                        L10n.text("访问项目"),
                        systemImage: "chevron.left.forwardslash.chevron.right",
                        url: AppLinks.github
                    )
                    linkButton(
                        L10n.text("访问官网"),
                        systemImage: "globe",
                        url: AppLinks.website
                    )
                    linkButton(
                        L10n.text("提交反馈"),
                        systemImage: "bubble.left",
                        url: AppLinks.issues
                    )
                }
            }
            .padding(.vertical, Space.xl)

            divider

            aboutSection(title: L10n.text("开源致谢"), systemImage: "heart") {
                paragraph(L10n.text("基于开源项目构建，感谢原作者的贡献。"))
                licenseButton
            }
            .padding(.vertical, Space.xl)
        }
        .sheet(isPresented: $showingLicenses) {
            LicenseSheet()
        }
    }

    private var appHeader: some View {
        HStack(alignment: .top, spacing: 20) {
            Image(nsImage: NSApp.applicationIconImage ?? NSImage())
                .resizable()
                .interpolation(.high)
                .frame(width: 76, height: 76)
                .accessibilityHidden(true)

            VStack(alignment: .leading, spacing: 8) {
                Text(verbatim: "微信流")
                    .font(.system(size: 26, weight: .semibold))
                    .foregroundStyle(Theme.ink)
                StatusPill(text: L10n.format("版本 %@ (%@)", AppVersion.short, AppVersion.build))
                Text(L10n.text("让微信的内容，流向你需要的地方。"))
                    .font(.system(size: 14))
                    .foregroundStyle(Theme.inkSecondary)
            }
        }
        .padding(.bottom, Space.xl)
    }

    private var divider: some View {
        Rectangle()
            .fill(Theme.stroke)
            .frame(height: Stroke.hairline)
    }

    private var licenseButton: some View {
        Button {
            showingLicenses = true
        } label: {
            HStack(spacing: 10) {
                Image(systemName: "doc.text")
                    .font(.system(size: 13, weight: .medium))
                    .foregroundStyle(Theme.inkSecondary)
                Text(L10n.text("许可证与第三方声明"))
                    .font(Typo.paneBody)
                    .foregroundStyle(Theme.ink)
                Spacer(minLength: Space.s)
                Image(systemName: "chevron.right")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(Theme.inkTertiary)
            }
            .padding(.horizontal, Space.m)
            .padding(.vertical, 10)
            .frame(maxWidth: 620, alignment: .leading)
            .background(
                RoundedRectangle(cornerRadius: Radius.control, style: .continuous)
                    .fill(hoveringLicenses ? Theme.hover : Color.clear)
            )
            .contentShape(RoundedRectangle(cornerRadius: Radius.control, style: .continuous))
        }
        .buttonStyle(PlainPressButtonStyle(staticFeedback: true))
        .onHover { hoveringLicenses = $0 }
        .accessibilityIdentifier("about.licenses")
    }

    private func aboutSection<Content: View>(
        title: String,
        systemImage: String,
        @ViewBuilder content: () -> Content
    ) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 12) {
                Image(systemName: systemImage)
                    .font(.system(size: 17, weight: .medium))
                    .foregroundStyle(Theme.inkSecondary)
                    .frame(width: 26, alignment: .leading)
                Text(title)
                    .font(.system(size: 17.5, weight: .semibold))
                    .foregroundStyle(Theme.ink)
            }

            VStack(alignment: .leading, spacing: 12) {
                content()
            }
            .padding(.leading, 38)
        }
    }

    private func linkButton(_ title: String, systemImage: String, url: String) -> some View {
        Button {
            AppLinks.open(url)
        } label: {
            Label(title, systemImage: systemImage)
        }
        .buttonStyle(SettingsActionButtonStyle(width: 150))
    }

    @ViewBuilder
    private var updateStatus: some View {
        switch updater.updateState {
        case .idle:
            paragraph(L10n.text("尚未检查更新。"))
        case .checking:
            HStack(spacing: 10) {
                ProgressView()
                    .controlSize(.small)
                Text(L10n.text("正在检查更新…"))
                    .font(.system(size: 14))
                    .foregroundStyle(Theme.inkSecondary)
            }
        case .upToDate:
            paragraph(L10n.format("已是最新版本 %@ (%@)。", AppVersion.short, AppVersion.build))
        case .updateAvailable(let release):
            paragraph(L10n.format(
                "发现新版本 %@ (%@)，大小 %@。",
                release.versionLabel, String(release.build), release.size
            ))
        case .failed(let message):
            paragraph(L10n.format("检查更新失败：%@", message))
        }
    }

    private func paragraph(_ text: String) -> some View {
        Text(text)
            .font(.system(size: 14))
            .foregroundStyle(Theme.inkSecondary)
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: 620, alignment: .leading)
    }
}

private struct LicenseSheet: View {
    @Environment(\.dismiss) private var dismiss

    private let documents = LicenseDocument.load()

    var body: some View {
        VStack(alignment: .leading, spacing: Space.xl) {
            HStack {
                Text(L10n.text("许可证与第三方声明"))
                    .font(Typo.paneTitle)
                    .foregroundStyle(Theme.ink)
                Spacer(minLength: Space.l)
                Button(L10n.text("完成")) { dismiss() }
                    .buttonStyle(SettingsActionButtonStyle())
                    .keyboardShortcut(.defaultAction)
            }

            if documents.isEmpty {
                Text(L10n.text("当前构建未包含许可证文件。"))
                    .font(Typo.paneBody)
                    .foregroundStyle(Theme.inkSecondary)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: Space.section) {
                        ForEach(documents) { document in
                            VStack(alignment: .leading, spacing: 10) {
                                Text(document.title)
                                    .font(Typo.sectionLabel)
                                    .foregroundStyle(Theme.inkSecondary)

                                Rectangle()
                                    .fill(Theme.stroke)
                                    .frame(height: Stroke.hairline)

                                Text(document.body)
                                    .font(Typo.paneCaption)
                                    .foregroundStyle(Theme.inkSecondary)
                                    .lineSpacing(2)
                                    .textSelection(.enabled)
                                    .fixedSize(horizontal: false, vertical: true)
                            }
                        }
                    }
                    .padding(.trailing, Space.s)
                }
                .defaultScrollAnchor(.top)
            }
        }
        .padding(Space.xl)
        .frame(width: 600, height: 500, alignment: .topLeading)
    }
}

private struct LicenseDocument: Identifiable {
    let id: String
    let title: String
    let body: String

    static func load() -> [LicenseDocument] {
        [
            document(
                id: "application",
                title: L10n.text("本应用许可"),
                resource: "LICENSE"
            ),
            document(
                id: "notices",
                title: L10n.text("第三方声明"),
                resource: "THIRD-PARTY-NOTICES",
                fileExtension: "md"
            ),
            document(
                id: "sparkle",
                title: L10n.text("Sparkle 许可"),
                resource: "Sparkle-LICENSE",
                fileExtension: "txt",
                subdirectory: "Licenses"
            )
        ].compactMap { $0 }
    }

    private static func document(
        id: String,
        title: String,
        resource: String,
        fileExtension: String? = nil,
        subdirectory: String? = nil
    ) -> LicenseDocument? {
        guard let url = Bundle.main.url(
            forResource: resource,
            withExtension: fileExtension,
            subdirectory: subdirectory
        ),
        let body = try? String(contentsOf: url, encoding: .utf8) else {
            return nil
        }
        return LicenseDocument(id: id, title: title, body: body)
    }
}
