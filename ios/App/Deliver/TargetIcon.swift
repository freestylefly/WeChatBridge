import SwiftUI

/// A target as an app icon: its real logo when the project ships one, else an
/// SF Symbol on a tinted tile of the same shape.
struct TargetIcon: View {
    let profile: TargetProfile
    var size: CGFloat = 52

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: size * 0.225, style: .continuous)
        Group {
            if let logo = profile.logo {
                Image(logo).resizable().scaledToFill()
            } else {
                Image(systemName: profile.symbol)
                    .font(.system(size: size * 0.42, weight: .medium))
                    .foregroundStyle(Color.accentColor)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .background(Color.accentColor.opacity(0.12))
            }
        }
        .frame(width: size, height: size)
        .clipShape(shape)
        .overlay(shape.strokeBorder(Color.primary.opacity(0.08), lineWidth: 0.5))
    }
}
