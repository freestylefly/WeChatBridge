import AppKit
import ApplicationServices
import WeChatBridgeCore

/// Activates another app and presses ⌘V in it.
///
/// This is the only part of WeChatBridge that reaches outside its own windows, and it
/// is gated by the system: synthesising a keystroke requires the Accessibility
/// permission, which only the user can grant. Everything here is written so that
/// a refusal degrades to "the files are on your clipboard, press ⌘V yourself"
/// rather than to a silent no-op.
enum AutoPaste {
    enum Failure: Error, LocalizedError {
        case notInstalled(name: String)
        case notTrusted
        case didNotBecomeActive(name: String)
        case eventCreationFailed

        var errorDescription: String? {
            switch self {
            case .notInstalled(let name):
                return L10n.format("这台 Mac 上没有找到 %@。", name)
            case .notTrusted:
                return L10n.text("WeChatBridge 还没有「辅助功能」权限，没法代你按 ⌘V。")
            case .didNotBecomeActive(let name):
                return L10n.format("%@ 没有切到前台，已经放弃自动粘贴。", name)
            case .eventCreationFailed:
                return L10n.text("系统没有接受这次按键事件。")
            }
        }
    }

    /// Synchronous, side-effect free and safe to call from anywhere, so the
    /// first frame of any UI that depends on it already shows the truth.
    static var isTrusted: Bool { AXIsProcessTrusted() }

    static func openAccessibilitySettings() {
        guard let url = URL(
            string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"
        ) else { return }
        NSWorkspace.shared.open(url)
    }

    static func applicationURL(forBundleIdentifier identifier: String) -> URL? {
        NSWorkspace.shared.urlForApplication(withBundleIdentifier: identifier)
    }

    /// Brings the target forward and pastes into it.
    ///
    /// The wait is not decoration: `openApplication` returns as soon as the
    /// process is running, which on a cold launch is long before it has a window
    /// or a focused text field. Pressing ⌘V at that moment types into nothing.
    static func activateAndPaste(
        applicationAt url: URL,
        bundleIdentifier: String,
        displayName: String,
        plan: [PastePayload]
    ) async throws {
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = true
        configuration.addsToRecentItems = false

        _ = try await NSWorkspace.shared.openApplication(at: url, configuration: configuration)
        let pid = NSRunningApplication
            .runningApplications(withBundleIdentifier: bundleIdentifier)
            .first?
            .processIdentifier
        // Raise the window the user last had focused before ⌘V goes anywhere
        // near it.
        if let pid { restoreWindows(pid: pid) }
        try await waitUntilFrontmost(bundleIdentifier: bundleIdentifier, displayName: displayName)
        // A short settle after the app is frontmost, so the window has had a
        // turn of its own run loop to focus its input field.
        try? await Task.sleep(nanoseconds: 350_000_000)
        // …and when it has not: see `focusTextInput`.
        if let pid { focusTextInput(pid: pid) }
        try? await Task.sleep(nanoseconds: focusSettle)

        guard isTrusted else { throw Failure.notTrusted }
        for (index, payload) in plan.enumerated() {
            if index > 0 { try? await Task.sleep(nanoseconds: betweenPastes) }
            guard FilePasteboard.write(payload) else { throw Failure.eventCreationFailed }
            try pressCommandV()
        }
    }

    /// Between the two pastes of a prompt-plus-files plan. The target has to
    /// take the first one — attach the file, insert the text — before the
    /// pasteboard is rewritten under it; a chat app that is still reading a
    /// file URL when the text lands drops one or the other.
    private static let betweenPastes: UInt64 = 450_000_000

    /// After `focusTextInput`, so the renderer has moved its DOM focus before
    /// the ⌘V arrives. Short: the target is already up and frontmost.
    private static let focusSettle: UInt64 = 150_000_000

