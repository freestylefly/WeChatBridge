import AppKit
import SwiftUI

extension Color {
    /// A dynamic colour is one `NSColor` with two appearance branches. SwiftUI
    /// has no `Color(light:dark:)`, and a SwiftPM executable has no asset
    /// catalogue, so this is the only way to get real light/dark tokens here.
    /// Resolving inside the `NSColor` also means an appearance switch redraws
    /// without any view having to be invalidated.
    static func dynamic(light: UInt32, dark: UInt32) -> Color {
        dynamic(light: light, lightAlpha: 1, dark: dark, darkAlpha: 1)
    }

    /// The alpha-carrying form. Light strokes are alpha so they recede; dark
    /// strokes are solid, because an alpha line glows against a dark ground.
    static func dynamic(light: UInt32, lightAlpha: Double, dark: UInt32, darkAlpha: Double) -> Color {
        Color(nsColor: NSColor(name: nil) { appearance in
            let isDark = appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
            let hex = isDark ? dark : light
            return NSColor(
                srgbRed: Double((hex >> 16) & 0xFF) / 255,
                green: Double((hex >> 8) & 0xFF) / 255,
                blue: Double(hex & 0xFF) / 255,
                alpha: isDark ? darkAlpha : lightAlpha
            )
        })
    }
}

/// The floating surfaces — toast and permission card — borrow the system's own
/// materials instead of inventing a palette, because they sit over other
/// people's windows. Only what a material cannot provide is defined here:
/// hairlines, row states, and the colours that have to stay legible on both
/// grounds.
enum Palette {
    static let hairline    = Color.dynamic(light: 0x000000, lightAlpha: 0.10, dark: 0xFFFFFF, darkAlpha: 0.12)
    static let rowHover    = Color.dynamic(light: 0x000000, lightAlpha: 0.05, dark: 0xFFFFFF, darkAlpha: 0.07)
    static let rowSelected = Theme.brandTint

    /// `.red` measures 3.1:1 on a dark material and fails at 11 pt. These are
    /// picked per appearance to clear 4.5:1 on both. There is no green here:
    /// the floating surfaces only ever report failures now, so a success colour
    /// would be a token nothing is allowed to use.
    static let danger      = Theme.danger
    static let warning     = Theme.warning
}

/// The opaque surfaces: the settings window, which is WeChatBridge's only real window
/// and therefore the only place that paints its own light instead of borrowing
/// the desktop's.
///
/// Every colour here is one declaration with two branches, so a pane never has
/// to read `@Environment(\.colorScheme)` to look right in the dark.
enum Theme {

    // MARK: - Brand

    static let brandPrimary = Color.dynamic(light: 0x14C653, dark: 0x14C653)
    static let brandHover   = Color.dynamic(light: 0x18D45B, dark: 0x18D45B)
    static let brandPressed = Color.dynamic(light: 0x0FA845, dark: 0x0FA845)
    static let brandSoft    = Color.dynamic(light: 0xE9FFF2, dark: 0x123323)
    static let brandTint    = Color.dynamic(light: 0x14C653, lightAlpha: 0.12, dark: 0x14C653, darkAlpha: 0.18)
    static let brandGlow    = Color.dynamic(light: 0x14C653, lightAlpha: 0.18, dark: 0x14C653, darkAlpha: 0.20)
    static let onBrand      = Color.white

    // MARK: - Surfaces

    static let background      = Color.dynamic(light: 0xF8FAF9, dark: 0x111312)
    static let surface         = Color.dynamic(light: 0xFFFFFF, dark: 0x1B1E1C)
    static let surfaceSecondary = Color.dynamic(light: 0xF2F5F3, dark: 0x242825)
    static let hover           = Color.dynamic(light: 0xF4F7F5, dark: 0x2A2F2C)
    static let selected        = Color.dynamic(light: 0xE8EEEA, dark: 0x303632)
    static let choiceDivider   = Color.dynamic(light: 0xD9DFDB, dark: 0x4A524C)

    // MARK: - Lines

