import AppKit
import WeChatBridgeCore
import SwiftUI

/// The links the About pane offers. Constants, because a typo in a URL is not
/// something a user can report usefully.
enum AppLinks {
    static let github = "https://github.com/freestylefly/WeChatBridge"
    static let issues = "https://github.com/freestylefly/WeChatBridge/issues/new"
    static let website = "https://render.qmuse.pub/p/muse/2413870555736078"

    static func open(_ urlString: String) {
        guard let url = URL(string: urlString) else { return }
        NSWorkspace.shared.open(url)
    }
}

enum SettingsTab: String, CaseIterable, Identifiable {
    case general
    case scenes
    case skills
    case entries
    case permissions
    case history
    case about

    var id: String { rawValue }

    var title: String {
        switch self {
        case .general: L10n.text("通用")
        case .scenes: L10n.text("场景")
        case .skills: L10n.text("技能")
        case .entries: L10n.text("入口")
        case .permissions: L10n.text("权限")
        case .history: L10n.text("记录")
        case .about: L10n.text("关于")
        }
    }

    var paneTitle: String {
        self == .skills ? L10n.text("技能中心") : title
    }

    var symbol: String {
        switch self {
        case .general: "slider.horizontal.3"
        case .scenes: "square.stack.3d.up"
        case .skills: "puzzlepiece.extension"
        case .entries: "square.and.arrow.up"
        case .permissions: "checkmark.shield"
        case .history: "clock.arrow.circlepath"
        case .about: "info.circle"
        }
    }
}

/// Which pane the window shows.
///
/// Held outside the view because the callers that decide it are all in AppKit:
/// the status menu opens 设置 on 通用 and 关于 WeChatBridge on 关于, the extension's
/// 打开 WeChatBridge opens 入口, and the guide's 打开设置 opens 通用. Held here so
/// `SettingsWindowController` can set the pane before the window is ordered in,
/// rather than after the view has already drawn one.
@MainActor
final class SettingsRouter: ObservableObject {
    @Published var tab: SettingsTab = .general
    /// Set by the skills pane's 用于 N 个场景 badge; the scenes pane consumes
    /// it by selecting the first scene that references the skill.
    @Published var skillFocus: String?
}

/// What the settings window can ask the rest of the app to do. Closures rather
/// than references, so a pane cannot reach past them into the action runner.
struct SettingsActions {
    /// `target` is only ever set for 「发送到自定义」, whose destination is the
    /// user's choice rather than the action's.
    let perform: (ShareAction, ForwardTarget?, [URL]) -> Void
    /// Brings the window to 入口, where the custom forward targets are managed.
    /// Reached from a 发给 ▸ menu that has nothing of the user's own in it yet.
    let showEntries: () -> Void
    /// Opens the first-run guide again, from its first step.
    let openCollection: (UUID, Bool) -> Void
    let restartOnboarding: () -> Void
}

/// WeChatBridge's only real window: a quiet column of navigation on the left, one
/// scrolling pane on the right.
struct SettingsView: View {
    @ObservedObject var model: AppModel
    @ObservedObject var preferences: Preferences
    @ObservedObject var loginItem: LoginItem
    @ObservedObject var authorization: AccessibilityAuthorization
    @ObservedObject var screenRecording: ScreenRecordingAuthorization
    @ObservedObject var router: SettingsRouter
    @ObservedObject var forwardTargets: ForwardTargets
    @ObservedObject var skills: SkillLibrary
    @ObservedObject var updater: AppUpdater
    let actions: SettingsActions

    /// Bound to no control on purpose. SwiftUI hands a freshly shown window's
    /// focus to its first text field a turn after `makeFirstResponder(nil)`
    /// has run — measured 2026-09-06, when the 入口 pane started opening
    /// scrolled down to the prompt's name field with the field lit. Naming a
    /// default that matches nothing is what stops that.
    @FocusState private var initialFocus: Bool

    var body: some View {
        HStack(spacing: 0) {
            // The navigation column runs the full height of the window and the
            // traffic lights are drawn over its top — which is why it, and not
            // the content, owns the window's left edge.
            nav
                .frame(width: Metrics.settingsNavWidth)
                .frame(maxHeight: .infinity, alignment: .top)
                .background(Theme.sunken)

            ZStack {
                Theme.raised
                ScrollView {
                    VStack(alignment: .leading, spacing: 0) {
                        Text(router.tab.paneTitle)
                            .font(Typo.pageTitle)
                            .foregroundStyle(Theme.ink)
                            .padding(.bottom, Space.l)

                        content
                    }
                    .padding(.horizontal, 34)
                    .padding(.vertical, Space.xl)
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .scrollBounceBehavior(.basedOnSize)
                // Without this the pane opens scrolled to whichever control
                // AppKit made first responder, which hides the page title.
                .defaultScrollAnchor(.top)
                .id(router.tab)
            }
        }
        .frame(minWidth: Metrics.settingsWidth, minHeight: Metrics.settingsHeight)
        .background(Theme.raised)
        .defaultFocus($initialFocus, true)
        .onAppear { authorization.refresh() }
        .modifier(SettingsFocusScope())
    }

    private var nav: some View {
        VStack(alignment: .leading, spacing: 2) {
            ForEach(SettingsTab.allCases) { tab in
                SettingsNavItem(tab: tab, isSelected: router.tab == tab) { router.tab = tab }
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 10)
        .padding(.top, Metrics.settingsTrafficLightInset)
        .padding(.bottom, Space.l)
    }

    @ViewBuilder
    private var content: some View {
        switch router.tab {
        case .general:
            GeneralPane(
                preferences: preferences,
                loginItem: loginItem,
                authorization: authorization,
                screenRecording: screenRecording,
                updater: updater,
                actions: actions
            )
        case .scenes:
            SceneSettingsView(preferences: preferences, skills: skills, router: router)
        case .skills:
            SkillsPane(skills: skills, preferences: preferences, router: router)
        case .entries:
            EntriesPane(targets: forwardTargets, preferences: preferences)
        case .permissions:
            PermissionsPane(authorization: authorization, screenRecording: screenRecording)
        case .history:
            HistoryPane(
                model: model,
                targets: forwardTargets,
                preferences: preferences,
                actions: actions
            )
        case .about:
            AboutPane(updater: updater)
        }
    }
}

private struct SettingsNavItem: View {
    let tab: SettingsTab
    let isSelected: Bool
    let action: () -> Void
    @State private var hovering = false
    @FocusState private var focused: Bool

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: tab.symbol)
                    .font(.system(size: 12.5, weight: .medium))
                    .frame(width: 16)
                    .foregroundStyle(isSelected ? Theme.brandPrimary : Theme.inkSecondary)
                Text(tab.title)
                    .font(.system(size: 14.5, weight: isSelected ? .semibold : .regular))
                    .foregroundStyle(isSelected ? Theme.ink : Theme.inkSecondary)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 11)
            .padding(.vertical, Space.s)
            .background(
                RoundedRectangle(cornerRadius: Radius.control, style: .continuous)
                    .fill(isSelected ? Theme.brandTint : (hovering ? Theme.hover : Color.clear))
            )
            .contentShape(Rectangle())
        }
        .buttonStyle(PlainPressButtonStyle(staticFeedback: true))
        .focused($focused)
        .focusEffectDisabled()
        .modifier(SettingsFocusRing(focused: focused, radius: Radius.control))
        .onHover { hovering = $0 }
        .accessibilityAddTraits(isSelected ? [.isSelected] : [])
        .accessibilityRemoveTraits(isSelected ? [] : [.isSelected])
        .accessibilityIdentifier("settings." + tab.rawValue)
    }
}