    /// Raises the target's focused window, and takes it out of the Dock only
    /// when the app has nothing else on screen.
    ///
    /// Belt and braces behind `activate`, and deliberately timid. Restoring
    /// *every* minimized window is the obvious reading of 「还原目标」 and is
    /// wrong twice: it reopens windows the user put away on purpose, and —
    /// measured 2026-09-06 with TextEdit, window A focused and window B in the
    /// Dock — deminiaturizing makes a window key, so B ends up focused and ⌘V
    /// lands in the window the user was not looking at. WeChat pays the same
    /// price: a popped-out chat coming back reads as wrongChat.
    ///
    /// So the focused window is read *before* anything is touched, that one is
    /// the only window ever unminimized, and only when there is no other way to
    /// see the app — the case LaunchServices already covers, kept as the
    /// fallback for when it does not. Every error is ignored on purpose: this
    /// backs up a foreground change that has already been asked for, and an app
    /// that exposes no AX windows (one still launching) is the ordinary case,
    /// not a failure.
    static func restoreWindows(pid: pid_t) {
        guard isTrusted else { return }
        let application = AXUIElementCreateApplication(pid)
        // One second, so an app that has stopped answering cannot freeze
        // WeChatBridge's own windows.
        AXUIElementSetMessagingTimeout(application, 1)
        guard let windows = attribute(application, kAXWindowsAttribute) as? [AXUIElement],
              !windows.isEmpty,
              let raise = focusedWindow(of: application) else { return }
        if windows.allSatisfy(isMinimized) {
            AXUIElementSetAttributeValue(raise, kAXMinimizedAttribute as CFString, kCFBooleanFalse)
        }
        AXUIElementPerformAction(raise, kAXRaiseAction as CFString)
    }

    /// Puts the keyboard into the target's own text field before ⌘V.
    ///
    /// Activating an app restores *its* focus, not the field inside it. A
    /// composer that was never clicked has its DOM focus on the page body, and
    /// the ⌘V that follows is swallowed in silence — while the forward still
    /// records 已送达, because the keystroke was posted and nothing threw.
    ///
    /// Measured 2026-09-20 on WeSight 1.0.8 (Electron 40, `ai.wesight.app`),
    /// which is where this was found: 「发给 WeSight」 recorded 已送达 and left
    /// the input box empty. Two ⌘V presses over the same file-URL pasteboard
    /// told the two cases apart — window frontmost with nothing focused, the
    /// accessibility tree was unchanged; composer clicked first, the same ⌘V
    /// produced the attachment chip.
    ///
    /// Chromium keeps DOM focus across app switches, so a target last used in
    /// its composer already works, and this only has to cover the one that was
    /// not. Bottom-most editable element wins: all of these destinations are
    /// chats and their composer is the input at the bottom of the window.
    /// Nothing found is not a failure — the paste is attempted exactly as it
    /// was before.
    ///
    /// ponytail: geometric guess, not a per-app selector. If a destination
    /// grows a second text field below its composer, name that app's composer
    /// role here instead.
    private static func focusTextInput(pid: pid_t) {
        guard isTrusted else { return }
        let application = AXUIElementCreateApplication(pid)
        // The same one-second leash `restoreWindows` uses.
        AXUIElementSetMessagingTimeout(application, 1)
        guard let window = focusedWindow(of: application),
              let field = editableElements(in: window)
                  .max(by: { $0.frame.maxY < $1.frame.maxY })?.element
        else { return }
        AXUIElementSetAttributeValue(field, kAXFocusedAttribute as CFString, kCFBooleanTrue)
    }

    /// The window the app itself calls key, or its first window for an app
    /// that answers with nothing — one still building its window, which is
    /// what the fallback is for.
    private static func focusedWindow(of application: AXUIElement) -> AXUIElement? {
        guard let windows = attribute(application, kAXWindowsAttribute) as? [AXUIElement],
              !windows.isEmpty else { return nil }
        // The type check before the cast: an AX attribute is a `CFTypeRef` and
        // a forced cast on the wrong kind traps rather than returning nil.
        if let focused = attribute(application, kAXFocusedWindowAttribute),
           CFGetTypeID(focused) == AXUIElementGetTypeID() {
            return (focused as! AXUIElement)
        }
        return windows[0]
    }

