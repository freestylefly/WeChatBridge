import XCTest
@testable import WeChatBridgeCore

/// A 1440×900 screen with the menu bar taken off the top, which is what
/// `NSScreen.visibleFrame` hands the app.
private let screenRect: CGRect = CGRect(x: 0, y: 0, width: 1440, height: 875)
private let panelSize: CGSize = CGSize(width: 260, height: 140)

final class PointerPlacementTests: XCTestCase {
    func testHangsBelowRightOfThePointer() {
        let frame: CGRect = PointerPlacement.frame(size: panelSize, pointer: CGPoint(x: 400, y: 600), in: screenRect)
        XCTAssertEqual(frame.minX, 410)
        XCTAssertEqual(frame.maxY, 590)
    }

    func testFlipsLeftRatherThanRunningOffTheRightEdge() {
        let frame: CGRect = PointerPlacement.frame(size: panelSize, pointer: CGPoint(x: 1400, y: 600), in: screenRect)
        XCTAssertEqual(frame.maxX, 1390)
        XCTAssertLessThanOrEqual(frame.maxX, screenRect.maxX - 12)
    }

    func testFlipsAboveRatherThanSittingUnderThePointer() {
        let frame: CGRect = PointerPlacement.frame(size: panelSize, pointer: CGPoint(x: 400, y: 60), in: screenRect)
        XCTAssertEqual(frame.minY, 70)
    }

    /// A second display to the left has negative coordinates; the panel has to
    /// stay on it rather than being pulled back towards the origin.
    func testStaysOnTheScreenThePointerIsOn() {
        let left = CGRect(x: -1920, y: -200, width: 1920, height: 1080)
        let frame: CGRect = PointerPlacement.frame(size: panelSize, pointer: CGPoint(x: -1000, y: 500), in: left)
        XCTAssertEqual(frame.minX, -990)
        XCTAssertTrue(left.contains(frame))
    }

    /// Neither side fits: clamping has the last word, and the whole panel is
    /// still on screen.
    func testClampsWhenNeitherSideFits() {
        let narrow = CGRect(x: 0, y: 0, width: 300, height: 200)
        let frame: CGRect = PointerPlacement.frame(size: panelSize, pointer: CGPoint(x: 290, y: 10), in: narrow)
        XCTAssertTrue(narrow.insetBy(dx: 12, dy: 12).contains(frame))
    }
}
