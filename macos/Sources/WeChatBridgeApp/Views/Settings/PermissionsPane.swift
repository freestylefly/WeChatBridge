import WeChatBridgeCore
import SwiftUI

struct PermissionsPane: View {
    @ObservedObject var authorization: AccessibilityAuthorization
    @ObservedObject var screenRecording: ScreenRecordingAuthorization

    var body: some View {
        VStack(alignment: .leading, spacing: Space.section) {
            SettingsSection(title: L10n.text("系统权限"), systemImage: "checkmark.shield") {
                PermissionRow(
                    title: L10n.text("辅助功能"),
                    detail: L10n.text("用于激活目标应用并粘贴；只复制不需要。"),
                    granted: authorization.isTrusted
                ) {
                    Button(L10n.text("引导授权")) { authorization.guideIfNeeded() }
                        .buttonStyle(SettingsActionButtonStyle())
                }
                PermissionRow(
                    title: L10n.text("屏幕录制"),
                    detail: L10n.text("用于截取微信群标题栏并识别群名；图片只在内存中处理，不落盘。"),
                    granted: screenRecording.isGranted
                ) {
                    Button(screenRecording.isGranted ? L10n.text("打开系统设置") : L10n.text("申请权限")) {
                        if screenRecording.isGranted {
                            screenRecording.openSystemSettings()
                        } else {
                            screenRecording.request()
                        }
                    }
                    .buttonStyle(SettingsActionButtonStyle())
                }
            }

            Notice(L10n.text("辅助功能不会弹系统授权框，需要把 WeChatBridge 拖进列表；授权后立即生效，不必重启。"))
        }
        .onAppear { screenRecording.refresh() }
    }
}