    static let border       = Color.dynamic(light: 0xE5EAE7, dark: 0x343A36)
    static let borderStrong = Color.dynamic(light: 0xD8DEDA, dark: 0x464D48)
    static let buttonBorder = Color.dynamic(light: 0xDFE4E1, dark: 0x414843)
    /// Editable fields need a readable boundary against their fill (over 3:1).
    static let inputStroke = Color.dynamic(light: 0x8C948F, dark: 0x777E79)

    // MARK: - Ink

    static let textPrimary    = Color.dynamic(light: 0x1D1D1F, dark: 0xF5F5F7)
    static let textSecondary  = Color.dynamic(light: 0x687078, dark: 0xA5ABB0)
    static let textTertiary   = Color.dynamic(light: 0x9AA1A6, dark: 0x747A7E)
    static let disabled       = Color.dynamic(light: 0xC8CECB, dark: 0x555B58)
    static let secondaryActionText = Color.dynamic(light: 0x4A5055, dark: 0xD7DCD9)

    // MARK: - Semantic

    static let systemBlue = Color.dynamic(light: 0x0A84FF, dark: 0x0A84FF)
    static let warning    = Color.dynamic(light: 0xFF9F0A, dark: 0xFF9F0A)
    static let warningSoft = Color.dynamic(light: 0xFFF4E5, dark: 0x35270F)
    static let danger     = Color.dynamic(light: 0xFF453A, dark: 0xFF453A)
    static let dangerSoft = Color.dynamic(light: 0xFFF0EE, dark: 0x351A18)

    // MARK: - Compatibility aliases

    /// The green of the app icon remains the one identity colour.
    static let accent = brandPrimary
    static let accentSoft = brandSoft
    static let controlOn = brandPrimary
    static let positive = brandPrimary
    static let positiveSoft = brandSoft

    static let sunken = surfaceSecondary
    static let raised = background
    static let stroke = border
    static let strokeStrong = borderStrong
    static let ink = textPrimary
    static let inkSecondary = textSecondary
    static let inkTertiary = textTertiary
}

enum Space {
    static let xxs: CGFloat = 2, xs: CGFloat = 4, s: CGFloat = 8, m: CGFloat = 12
    static let l: CGFloat = 16, xl: CGFloat = 20, xxl: CGFloat = 28
    /// Between two settings sections. Wide enough that the section labels, not
    /// a rule, are what separates them.
    static let section: CGFloat = 24
}

enum Radius {
    /// Notices, fields, navigation items — anything the size of one row.
    static let control: CGFloat = 10
    static let row: CGFloat = 8
    static let card: CGFloat = 12
    /// A floating panel that holds a list rather than a sentence: the target
    /// picker.
    static let panel: CGFloat = 14
}

enum Stroke {
    static let hairline: CGFloat = 1, focus: CGFloat = 2
}

// MARK: - Type
//
// Two faces, split by script, never mixed inside one Text. `.rounded` is a no-op
// on Chinese glyphs — CJK falls back to the PingFang UI cut with identical
// metrics — so a rounded font on a mixed string silently splits its personality.

extension Font {
    /// Numerals, byte counts and clock times. Tabular figures keep a row from
    /// reflowing as the value changes.
    static func numeral(_ size: CGFloat, _ weight: Font.Weight = .semibold) -> Font {
        .system(size: size, weight: weight, design: .rounded).monospacedDigit()
    }

    /// Chinese and Latin UI text. Chinese weight mapping breaks above
    /// `.semibold`, so nothing here goes heavier except the one page title,
    /// which is Latin-led and large enough to carry it.
    static func ui(_ size: CGFloat, _ weight: Font.Weight = .regular) -> Font {
        .system(size: size, weight: weight)
    }
}

/// Declared as its own namespace rather than as `Font` statics: `title`, `body`
/// and `caption` already exist on `Font`, and redeclaring them does not compile.
///
/// The settings window has 780 pt to itself and reads at the distance a document
/// does.
enum Typo {
    static let title = Font.ui(20, .semibold)
    static let heading = Font.ui(13, .semibold)
    static let body = Font.ui(12)
    static let label = Font.ui(12, .medium)
    static let caption = Font.ui(11)
    static let captionStrong = Font.ui(11, .medium)
    static let micro = Font.ui(10.5)

