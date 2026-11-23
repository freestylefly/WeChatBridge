import AppKit
import SwiftUI
import XCTest
@testable import WeChatBridgeApp

final class ScenePromptTextTests: XCTestCase {
    @MainActor
    func testLongChinesePreviewIsBoundedReadOnlyAndSelectable() async throws {
        let text = String(repeating: "只保留已确认的事实、决定与待办。\n", count: 800)
        let (_, textView) = try await showPreview(text)
        let scroll = try XCTUnwrap(textView.enclosingScrollView)
        XCTAssertEqual(textView.string, text)
        XCTAssertFalse(textView.isEditable)
        XCTAssertTrue(textView.isSelectable)
        XCTAssertNotNil(textView.textLayoutManager)
        XCTAssertEqual(scroll.frame.height, 200, accuracy: 1)
        XCTAssertGreaterThan(textView.frame.width, 0)
        XCTAssertTrue(textView.textContainer?.widthTracksTextView == true)
    }

    @MainActor
    func testResizingKeepsTheViewportBoundedAndTheFullText() async throws {
        let text = String(repeating: "中文与 emoji 🧑🏽‍💻，保留末尾要求。\n", count: 800) + "这是最后一行。"
        let (host, textView) = try await showPreview(text)
        let scroll = try XCTUnwrap(textView.enclosingScrollView)
        for width in [CGFloat(380), CGFloat(900)] {
            host.setFrameSize(NSSize(width: width, height: 200))
            host.layoutSubtreeIfNeeded()
            XCTAssertEqual(scroll.frame.height, 200, accuracy: 1)
            XCTAssertEqual(textView.frame.width, scroll.contentSize.width, accuracy: 1)
            XCTAssertEqual(textView.string, text)
            XCTAssertFalse(scroll.hasHorizontalScroller)
        }
    }

    @MainActor
    func testUnchangedRefreshPreservesSelectionAndNewTextReplacesPreview() async throws {
        let text = String(repeating: "保留选区与完整预览。\n", count: 80)
        let (host, textView) = try await showPreview(text)
        let selection = NSRange(location: 1, length: 6)
        textView.setSelectedRange(selection)
        host.rootView = ScenePromptText(text: text, height: 200, label: "转发提示词预览")
        try await Task.sleep(nanoseconds: 100_000_000)
        host.layoutSubtreeIfNeeded()
        XCTAssertEqual(textView.selectedRange(), selection)
        host.rootView = ScenePromptText(text: "新场景的完整提示词", height: 200, label: "转发提示词预览")
        try await Task.sleep(nanoseconds: 100_000_000)
        host.layoutSubtreeIfNeeded()
        XCTAssertEqual(textView.string, "新场景的完整提示词")
        XCTAssertEqual(textView.enclosingScrollView?.frame.height, 200)
    }

    @MainActor
    private func showPreview(_ text: String) async throws -> (NSHostingView<ScenePromptText>, NSTextView) {
        _ = NSApplication.shared
        let host = NSHostingView(rootView: ScenePromptText(text: text, height: 200, label: "转发提示词预览"))
        // 与设置窗口一致，由外层窗口给定尺寸，而不是用内容的理想尺寸收窄窗口。
        host.sizingOptions = []
        host.setFrameSize(NSSize(width: 600, height: 200))
        let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 600, height: 200), styleMask: [.titled], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        window.contentView = host
        addTeardownBlock { await MainActor.run { window.close() } }
        window.orderFront(nil)
        try await Task.sleep(nanoseconds: 100_000_000)
        host.layoutSubtreeIfNeeded()
        var pending = [host as NSView]
        var textView: NSTextView?
        while let view = pending.popLast() {
            pending.append(contentsOf: view.subviews)
            if let preview = view as? NSTextView { textView = preview }
        }
        return (host, try XCTUnwrap(textView))
    }
}
