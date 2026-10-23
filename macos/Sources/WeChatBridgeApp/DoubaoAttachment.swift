import AppKit
import ApplicationServices
import WeChatBridgeCore

/// Doubao's web-based composer ignores file URLs pasted with Command-V.
/// Its attachment picker is the only route that actually uploads the files, so
/// this drives that picker through Accessibility and the native open panel.
enum DoubaoAttachment {
    enum Failure: Error, LocalizedError {
        case menu
        case dialog
        case expand(String)
        case unsupported(String)
        case rejected(String)

        var errorDescription: String? {
            switch self {
            case .menu:
                return L10n.text("没有找到豆包的“上传文件或图片”入口。")
            case .dialog:
                return L10n.text("豆包没有打开文件选择窗口。")
            case .expand(let name):
                return L10n.format("无法解包“%@”。", name)
            case .unsupported(let name):
                return L10n.format("压缩包“%@”里没有豆包可读取的文件。", name)
            case .rejected(let name):
                return L10n.format("豆包没有接住文件“%@”。", name)
            }
        }
    }

    private struct Node {
        let element: AXUIElement
        let role: String
        let strings: [String]
        let children: [AXUIElement]
        let rect: CGRect
        let isEnabled: Bool

        init(_ element: AXUIElement) {
            self.element = element
            role = Self.value(element, kAXRoleAttribute) as? String ?? ""
            strings = [
                Self.value(element, kAXTitleAttribute),
                Self.value(element, kAXValueAttribute),
                Self.value(element, kAXDescriptionAttribute),
                Self.value(element, kAXHelpAttribute),
            ].compactMap { $0 as? String }.filter { !$0.isEmpty }
            children = Self.value(element, kAXChildrenAttribute) as? [AXUIElement] ?? []
            isEnabled = Self.value(element, kAXEnabledAttribute) as? Bool ?? true
            var point = CGPoint.zero
            var size = CGSize.zero
            if let value = Self.value(element, kAXPositionAttribute),
               CFGetTypeID(value) == AXValueGetTypeID() {
                AXValueGetValue(value as! AXValue, .cgPoint, &point)
            }
            if let value = Self.value(element, kAXSizeAttribute),
               CFGetTypeID(value) == AXValueGetTypeID() {
                AXValueGetValue(value as! AXValue, .cgSize, &size)
            }
            rect = CGRect(origin: point, size: size)
        }

        private static func value(_ element: AXUIElement, _ key: String) -> CFTypeRef? {
            var value: CFTypeRef?
            return AXUIElementCopyAttributeValue(element, key as CFString, &value) == .success ? value : nil
        }
    }

    @MainActor
    static func attach(urls: [URL]) async throws {
        guard !urls.isEmpty else { return }
        guard AutoPaste.isTrusted,
              let app = NSRunningApplication.runningApplications(
                withBundleIdentifier: "com.bot.pc.doubao"
              ).first
        else {
            throw AutoPaste.Failure.notTrusted
        }

        let root = AXUIElementCreateApplication(app.processIdentifier)
        AXUIElementSetMessagingTimeout(root, 1)
        let expanded = try expand(urls)
        defer {
            if let directory = expanded.cleanup {
                try? FileManager.default.removeItem(at: directory)
            }
        }
        for url in expanded.urls {
            let existingMatches = attachmentMatchCount(named: url.lastPathComponent, in: root)
            try await attach(url, to: root)
            guard try await waitForAttachment(
                named: url.lastPathComponent,
                exceeding: existingMatches,
                in: root
            ) else {
                throw Failure.rejected(url.lastPathComponent)
            }
        }
    }

    private static func attach(_ url: URL, to root: AXUIElement) async throws {
        await dismissStaleOpenPanel(in: root)

        let item: Node
        if let openItem = nodes(in: root).first(where: isUploadItem) {
            item = openItem
        } else {
            guard let button = try await waitForAttachmentButton(in: root, timeout: 8) else {
                throw Failure.menu
            }
            try click(button)
            guard let openedItem = try await waitForNode(
                in: root,
                timeout: 2,
                where: isUploadItem
            ) else {
                throw Failure.menu
            }
            item = openedItem
        }

        try click(item)
        try await Task.sleep(nanoseconds: 700_000_000)
        try pressCommandShiftG()
        try await Task.sleep(nanoseconds: 250_000_000)
        try pasteTemporarily(url.path)
        try await Task.sleep(nanoseconds: 200_000_000)
        try pressReturn()
        guard try await waitForOpenButton(in: root, timeout: 3) else {
            throw Failure.dialog
        }
        try pressReturn()
        guard await waitForOpenPanelToClose(in: root, timeout: 3) else {
            throw Failure.dialog
        }
    }

    private static func isUploadItem(_ node: Node) -> Bool {
        node.role == kAXMenuItemRole
            && node.strings.contains(where: { $0.contains("上传文件或图片") })
    }

