import AppKit
import WeChatBridgeCore
import SwiftUI
import UniformTypeIdentifiers

/// The first run, in the order WeChatBridge is actually adopted: switch on an
/// entry, hand over the one permission, done.
///
/// A window of its own rather than a pane in 设置, because none of this is a
/// setting. WeChatBridge's whole surface lives inside WeChat's Share menu, which
/// a settings window cannot demonstrate, and a user who never finds the entries
/// never sees the app work at all.
///
/// Every step is laid out to fit without scrolling. A guide whose next button is
/// below the fold is a guide people abandon, so anything that does not fit is a
/// cue to cut the copy rather than to add a `ScrollView`.
struct OnboardingFlow: View {
    @ObservedObject var preferences: Preferences
    @ObservedObject var authorization: AccessibilityAuthorization
    /// Closes the guide. The app opens 设置 behind it, so the last step has
    /// nothing else to ask.
    let finish: () -> Void

    /// One probe for the whole guide, and the very same list the 入口 pane
    /// draws — §11.2 asks for one implementation of these switches, not two.
    @StateObject private var probe = ShareEntryProbe()
    @State private var step: Step

    init(
        preferences: Preferences,
        authorization: AccessibilityAuthorization,
        finish: @escaping () -> Void
    ) {
        self.preferences = preferences
        self.authorization = authorization
        self.finish = finish
        _step = State(initialValue: Step(rawValue: preferences.onboardingStep) ?? .entries)
    }

    enum Step: Int, CaseIterable {
        case entries, permissions, done

        var title: String {
            switch self {
            case .entries: L10n.text("入口")
            case .permissions: L10n.text("权限")
            case .done: L10n.text("完成")
            }
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            StepBar(steps: Step.allCases.map(\.title), current: step.rawValue)
                .frame(height: 52)
                .padding(.top, 6)
                .frame(maxWidth: .infinity)

            Rectangle()
                .fill(Theme.stroke)
                .frame(height: Stroke.hairline)

            HStack(spacing: 0) {
                art
                    .frame(width: Metrics.onboardingArtWidth)
                    .frame(maxHeight: .infinity)
                // In light appearance the aurora fades to almost the same grey
                // as the content pane by the bottom of the column, and the two
                // halves of the window ran together. One hairline is cheaper
                // than making the gradient louder.
                Rectangle()
                    .fill(Theme.stroke)
                    .frame(width: Stroke.hairline)
                pane
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            }
        }
        // The design's width, and only its width: the height is whatever the
        // step showing asks for. It has to be — the entry roster has grown past
        // the height the design was drawn at, and a fixed one clipped the
        // footer button off the bottom of step one. The window follows this
        // view's own size.
        .frame(width: Metrics.onboardingWidth)
        .background(Theme.raised)
        .onAppear {
            authorization.refresh()
            probe.refresh()
            preferences.onboardingStep = step.rawValue
        }
        .onChange(of: step) { _, new in preferences.onboardingStep = new.rawValue }
        // The permission is granted, and the entries can be switched, in System
        // Settings — in another process, while this window waits. Both have to
        // be re-read the moment the user comes back.
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            authorization.refresh()
            probe.refresh()
        }
        .animation(Motion.reduced(.easeInOut(duration: 0.22)), value: step)
    }

    // MARK: - Art column

    private var art: some View {
        ZStack {
            AuroraBackdrop()
            Group {
                switch step {
                case .entries: ShareMenuArt()
                case .permissions: PermissionArt()
                // The same menu step 1 promised, now as the thing to go and
                // look for. It is the one picture the last step's sentence is
                // actually about.
                case .done: ShareMenuArt()
                }
            }
            .frame(width: Metrics.onboardingArtContentWidth)
        }
    }

    // MARK: - Steps

    private var pane: some View {
        VStack(alignment: .leading, spacing: 0) {
            switch step {
            case .entries: entriesStep
            case .permissions: permissionsStep
            case .done: doneStep
            }
            // Every step but the last is read top-down. The last one is a full
            // stop, and a full stop belongs in the middle of the pane rather
            // than pinned under a step bar it no longer belongs to.
            if step != .done { Spacer(minLength: 0) }
        }
        .padding(.horizontal, 40)
        .padding(.vertical, 28)
    }

    private var entriesStep: some View {
        VStack(alignment: .leading, spacing: 0) {
            StepTitle(
                title: L10n.text("把微信的转发菜单接到你要去的地方"),
                subtitle: L10n.text("把微信里的聊天记录压缩包接住，再交给目标 App。")
            )

            ShareEntryList(probe: probe, spacing: Space.m, carded: false)
                .padding(.top, 20)

            Text(L10n.text("以后随时能在设置 → 入口 里改。"))
                .font(Typo.paneCaption)
                .foregroundStyle(Theme.inkTertiary)
                .padding(.top, 14)

            StepFooter(next: (L10n.text("继续"), { step = .permissions }))
                .padding(.top, 20)
        }
    }

    private var permissionsStep: some View {
        VStack(alignment: .leading, spacing: 0) {
            StepTitle(title: L10n.text("让 WeChatBridge 代你按 ⌘V"))

            ChecklistRow(
                done: authorization.isTrusted,
                title: L10n.text("辅助功能"),
                detail: L10n.text("用于激活目标应用并粘贴；只复制不需要。")
            ) {
                if !authorization.isTrusted {
                    Button(L10n.text("引导授权")) { authorization.guideIfNeeded() }
                        .buttonStyle(GhostButtonStyle())
                }
            }
            .padding(.top, 26)

            Text(L10n.text("可以先跳过，第一次转发失败时 WeChatBridge 会再引导一次。"))
                .font(Typo.paneCaption)
                .foregroundStyle(Theme.inkTertiary)
                .padding(.top, 14)

            StepFooter(
                back: (L10n.text("上一步"), { step = .entries }),
                // The button says what pressing it means. "继续" over an
                // unfinished permission would read as "and that's handled".
                next: (
                    authorization.isTrusted ? L10n.text("继续") : L10n.text("稍后再说"),
                    { step = .done }
                )
            )
            .padding(.top, 30)
        }
    }

    private var doneStep: some View {
        VStack(alignment: .leading, spacing: 0) {
            Spacer(minLength: 0)

            ZStack {
                Circle()
                    .fill(Theme.positiveSoft)
                    .frame(width: 64, height: 64)
                Image(systemName: "checkmark")
                    .font(.system(size: 26, weight: .bold))
                    .foregroundStyle(Theme.accent)
            }
            .accessibilityHidden(true)

            Text(L10n.text("好了"))
                .font(.system(size: 40, weight: .bold))
                .foregroundStyle(Theme.ink)
                .padding(.top, 24)

            Text(L10n.text("去微信多选聊天记录 → 转发到其他应用，就能看到这些入口了。"))
                .font(.system(size: 15))
                .foregroundStyle(Theme.inkSecondary)
                .frame(maxWidth: 520, alignment: .leading)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.top, 12)

            Button(L10n.text("完成")) { finish() }
                .buttonStyle(InkButtonStyle())
                .keyboardShortcut(.defaultAction)
                .padding(.top, 34)

            Spacer(minLength: 0)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
    }

}

