import AppKit
import SwiftUI
import WeChatBridgeCore
import XCTest
@testable import WeChatBridgeApp

final class SceneSettingsViewTests: XCTestCase {
    @MainActor
    func testLongChinesePromptUsesABoundedScrollableEditor() async throws {
        _ = NSApplication.shared
        let domain = "scene-editor-tests-" + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        defer { defaults.removePersistentDomain(forName: domain) }
        let preferences = Preferences(defaults: defaults)
        let prompt = String(repeating: "只整理已确认的事实、决定和待办；保留负责人及截止时间，未明确的信息标为待确认。\n", count: 80)
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(domain)
        let skills = SkillLibrary(resourcesRoot: nil, store: SkillStore(root: root, configDirectory: root))
        preferences.scenes = SceneSettings(scenes: [WeChatScene(id: "long-prompt", name: "长提示词", instruction: prompt, outputSpec: "", enabled: true)])
        let host = NSHostingView(rootView: ScrollView { SceneSettingsView(preferences: preferences, skills: skills, router: SettingsRouter()) }.frame(width: 1100, height: 700))
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1100, height: 700), styleMask: [.titled], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        defer { window.close() }
        window.orderFront(nil)
        try await Task.sleep(nanoseconds: 100_000_000)
        host.layoutSubtreeIfNeeded()
        var pending = [host as NSView]
        var textViews: [NSTextView] = []
        while let view = pending.popLast() {
            pending.append(contentsOf: view.subviews)
            if let textView = view as? NSTextView { textViews.append(textView) }
        }
        let editor = try XCTUnwrap(textViews.first { $0.isEditable && $0.string == prompt })
        let scroll = try XCTUnwrap(editor.enclosingScrollView)
        XCTAssertGreaterThan(scroll.frame.height, 0)
        XCTAssertLessThanOrEqual(scroll.frame.height, 160)
        XCTAssertGreaterThan(editor.bounds.height, scroll.contentSize.height)
    }
}
