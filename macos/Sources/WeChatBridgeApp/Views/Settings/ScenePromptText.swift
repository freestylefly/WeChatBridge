import AppKit
import SwiftUI

/// 只读提示词保留完整文本与选择复制；视口尺寸不依赖全文排版结果。
struct ScenePromptText: NSViewRepresentable {
    let text: String
    let height: CGFloat
    var fontSize: CGFloat = 12
    let label: String

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSScrollView()
        scroll.drawsBackground = false
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        let textView = NSTextView(usingTextLayoutManager: true)
        textView.isEditable = false
        textView.isSelectable = true
        textView.isRichText = false
        textView.drawsBackground = false
        textView.isVerticallyResizable = true
        textView.isHorizontallyResizable = false
        textView.autoresizingMask = [.width]
        textView.minSize = NSSize(width: 0, height: height)
        textView.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        textView.textContainer?.size = NSSize(width: 0, height: CGFloat.greatestFiniteMagnitude)
        textView.textContainer?.widthTracksTextView = true
        textView.textContainer?.lineFragmentPadding = 0
        textView.textContainerInset = NSSize(width: 10, height: 8)
        scroll.documentView = textView
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard let textView = scroll.documentView as? NSTextView else { return }
        let font = NSFont.systemFont(ofSize: fontSize)
        let color = NSColor(Theme.inkSecondary)
        if textView.font != font { textView.font = font }
        if textView.textColor != color { textView.textColor = color }
        textView.setAccessibilityLabel(label)
        // 焦点或悬停刷新不应重置选区，也不应重新设置整段长文本。
        if textView.string != text { textView.string = text }
    }

    func sizeThatFits(_ proposal: ProposedViewSize, nsView: NSScrollView, context: Context) -> CGSize? {
        let width = proposal.width ?? 0
        return CGSize(width: width.isFinite ? width : 0, height: height)
    }
}