// MARK: - Step furniture

private struct StepTitle: View {
    let title: String
    /// Absent where the step's own content already says it: a subtitle that
    /// paraphrases the line under it is one more thing to read, not one more
    /// thing to know.
    var subtitle: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title)
                // Wraps rather than being pinned to one line: the English copy
                // is half again as long as the Chinese, and a title that has to
                // fit one line is a title written to fit one language.
                .font(.system(size: 27, weight: .bold))
                .foregroundStyle(Theme.ink)
                .fixedSize(horizontal: false, vertical: true)
            if let subtitle {
                Text(subtitle)
                    .font(.system(size: 14))
                    .foregroundStyle(Theme.inkSecondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .frame(maxWidth: 500, alignment: .leading)
    }
}

/// Back on the left, forward on the right, Return always on forward.
private struct StepFooter: View {
    var back: (title: String, action: () -> Void)?
    let next: (title: String, action: () -> Void)

    var body: some View {
        HStack(spacing: Space.m) {
            if let back {
                Button(back.title, action: back.action)
                    .buttonStyle(GhostButtonStyle())
            }
            Button(next.title, action: next.action)
                .buttonStyle(InkButtonStyle())
                .keyboardShortcut(.defaultAction)
        }
    }
}

// MARK: - Art

/// What the Share menu will look like once the switches above are on. Drawn
/// rather than screenshotted, because a screenshot of someone else's menu ages
/// the moment they restyle it.
private struct ShareMenuArt: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(L10n.text("转发到其他应用"))
                .font(Typo.captionStrong)
                .foregroundStyle(Theme.inkTertiary)
                .padding(.horizontal, 12)
                .padding(.bottom, 10)

            ForEach(ShareAction.allCases, id: \.self) { action in
                HStack(spacing: 10) {
                    Image(systemName: ShareEntryList.symbol(for: action))
                        .font(.system(size: 12, weight: .medium))
                        .foregroundStyle(Theme.accent)
                        .frame(width: 16)
                    Text(action.entryTitle)
                        .font(Typo.paneBody)
                        .foregroundStyle(Theme.ink)
                        .lineLimit(1)
                    Spacer(minLength: 0)
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 7)
            }
        }
        .padding(.vertical, 14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Theme.surface, in: RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: Radius.card, style: .continuous)
                .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
        )
        .shadow(color: .black.opacity(0.08), radius: 16, y: 6)
        .accessibilityHidden(true)
    }
}

/// The pitfall of this particular permission, said once, beside the row that
/// asks for it: macOS never prompts for Accessibility, so a user waiting for a
/// dialog waits forever.
private struct PermissionArt: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Image(systemName: "hand.raised.slash")
                .font(.system(size: 20, weight: .medium))
                .foregroundStyle(Theme.accent)
            Text(L10n.text("辅助功能不会弹系统授权框，需要把 WeChatBridge 拖进列表；授权后立即生效，不必重启。"))
                .font(.system(size: 13))
                .foregroundStyle(Theme.inkSecondary)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(20)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(Theme.surface, in: RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: Radius.card, style: .continuous)
                .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
        )
        .shadow(color: .black.opacity(0.08), radius: 16, y: 6)
        .accessibilityHidden(true)
    }
}