    private static func waitForOpenButton(in root: AXUIElement, timeout: TimeInterval) async throws -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if hasOpenButton(in: root) { return true }
            try await Task.sleep(nanoseconds: 100_000_000)
        }
        return false
    }

    /// Doubao rejects ZIP archives. Its chat-export ZIPs contain a text
    /// transcript plus media, both of which it accepts as attachments.
    private static func expand(_ urls: [URL]) throws -> (urls: [URL], cleanup: URL?) {
        guard urls.contains(where: { $0.pathExtension.lowercased() == "zip" }) else {
            return (urls, nil)
        }

        let fileManager = FileManager.default
        let root = fileManager.temporaryDirectory
            .appendingPathComponent("WeChatBridge-Doubao-\(UUID().uuidString)", isDirectory: true)
        try fileManager.createDirectory(at: root, withIntermediateDirectories: true)

        do {
            var expanded: [URL] = []
            for url in urls {
                guard url.pathExtension.lowercased() == "zip" else {
                    expanded.append(url)
                    continue
                }

                let directory = root.appendingPathComponent(UUID().uuidString, isDirectory: true)
                try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
                let process = Process()
                process.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
                process.arguments = ["-x", "-k", url.path, directory.path]
                try process.run()
                process.waitUntilExit()
                guard process.terminationStatus == 0 else {
                    throw Failure.expand(url.lastPathComponent)
                }

                let files = uploadableFiles(in: directory)
                guard !files.isEmpty else {
                    throw Failure.unsupported(url.lastPathComponent)
                }
                expanded.append(contentsOf: files)
            }
            return (expanded, root)
        } catch {
            try? fileManager.removeItem(at: root)
            throw error
        }
    }

    private static func uploadableFiles(in directory: URL) -> [URL] {
        let supported: Set<String> = [
            "txt", "md", "csv", "rtf", "pdf",
            "doc", "docx", "xls", "xlsx", "ppt", "pptx",
            "png", "jpg", "jpeg", "heic", "webp", "gif", "bmp",
        ]
        let rank: [String: Int] = [
            "txt": 0, "md": 1, "csv": 2, "rtf": 3, "pdf": 4,
            "doc": 5, "docx": 5, "xls": 6, "xlsx": 6, "ppt": 7, "pptx": 7,
        ]
        guard let enumerator = FileManager.default.enumerator(
            at: directory,
            includingPropertiesForKeys: [.isRegularFileKey],
            options: [.skipsHiddenFiles]
        ) else {
            return []
        }

        let files = enumerator
            .compactMap { $0 as? URL }
            .filter { supported.contains($0.pathExtension.lowercased()) }
        let documents = files.filter { rank[$0.pathExtension.lowercased()] != nil }
        return (documents.isEmpty ? files : documents)
            .sorted {
                let left = rank[$0.pathExtension.lowercased()] ?? 100
                let right = rank[$1.pathExtension.lowercased()] ?? 100
                return left == right
                    ? $0.lastPathComponent.localizedStandardCompare($1.lastPathComponent) == .orderedAscending
                    : left < right
            }
    }

    /// Doubao's picker button has no label until its menu has been opened. In
    /// that state it is the bottommost composer pop-up with an image child;
    /// message action menus higher in the chat match the same shape.
    private static func attachmentButton(in nodes: [Node]) -> Node? {
        let labeled = nodes.first {
            $0.role == kAXPopUpButtonRole
                && $0.strings.contains(where: { $0.contains("上传文件或图片") })
        }
        if let labeled { return labeled }

        return nodes.filter {
            $0.role == kAXPopUpButtonRole
                && $0.strings.isEmpty
                && containsImage($0)
        }.max { $0.rect.maxY < $1.rect.maxY }
    }

    private static func waitForAttachmentButton(
        in root: AXUIElement,
        timeout: TimeInterval
    ) async throws -> Node? {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if let button = attachmentButton(in: nodes(in: root)) { return button }
            try await Task.sleep(nanoseconds: 120_000_000)
        }
        return nil
    }

    private static func containsImage(_ node: Node) -> Bool {
        var queue = node.children
        var index = 0
        while index < queue.count && index < 50 {
            let child = Node(queue[index])
            if child.role == kAXImageRole { return true }
            queue.append(contentsOf: child.children)
            index += 1
        }
        return false
    }

    private static func attachmentMatchCount(named name: String, in root: AXUIElement) -> Int {
        nodes(in: root).reduce(into: 0) { count, node in
            if node.strings.contains(where: { $0.contains(name) }) {
                count += 1
            }
        }
    }

    private static func waitForAttachment(
        named name: String,
        exceeding baseline: Int,
        in root: AXUIElement
    ) async throws -> Bool {
        let deadline = Date().addingTimeInterval(6)
        while Date() < deadline {
            if attachmentMatchCount(named: name, in: root) > baseline {
                return true
            }
            try await Task.sleep(nanoseconds: 120_000_000)
        }
        return false
    }

    /// A previous run can stop after choosing the file but before Doubao closes
    /// its native open panel. The next share then types into that leftover panel
    /// instead of the composer, so every later upload fails. At most two Escapes
    /// are enough: the first closes the panel, and the second guards a panel that
    /// took a moment to appear.
    private static func dismissStaleOpenPanel(in root: AXUIElement) async {
        guard hasOpenButton(in: root) else { return }
        for _ in 0..<2 {
            try? pressEscape()
            if await waitForOpenPanelToClose(in: root, timeout: 1) { return }
        }
    }

    private static func waitForOpenPanelToClose(
        in root: AXUIElement,
        timeout: TimeInterval
    ) async -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if !hasOpenButton(in: root) { return true }
            try? await Task.sleep(nanoseconds: 100_000_000)
        }
        return false
    }

    private static func hasOpenButton(in root: AXUIElement) -> Bool {
        nodes(in: root).contains(where: {
            $0.role == kAXButtonRole
                && $0.isEnabled
                && $0.strings.contains(where: { $0 == "打开" || $0 == "Open" })
        })
    }

    private static func waitForNode(
        in root: AXUIElement,
        timeout: TimeInterval,
        where predicate: (Node) -> Bool
    ) async throws -> Node? {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if let node = nodes(in: root).first(where: predicate) { return node }
            try await Task.sleep(nanoseconds: 80_000_000)
        }
        return nil
    }

    private static func nodes(in root: AXUIElement) -> [Node] {
        var queue = [root]
        var result: [Node] = []
        var index = 0
        while index < queue.count && result.count < 2_500 {
            let node = Node(queue[index])
            index += 1
            result.append(node)
            queue.append(contentsOf: node.children)
        }
        return result
    }

    private static func click(_ node: Node) throws {
        guard node.rect.width > 0, node.rect.height > 0 else { throw Failure.menu }
        let point = CGPoint(x: node.rect.midX, y: node.rect.midY)
        let source = CGEventSource(stateID: .combinedSessionState)
        guard
            let down = CGEvent(
                mouseEventSource: source,
                mouseType: .leftMouseDown,
                mouseCursorPosition: point,
                mouseButton: .left
            ),
            let up = CGEvent(
                mouseEventSource: source,
                mouseType: .leftMouseUp,
                mouseCursorPosition: point,
                mouseButton: .left
            )
        else {
            throw AutoPaste.Failure.eventCreationFailed
        }
        down.setIntegerValueField(.mouseEventClickState, value: 1)
        up.setIntegerValueField(.mouseEventClickState, value: 1)
        down.post(tap: .cghidEventTap)
        usleep(50_000)
        up.post(tap: .cghidEventTap)
    }

    private static func pasteTemporarily(_ text: String) throws {
        let pasteboard = NSPasteboard.general
        let saved = copyPasteboardItems(pasteboard)
        guard FilePasteboard.writeText(text, to: pasteboard) else {
            throw AutoPaste.Failure.eventCreationFailed
        }
        try pressCommandV()
        usleep(150_000)
        if !saved.isEmpty {
            pasteboard.clearContents()
            pasteboard.writeObjects(saved)
        }
    }

    /// `NSPasteboardItem` instances cannot be attached to a second pasteboard.
    /// Copy their data first, or restoring the user's clipboard raises an
    /// Objective-C exception and takes the whole app down mid-upload.
    private static func copyPasteboardItems(_ pasteboard: NSPasteboard) -> [NSPasteboardItem] {
        (pasteboard.pasteboardItems ?? []).map { item in
            let copy = NSPasteboardItem()
            for type in item.types {
                if let data = item.data(forType: type) {
                    copy.setData(data, forType: type)
                }
            }
            return copy
        }
    }

    private static func pressCommandShiftG() throws {
        try pressKey(0x05, flags: [.maskCommand, .maskShift])
    }

    private static func pressCommandV() throws {
        try pressKey(0x09, flags: .maskCommand)
    }

    private static func pressReturn() throws {
        try pressKey(0x24, flags: [])
    }

    private static func pressEscape() throws {
        try pressKey(0x35, flags: [])
    }

    private static func pressKey(_ code: CGKeyCode, flags: CGEventFlags) throws {
        let source = CGEventSource(stateID: .combinedSessionState)
        guard
            let down = CGEvent(keyboardEventSource: source, virtualKey: code, keyDown: true),
            let up = CGEvent(keyboardEventSource: source, virtualKey: code, keyDown: false)
        else {
            throw AutoPaste.Failure.eventCreationFailed
        }
        down.flags = flags
        up.flags = flags
        down.post(tap: .cghidEventTap)
        up.post(tap: .cghidEventTap)
    }
}
