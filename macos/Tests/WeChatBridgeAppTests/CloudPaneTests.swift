import AppKit
import SwiftUI
import XCTest
@testable import WeChatBridgeApp
import WeChatBridgeCore

final class CloudPaneTests: XCTestCase {
    @MainActor
    func testLoginPaneRendersWithoutAccessingRealCredentials() throws {
        guard let output = ProcessInfo.processInfo.environment["WCB_CLOUD_UI_SNAPSHOT"] else {
            throw XCTSkip("Set WCB_CLOUD_UI_SNAPSHOT to render the native account pane.")
        }
        _ = NSApplication.shared
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let library = SkillLibrary(resourcesRoot: nil, store: SkillStore(root: root, configDirectory: root))
        let account = CloudAccount(loadSavedAccount: false)
        let view = NSHostingView(rootView: CloudPane(skills: library, account: account).padding(32).frame(width: 860, height: 540, alignment: .topLeading).background(Color.white))
        view.frame = NSRect(x: 0, y: 0, width: 860, height: 540)
        view.layoutSubtreeIfNeeded()
        let bitmap = try XCTUnwrap(view.bitmapImageRepForCachingDisplay(in: view.bounds))
        view.cacheDisplay(in: view.bounds, to: bitmap)
        let png = try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
        XCTAssertGreaterThan(png.count, 5000)
        try png.write(to: URL(fileURLWithPath: output))
        XCTAssertFalse(account.signedIn)
    }
}
