import ImageIO
import SwiftUI
import UIKit

/// Downsampled decoding: a forwarded chat can carry dozens of full-size
/// photos, and decoding them at full resolution for a thumbnail costs memory.
enum Thumbnail {
    static func load(_ url: URL, maxPixel: Int) -> UIImage? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, [kCGImageSourceShouldCache: false] as CFDictionary) else {
            return nil
        }
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceShouldCacheImmediately: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixel,
        ]
        guard let image = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) else { return nil }
        return UIImage(cgImage: image)
    }
}

struct ImageViewer: View {
    let url: URL
    @Environment(\.dismiss) private var dismiss
    @State private var scale: CGFloat = 1
    @State private var settled: CGFloat = 1

    var body: some View {
        NavigationStack {
            GeometryReader { proxy in
                if let image = Thumbnail.load(url, maxPixel: Int(max(proxy.size.width, proxy.size.height) * 3)) {
                    Image(uiImage: image)
                        .resizable()
                        .scaledToFit()
                        .scaleEffect(scale)
                        .frame(width: proxy.size.width, height: proxy.size.height)
                        .gesture(
                            MagnificationGesture()
                                .onChanged { scale = min(max(settled * $0, 1), 5) }
                                .onEnded { _ in settled = scale }
                        )
                        .onTapGesture(count: 2) {
                            withAnimation { scale = scale > 1 ? 1 : 2.5; settled = scale }
                        }
                } else {
                    ContentUnavailableView("无法显示这张图片", systemImage: "photo")
                }
            }
            .background(.black)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) { Button("完成") { dismiss() } }
                ToolbarItem(placement: .topBarTrailing) {
                    ShareLink(item: url) { Image(systemName: "square.and.arrow.up") }
                }
            }
            .toolbarBackground(.visible, for: .navigationBar)
        }
    }
}
