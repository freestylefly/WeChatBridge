import AppKit
import SwiftUI
import XCTest
@testable import WeChatBridgeApp

final class ScenePromptTextViewTests: XCTestCase {
    @MainActor
    func testSwiftUIEditorAndPreviewStayBoundedInsideAnOuterScrollView() {
        _ = NSApplication.shared
        let prompt = String(repeating: "用于布局回归的长提示词。\n", count: 2_000)
        let host = NSHostingView(rootView: ScrollView {
            VStack {
                ScenePromptTextView(text: .constant(prompt)).frame(height: 160)
                ScenePromptTextView(text: .constant(prompt), isEditable: false).frame(height: 160)
            }
        })
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 320, height: 400),
                              styleMask: [.titled], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        defer { window.close() }
        host.layoutSubtreeIfNeeded()

        let views = promptViews(in: host)
        XCTAssertEqual(views.count, 2)
        for view in views {
            XCTAssertEqual(view.frame.height, 160, accuracy: 1)
            XCTAssertGreaterThan(view.frame.width, 0)
            XCTAssertEqual(view.textView.string, prompt)
        }
    }

    @MainActor
    func testLongPromptScrollsWithoutGrowingTheViewportOrLosingText() throws {
        let prompt = String(repeating: "测试段落：保留中文、emoji 🧪 和全部换行。\n", count: 2_000)
        let view = ScenePromptScrollView(frame: NSRect(x: 0, y: 0, width: 320, height: 160))
        view.update(text: prompt, isEditable: true)
        view.layoutSubtreeIfNeeded()
        let container = try XCTUnwrap(view.textView.textContainer)
        let layout = try XCTUnwrap(view.textView.layoutManager)
        layout.ensureLayout(for: container)

        XCTAssertEqual(view.textView.string, prompt)
        XCTAssertEqual(view.frame.height, 160)
        XCTAssertGreaterThan(layout.usedRect(for: container).height, view.contentSize.height)
        XCTAssertTrue(view.hasVerticalScroller)
        XCTAssertFalse(view.hasHorizontalScroller)
        XCTAssertTrue(container.widthTracksTextView)

        view.textView.scrollRangeToVisible(NSRange(location: (prompt as NSString).length, length: 0))
        XCTAssertGreaterThan(view.contentView.bounds.origin.y, 0)
        XCTAssertEqual(view.frame.height, 160)
    }

    @MainActor
    func testPreviewKeepsFullTextSelectableAndReadOnly() {
        let prompt = String(repeating: "预览内容\n", count: 2_000)
        let view = ScenePromptScrollView(frame: NSRect(x: 0, y: 0, width: 320, height: 160))
        view.update(text: prompt, isEditable: false)

        XCTAssertEqual(view.textView.string, prompt)
        XCTAssertFalse(view.textView.isEditable)
        XCTAssertTrue(view.textView.isSelectable)
        XCTAssertFalse(view.textView.isRichText)
    }

    @MainActor
    func testUnchangedUpdatePreservesSelectionAndScrollPosition() {
        let prompt = String(repeating: "测试段落\n", count: 2_000)
        let view = ScenePromptScrollView(frame: NSRect(x: 0, y: 0, width: 320, height: 160))
        view.update(text: prompt, isEditable: true)
        let selection = NSRange(location: (prompt as NSString).length - 5, length: 2)
        view.textView.setSelectedRange(selection)
        view.textView.scrollRangeToVisible(selection)
        let scrollPosition = view.contentView.bounds.origin

        view.update(text: prompt, isEditable: true)

        XCTAssertEqual(view.textView.selectedRange(), selection)
        XCTAssertEqual(view.contentView.bounds.origin, scrollPosition)
    }

    @MainActor
    func testEditingAndSkillInsertionUpdateTheBindingOncePerChange() {
        var prompt = "测试🧪内容"
        var changes = 0
        let binding = Binding(get: { prompt }, set: { prompt = $0; changes += 1 })
        let coordinator = ScenePromptTextView.Coordinator(text: binding)
        let view = ScenePromptScrollView(frame: NSRect(x: 0, y: 0, width: 320, height: 160))
        view.update(text: prompt, isEditable: true)
        view.textView.delegate = coordinator

        view.textView.insertText("{{skill:test-skill}}", replacementRange: NSRange(location: 2, length: 0))

        XCTAssertEqual(prompt, "测试{{skill:test-skill}}🧪内容")
        XCTAssertEqual(changes, 1)
        view.update(text: prompt, isEditable: true)
        XCTAssertEqual(changes, 1)
    }

    @MainActor
    func testExternalUpdateDoesNotReplaceInputMethodComposition() {
        let view = ScenePromptScrollView(frame: NSRect(x: 0, y: 0, width: 320, height: 160))
        view.update(text: "原文", isEditable: true)
        view.textView.setMarkedText("拼音", selectedRange: NSRange(location: 2, length: 0),
                                    replacementRange: NSRange(location: 2, length: 0))
        XCTAssertTrue(view.textView.hasMarkedText())

        view.update(text: "原文", isEditable: true)

        XCTAssertTrue(view.textView.hasMarkedText())
        XCTAssertEqual(view.textView.string, "原文拼音")
    }

    @MainActor
    private func promptViews(in view: NSView) -> [ScenePromptScrollView] {
        if let prompt = view as? ScenePromptScrollView { return [prompt] }
        return view.subviews.flatMap { promptViews(in: $0) }
    }
}
