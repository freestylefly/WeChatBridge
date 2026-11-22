import AppKit
import SwiftUI
import XCTest
@testable import WeChatBridgeApp

/// Issue #54: on macOS 14 the first-run guide's window and its content fed
/// each other's height until AppKit threw and the app exited at launch. The
/// guide's size must depend on the step showing, never on the size offered.
final class OnboardingFrameTests: XCTestCase {
    /// A stand-in with the guide's shape: content of a fixed height inside a
    /// pane that stretches with `maxHeight: .infinity`.
    private func stretchingContent(height: CGFloat) -> some View {
        VStack(spacing: 0) {
            Color.clear.frame(height: height)
            Spacer(minLength: 0)
        }
        .frame(maxHeight: .infinity)
    }

    @MainActor
    func testTallStepKeepsItsOwnHeightWhateverItIsOffered() {
        let host = NSHostingController(rootView: OnboardingFrame(width: 920, minHeight: 600) {
            stretchingContent(height: 740)
        })
        for offered in [300.0, 600, 740, 2000] {
            let size = host.sizeThatFits(in: CGSize(width: 920, height: offered))
            XCTAssertEqual(size.width, 920, "offered \(offered)")
            XCTAssertEqual(size.height, 740, "offered \(offered)")
        }
    }

    @MainActor
    func testShortStepFillsTheDesignHeight() {
        let host = NSHostingController(rootView: OnboardingFrame(width: 920, minHeight: 600) {
            stretchingContent(height: 420)
        })
        for offered in [300.0, 600, 2000] {
            XCTAssertEqual(host.sizeThatFits(in: CGSize(width: 920, height: offered)).height, 600, "offered \(offered)")
        }
    }

    /// The shape the guide had before: its height follows the offer, which is
    /// the feedback the window was looping on.
    @MainActor
    func testTheOldFrameFollowedTheOfferedHeight() {
        let host = NSHostingController(rootView: stretchingContent(height: 420).frame(width: 920))
        let small = host.sizeThatFits(in: CGSize(width: 920, height: 600)).height
        let large = host.sizeThatFits(in: CGSize(width: 920, height: 2000)).height
        XCTAssertNotEqual(small, large)
    }
}
