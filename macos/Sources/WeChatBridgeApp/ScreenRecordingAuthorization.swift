import AppKit
import Combine
import CoreGraphics
import Foundation

@MainActor
final class ScreenRecordingAuthorization: ObservableObject {
    @Published private(set) var isGranted = CGPreflightScreenCaptureAccess()

    func refresh() {
        let value = CGPreflightScreenCaptureAccess()
        if value != isGranted { isGranted = value }
    }

    func request() {
        guard !isGranted else { return }
        _ = CGRequestScreenCaptureAccess()
        refresh()
    }

    func openSystemSettings() {
        guard let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") else { return }
        NSWorkspace.shared.open(url)
    }
}