    private struct EditableElement {
        let element: AXUIElement
        let frame: CGRect
    }

    /// Every text area and text field in the window that would accept focus.
    ///
    /// Breadth-first with a cap: an app that answers slowly or not at all is
    /// the ordinary case here, and `AXUIElementSetMessagingTimeout` only
    /// bounds each individual call.
    private static func editableElements(in root: AXUIElement, limit: Int = 600) -> [EditableElement] {
        var queue = [root]
        var found: [EditableElement] = []
        var index = 0
        while index < queue.count && index < limit {
            let element = queue[index]
            index += 1
            let role = attribute(element, kAXRoleAttribute) as? String
            if role == kAXTextAreaRole || role == kAXTextFieldRole,
               (attribute(element, kAXEnabledAttribute) as? Bool) ?? true,
               isSettable(element, kAXFocusedAttribute),
               let frame = frame(of: element) {
                found.append(EditableElement(element: element, frame: frame))
            }
            queue.append(contentsOf: (attribute(element, kAXChildrenAttribute) as? [AXUIElement]) ?? [])
        }
        return found
    }

    private static func attribute(_ element: AXUIElement, _ key: String) -> CFTypeRef? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, key as CFString, &value) == .success else { return nil }
        return value
    }

    private static func isSettable(_ element: AXUIElement, _ key: String) -> Bool {
        var settable = DarwinBoolean(false)
        guard AXUIElementIsAttributeSettable(element, key as CFString, &settable) == .success else { return false }
        return settable.boolValue
    }

    private static func frame(of element: AXUIElement) -> CGRect? {
        guard let position = attribute(element, kAXPositionAttribute),
              let size = attribute(element, kAXSizeAttribute),
              CFGetTypeID(position) == AXValueGetTypeID(),
              CFGetTypeID(size) == AXValueGetTypeID() else { return nil }
        var origin = CGPoint.zero
        var dimensions = CGSize.zero
        guard AXValueGetValue(position as! AXValue, .cgPoint, &origin),
              AXValueGetValue(size as! AXValue, .cgSize, &dimensions) else { return nil }
        return CGRect(origin: origin, size: dimensions)
    }

    private static func isMinimized(_ window: AXUIElement) -> Bool {
        var minimized: CFTypeRef?
        guard AXUIElementCopyAttributeValue(window, kAXMinimizedAttribute as CFString, &minimized) == .success else { return false }
        return (minimized as? Bool) == true
    }

    private static func waitUntilFrontmost(
        bundleIdentifier: String,
        displayName: String,
        timeout: TimeInterval = 4
    ) async throws {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if NSWorkspace.shared.frontmostApplication?.bundleIdentifier == bundleIdentifier {
                return
            }
            try? await Task.sleep(nanoseconds: 60_000_000)
        }
        throw Failure.didNotBecomeActive(name: displayName)
    }

    /// `kVK_ANSI_V` is a physical key position, not the letter "V", so this
    /// works on Dvorak and on non-Latin layouts where a character-based lookup
    /// would send the wrong key.
    private static let virtualKeyV: CGKeyCode = 0x09

    private static func pressCommandV() throws {
        let source = CGEventSource(stateID: .combinedSessionState)
        guard
            let keyDown = CGEvent(keyboardEventSource: source, virtualKey: virtualKeyV, keyDown: true),
            let keyUp = CGEvent(keyboardEventSource: source, virtualKey: virtualKeyV, keyDown: false)
        else { throw Failure.eventCreationFailed }

        keyDown.flags = .maskCommand
        keyUp.flags = .maskCommand
        // The HID tap posts as if the key were physically pressed, which is what
        // reaches an app that is not listening for annotated session events.
        keyDown.post(tap: .cghidEventTap)
        keyUp.post(tap: .cghidEventTap)
    }
}
