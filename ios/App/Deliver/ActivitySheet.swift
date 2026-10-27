import UIKit

/// Presents the system share sheet from UIKit directly. Wrapped in a SwiftUI
/// `.sheet` it lands inside a second, mostly empty sheet; presented from the
/// top view controller it looks the way every other app's does.
enum ActivitySheet {
    struct Result {
        let completed: Bool
        /// Often the receiving extension's bundle id: kept in the delivery
        /// detail so the target table can be corrected from real use.
        let activityType: String?
    }

    @MainActor
    static func present(items: [Any], completion: @escaping (Result) -> Void) {
        guard let top = topViewController() else {
            completion(Result(completed: false, activityType: nil))
            return
        }
        let controller = UIActivityViewController(activityItems: items, applicationActivities: nil)
        controller.completionWithItemsHandler = { type, completed, _, _ in
            completion(Result(completed: completed, activityType: type?.rawValue))
        }
        controller.popoverPresentationController?.sourceView = top.view
        top.present(controller, animated: true)
    }

    @MainActor
    private static func topViewController() -> UIViewController? {
        let window = UIApplication.shared.connectedScenes
            .compactMap { $0 as? UIWindowScene }
            .flatMap(\.windows)
            .first(where: \.isKeyWindow)
        var top = window?.rootViewController
        while let presented = top?.presentedViewController, !presented.isBeingDismissed {
            top = presented
        }
        return top
    }
}
