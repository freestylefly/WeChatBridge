import AppKit
import SwiftUI
import WeChatBridgeCore

/// 将文本布局留在原生滚动视图内，避免长提示词参与整个设置页的高度计算。
struct ScenePromptTextView: NSViewRepresentable {
    @Binding var text: String
    var isEditable = true

    static let editorIdentifier = "scene.promptEditor"

    func makeCoordinator() -> Coordinator {
        Coordinator(text: $text)
    }

    func makeNSView(context: Context) -> ScenePromptScrollView {
        let view = ScenePromptScrollView(frame: NSRect(x: 0, y: 0, width: 320, height: 160))
        view.textView.delegate = context.coordinator
        configure(view)
        return view
    }

    func updateNSView(_ view: ScenePromptScrollView, context: Context) {
        context.coordinator.text = $text
        configure(view)
    }

    private func configure(_ view: ScenePromptScrollView) {
        view.update(text: text, isEditable: isEditable)
        view.textView.identifier = NSUserInterfaceItemIdentifier(
            isEditable ? Self.editorIdentifier : "scene.promptPreview"
        )
        view.textView.setAccessibilityLabel(
            L10n.text(isEditable ? "提示词" : "转发提示词预览")
        )
    }

    final class Coordinator: NSObject, NSTextViewDelegate {
        var text: Binding<String>

        init(text: Binding<String>) {
            self.text = text
        }

        func textDidChange(_ notification: Notification) {
            guard let view = notification.object as? NSTextView,
                  !view.hasMarkedText(),
                  text.wrappedValue != view.string
            else { return }
            text.wrappedValue = view.string
        }
    }
}

final class ScenePromptScrollView: NSScrollView {
    let textView = NSTextView(frame: .zero)

    override init(frame: NSRect) {
        super.init(frame: frame)
        borderType = .noBorder
        drawsBackground = false
        hasVerticalScroller = true
        hasHorizontalScroller = false
        autohidesScrollers = true

        textView.drawsBackground = false
        textView.isRichText = false
        textView.importsGraphics = false
        textView.allowsUndo = true
        textView.isSelectable = true
        textView.isVerticallyResizable = true
        textView.isHorizontallyResizable = false
        textView.autoresizingMask = [.width]
        textView.minSize = .zero
        textView.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        textView.textContainerInset = NSSize(width: 5, height: 8)
        textView.textContainer?.containerSize = NSSize(
            width: contentSize.width,
            height: CGFloat.greatestFiniteMagnitude
        )
        textView.textContainer?.widthTracksTextView = true
        textView.frame = NSRect(origin: .zero, size: contentSize)
        documentView = textView
    }

    required init?(coder: NSCoder) {
        fatalError("不支持从归档创建场景文本视图")
    }

    func update(text: String, isEditable: Bool) {
        if textView.isEditable != isEditable {
            textView.isEditable = isEditable
        }
        let font = NSFont.systemFont(ofSize: isEditable ? 12.5 : 12, weight: isEditable ? .medium : .regular)
        if textView.font != font { textView.font = font }
        let color = NSColor(isEditable ? Theme.ink : Theme.inkSecondary)
        if textView.textColor != color { textView.textColor = color }

        // 相同内容不重设文本，保留光标、撤销记录、滚动位置和输入法组合状态。
        guard textView.string != text, !textView.hasMarkedText() else { return }
        textView.string = text
    }
}
