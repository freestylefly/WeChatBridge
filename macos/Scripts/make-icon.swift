// Builds the WeChatBridge app icon from a bitmap artwork PNG.
// The icon is an illustration, so the artwork stays a bitmap and this script
// only adds what Apple's grid requires: the 1024 canvas, the 824 body with a
// continuous corner mask, and the two template shadows.
//
//   swift Scripts/make-icon.swift Resources/AppIcon-artwork.png \
//     Resources/AppIcon.png Resources/AppIcon.icns \
//     ../shared/Screenshots/app-icon-rounded.png
//
// The optional last argument exports the rounded body at 512 px without margins.
import AppKit
import SwiftUI
import ImageIO

let arguments = CommandLine.arguments
guard arguments.count == 4 || arguments.count == 5 else {
    FileHandle.standardError.write(Data("usage: make-icon.swift <artwork.png> <out.png> <out.icns> [rounded-512.png]\n".utf8))
    exit(2)
}
let inputURL = URL(fileURLWithPath: arguments[1])
let pngURL = URL(fileURLWithPath: arguments[2])
let outputURL = URL(fileURLWithPath: arguments[3])
let roundedURL = arguments.count == 5 ? URL(fileURLWithPath: arguments[4]) : nil
let outputs = [pngURL, outputURL] + (roundedURL.map { [$0] } ?? [])
let paths = ([inputURL] + outputs).map { $0.resolvingSymlinksInPath().standardizedFileURL.path }
guard Set(paths).count == paths.count else {
    FileHandle.standardError.write(Data("input and output paths must be distinct\n".utf8))
    exit(2)
}
guard let source = CGImageSourceCreateWithURL(inputURL as CFURL, nil),
      CGImageSourceGetType(source) as String? == "public.png",
      let pixels = CGImageSourceCreateImageAtIndex(source, 0, nil),
      pixels.width == pixels.height,
      let artwork = NSImage(contentsOf: inputURL) else {
    FileHandle.standardError.write(Data("artwork must be a readable square PNG: \(inputURL.path)\n".utf8))
    exit(1)
}
for url in outputs {
    try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
}

enum Grid {
    static let canvas: CGFloat = 1024
    static let body: CGFloat = 824
    static let radius: CGFloat = 185.4
    /// The mark reads small at Share-menu sizes. Enlarge it inside the same
    /// rounded body instead of changing the tile's footprint.
    static let markScale: CGFloat = 1.16
}

struct IconBody: View {
    let artwork: NSImage

    var body: some View {
        Image(nsImage: artwork)
            .resizable()
            .interpolation(.high)
            .aspectRatio(contentMode: .fill)
            .frame(width: Grid.body, height: Grid.body)
            .scaleEffect(Grid.markScale)
            .clipShape(RoundedRectangle(cornerRadius: Grid.radius, style: .continuous))
    }
}

struct IconCanvas: View {
    let artwork: NSImage

    var body: some View {
        ZStack {
            Color.clear
            IconBody(artwork: artwork)
                .shadow(color: .black.opacity(0.28), radius: 12, y: 10)
                .shadow(color: .black.opacity(0.12), radius: 3, y: 2)
        }
        .frame(width: Grid.canvas, height: Grid.canvas)
    }
}

@MainActor
func renderCanvas() -> CGImage? {
    let renderer = ImageRenderer(content: IconCanvas(artwork: artwork))
    renderer.scale = 1
    renderer.isOpaque = false
    return renderer.cgImage
}

func write(_ image: CGImage, side: Int, to url: URL) throws {
    let bitmap = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: side, pixelsHigh: side,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
    )!
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
    NSGraphicsContext.current?.imageInterpolation = .high
    NSGraphicsContext.current?.cgContext.draw(image, in: CGRect(x: 0, y: 0, width: side, height: side))
    NSGraphicsContext.restoreGraphicsState()
    guard let data = bitmap.representation(using: .png, properties: [:]) else {
        throw CocoaError(.fileWriteUnknown)
    }
    try data.write(to: url, options: .atomic)
}

let rendered = MainActor.assumeIsolated { renderCanvas() }
guard let rendered else {
    FileHandle.standardError.write(Data("render failed\n".utf8))
    exit(1)
}

try write(rendered, side: 1024, to: pngURL)

if let roundedURL {
    let roundedImage = MainActor.assumeIsolated { () -> CGImage? in
        let renderer = ImageRenderer(content: IconBody(artwork: artwork))
        renderer.scale = 1
        renderer.isOpaque = false
        return renderer.cgImage
    }
    guard let roundedImage else {
        FileHandle.standardError.write(Data("rounded render failed\n".utf8))
        exit(1)
    }
    try write(roundedImage, side: 512, to: roundedURL)
}

let temporaryDirectory = FileManager.default.temporaryDirectory
    .appendingPathComponent("wechatbridge-icon-\(UUID().uuidString)", isDirectory: true)
try FileManager.default.createDirectory(at: temporaryDirectory, withIntermediateDirectories: true)
defer { try? FileManager.default.removeItem(at: temporaryDirectory) }
let iconset = temporaryDirectory.appendingPathComponent("AppIcon.iconset", isDirectory: true)
try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)

// The set macOS actually asks for. 16 and 32 carry the menu-bar-adjacent sizes,
// so they matter more than their pixel count suggests.
let variants: [(name: String, side: Int)] = [
    ("icon_16x16", 16), ("icon_16x16@2x", 32),
    ("icon_32x32", 32), ("icon_32x32@2x", 64),
    ("icon_128x128", 128), ("icon_128x128@2x", 256),
    ("icon_256x256", 256), ("icon_256x256@2x", 512),
    ("icon_512x512", 512), ("icon_512x512@2x", 1024),
]
for variant in variants {
    try write(rendered, side: variant.side, to: iconset.appendingPathComponent("\(variant.name).png"))
}

let iconutil = Process()
iconutil.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
iconutil.arguments = ["-c", "icns", iconset.path, "-o", outputURL.path]
try iconutil.run()
iconutil.waitUntilExit()
if iconutil.terminationStatus != 0 {
    FileHandle.standardError.write(Data("iconutil failed\n".utf8))
    exit(1)
}
try? FileManager.default.removeItem(at: iconset)

let size = (try? FileManager.default.attributesOfItem(atPath: outputURL.path)[.size] as? Int) ?? 0
print("wrote \(pngURL.path) and \(outputURL.path) (\(size) bytes)")
if let roundedURL { print("wrote \(roundedURL.path) (512 × 512, no grid margin)") }
