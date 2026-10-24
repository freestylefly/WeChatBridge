import ActivityKit
import Foundation

/// Starts and steers the delivery Live Activity. Only one is up at a time: a
/// new delivery replaces the last one's.
@MainActor
enum DeliveryActivityController {
    /// Started just before the share sheet, while the app is still in front
    /// (ActivityKit refuses a request from the background).
    static func start(
        batchID: UUID,
        title: String,
        detail: String,
        target: TargetProfile,
        prompt: String,
        enabled: Bool
    ) {
        guard enabled, ActivityAuthorizationInfo().areActivitiesEnabled else { return }
        endAll()
        UserDefaults.standard.set(prompt, forKey: DeliveryActivityPrompt.key)
        let attributes = DeliveryActivityAttributes(
            batchID: batchID.uuidString,
            title: title,
            detail: detail,
            targetID: target.id,
            targetName: target.displayName
        )
        let state = DeliveryActivityAttributes.ContentState(phase: .choosing, hasPrompt: !prompt.isEmpty)
        _ = try? Activity.request(attributes: attributes, content: content(state), pushType: nil)
    }

    static func markSent() {
        for activity in Activity<DeliveryActivityAttributes>.activities {
            var state = activity.content.state
            state.phase = .sent
            Task { await activity.update(content(state)) }
        }
    }

    /// The share sheet was cancelled: nothing went out, nothing to come back to.
    static func cancel() {
        endAll()
    }

    static func endAll() {
        UserDefaults.standard.removeObject(forKey: DeliveryActivityPrompt.key)
        for activity in Activity<DeliveryActivityAttributes>.activities {
            Task { await activity.end(nil, dismissalPolicy: .immediate) }
        }
    }

    /// Greyed after an hour; by then the hand-off is long done.
    private static func content(_ state: DeliveryActivityAttributes.ContentState) -> ActivityContent<DeliveryActivityAttributes.ContentState> {
        ActivityContent(state: state, staleDate: Date().addingTimeInterval(60 * 60))
    }
}