    /// The settings window.
    static let pageTitle = Font.ui(26, .bold)
    static let paneTitle = Font.ui(21, .semibold)
    static let sectionLabel = Font.ui(12.5, .semibold)
    static let rowTitle = Font.ui(15, .semibold)
    static let paneBody = Font.ui(13.5)
    static let paneBodyStrong = Font.ui(13.5, .medium)
    static let paneCaption = Font.ui(12)
}

enum Metrics {
    /// Distance from the screen's `visibleFrame` to the toast.
    static let screenMargin: CGFloat = 12

    /// Wide enough for a sentence about a failure, narrow enough that it never
    /// reads as a dialog.
    static let toastMaxWidth: CGFloat = 360
    static let toastMinHeight: CGFloat = 36
    /// Gap between a floating panel and its anchor.
    static let toastGap: CGFloat = 10
    /// How far above its resting place a toast starts.
    static let toastDrop: CGFloat = 6

    /// The settings window's design size, and now also its floor: the window may
    /// grow past it, but not shrink below it.
    ///
    /// The height is what 通用 — the pane every launch lands on — actually
    /// occupies at this width, measured rather than guessed: at 560 the last
    /// card of that pane sat under the window's edge, and the row a user
    /// reaches for when the guide needs re-running was the one below the fold.
    /// The list panes scroll past this, which is what a list is for.
    static let settingsWidth: CGFloat = 780
    static let settingsHeight: CGFloat = 640
    static let settingsNavWidth: CGFloat = 176
    /// The navigation column runs to the top of the window and the traffic
    /// lights are drawn over it, so the first item starts below them.
    static let settingsTrafficLightInset: CGFloat = 38

    /// The first-run guide. Wider than the settings window because it carries an
    /// illustration beside each step, and read once, at full attention.
    ///
    /// The width is the guide's; the height is the size it was drawn at, and the
    /// frame its window opens from. Every step has to fit without scrolling — a
    /// guide the user has to scroll is a guide whose next button they cannot
    /// see — so the window follows the step's own layout rather than holding it
    /// to this number, which is not a thing a constant can keep up with.
    static let onboardingWidth: CGFloat = 920
    static let onboardingHeight: CGFloat = 600
    /// The art column on the left. The card inside it stops short of the edges
    /// so the aurora reads as a ground rather than as a border.
    static let onboardingArtWidth: CGFloat = 300
    static let onboardingArtContentWidth: CGFloat = 252
}

/// One curve for state, one set of durations for windows appearing.
///
/// Nothing here ever travels: a panel appears where it belongs and fades in on
/// the spot. A window that flies across the screen from the pointer to its
/// resting place is 300 ms of the user's attention spent on a journey that tells
/// them nothing, over an app they were in the middle of using.
enum Motion {
    static let ui = Animation.easeOut(duration: 0.16)

    /// A floating panel arriving: alpha with a 0.96 → 1 settle of its content.
    static let panelIn: TimeInterval = 0.18
    /// Leaving is faster than arriving — nobody watches a window go.
    static let panelOut: TimeInterval = 0.12
    /// One nudge when a toast is replaced while already on screen.
    static let bump: TimeInterval = 0.22
    static let toastIn: TimeInterval = 0.16
    static let toastOut: TimeInterval = 0.20
    /// Holding station with a window somebody else is dragging. Short enough
    /// that successive hops read as one continuous follow, and short enough not
    /// to count as an entrance.
    static let follow: TimeInterval = 0.18

    /// For AppKit code, which has no SwiftUI environment to read.
    static var systemReducesMotion: Bool {
        NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
    }

    /// Window animations collapse to nothing under reduce motion rather than
    /// shortening: a panel that snaps into place is the honest version of a
    /// panel that fades in, and there is no state left unexplained by it.
    static func duration(_ seconds: TimeInterval) -> TimeInterval {
        systemReducesMotion ? 0 : seconds
    }

    /// Reduced motion never means "no feedback": the change still has to be
    /// legible, it just must not spring.
    static func reduced(_ animation: Animation, _ reduce: Bool = systemReducesMotion) -> Animation {
        reduce ? .easeOut(duration: 0.12) : animation
    }
}
