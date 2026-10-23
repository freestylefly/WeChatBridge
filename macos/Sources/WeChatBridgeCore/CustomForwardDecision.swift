import Foundation

/// What 「发送到自定义」 should do with the list of apps the user has added.
///
/// The share extension used to answer this itself, on screen, inside the host's
/// share sheet — which is exactly the panel the user asked to be rid of
/// (2026-09-05 evening). The decision moved into the app, and it is a pure
/// function of the list so that it can be tested without a window, a share
/// sheet or an app group: `ActionRunner` is `@MainActor` AppKit code with no
/// test target of its own, and the branch that matters is this one.
///
/// One app is not a choice. Presenting a picker with a single row would be
/// WeChatBridge asking a question whose answer it already has — the user's own
/// requirement, and the reason `.single` exists rather than `.choose([one])`.
public enum CustomForwardDecision: Sendable, Equatable {
    /// Nothing to send to. The app says so and offers to open 设置 → 入口.
    case none
    /// Exactly one app: forward to it straight away, no interface at all.
    case single(ForwardTarget)
    /// Two or more: ask, with the list in the order it was given.
    case choose([ForwardTarget])

    /// `targets` arrives in display order — last used first, then the order the
    /// user arranged in 设置 → 入口 — and that order is preserved, because the
    /// panel's first row is the one Return picks.
    public static func decide(targets: [ForwardTarget]) -> CustomForwardDecision {
        switch targets.count {
        case 0: return .none
        case 1: return .single(targets[0])
        default: return .choose(targets)
        }
    }
}
