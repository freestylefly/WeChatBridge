import ActivityKit
import AppIntents
import SwiftUI
import WidgetKit

@main
struct WeChatBridgeWidgets: WidgetBundle {
    var body: some Widget {
        DeliveryLiveActivity()
    }
}

struct DeliveryLiveActivity: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: DeliveryActivityAttributes.self) { context in
            LockScreenView(context: context)
                .activityBackgroundTint(Color(.systemBackground).opacity(0.85))
                .widgetURL(context.attributes.detailURL)
        } dynamicIsland: { context in
            DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    AppGlyph(size: 40).padding(.leading, 4)
                }
                DynamicIslandExpandedRegion(.trailing) {
                    TargetGlyph(attributes: context.attributes, size: 40).padding(.trailing, 4)
                }
                DynamicIslandExpandedRegion(.center) {
                    VStack(spacing: 2) {
                        Text(context.attributes.title).font(.headline).lineLimit(1)
                        Text("投递到「\(context.attributes.targetName)」· \(context.state.status)")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                    }
                }
                DynamicIslandExpandedRegion(.bottom) {
                    ActionRow(context: context).padding(.top, 6)
                }
            } compactLeading: {
                AppGlyph(size: 22)
            } compactTrailing: {
                TargetGlyph(attributes: context.attributes, size: 22)
            } minimal: {
                AppGlyph(size: 22)
            }
            .widgetURL(context.attributes.detailURL)
            .keylineTint(Color("AccentColor"))
        }
    }
}

private struct LockScreenView: View {
    let context: ActivityViewContext<DeliveryActivityAttributes>

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 12) {
                AppGlyph(size: 44)
                VStack(alignment: .leading, spacing: 2) {
                    Text(context.attributes.title).font(.headline).lineLimit(1)
                    Text(context.attributes.detail.isEmpty ? context.state.status : "\(context.attributes.detail) · \(context.state.status)")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                }
                Spacer(minLength: 0)
                TargetGlyph(attributes: context.attributes, size: 36)
            }
            ActionRow(context: context)
        }
        .padding(16)
    }
}

private struct ActionRow: View {
    let context: ActivityViewContext<DeliveryActivityAttributes>

    var body: some View {
        HStack(spacing: 8) {
            Link(destination: context.attributes.detailURL) {
                Label("回到微信流", systemImage: "arrow.uturn.backward")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(ActionStyle(prominent: true))
            if context.state.hasPrompt {
                Button(intent: CopyDeliveryPromptIntent()) {
                    Label("复制提示词", systemImage: "doc.on.doc")
                        .frame(maxWidth: .infinity)
                }
                .buttonStyle(ActionStyle(prominent: false))
            }
            Button(intent: EndDeliveryActivityIntent()) {
                Image(systemName: "xmark")
            }
            .buttonStyle(ActionStyle(prominent: false))
            .accessibilityLabel("结束")
        }
        .font(.footnote.weight(.semibold))
        .labelStyle(.titleAndIcon)
    }
}

private struct ActionStyle: ButtonStyle {
    let prominent: Bool

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .padding(.vertical, 9)
            .padding(.horizontal, 12)
            .foregroundStyle(prominent ? Color.white : Color.primary)
            .background(prominent ? Color("AccentColor") : Color.secondary.opacity(0.25), in: Capsule())
            .opacity(configuration.isPressed ? 0.7 : 1)
    }
}

private struct AppGlyph: View {
    let size: CGFloat

    var body: some View {
        Image("AppGlyph")
            .resizable()
            .scaledToFit()
            .frame(width: size, height: size)
            .clipShape(RoundedRectangle(cornerRadius: size * 0.225, style: .continuous))
    }
}

private struct TargetGlyph: View {
    let attributes: DeliveryActivityAttributes
    let size: CGFloat

    var body: some View {
        Group {
            if let logo = attributes.targetLogo {
                Image(logo).resizable().scaledToFit()
            } else {
                Image(systemName: "square.and.arrow.up")
                    .font(.system(size: size * 0.5, weight: .semibold))
                    .foregroundStyle(Color("AccentColor"))
                    .frame(width: size, height: size)
                    .background(Color("AccentColor").opacity(0.18))
            }
        }
        .frame(width: size, height: size)
        .clipShape(RoundedRectangle(cornerRadius: size * 0.225, style: .continuous))
    }
}
