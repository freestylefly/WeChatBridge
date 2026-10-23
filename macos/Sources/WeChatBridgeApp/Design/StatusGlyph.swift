import AppKit

/// The monochrome menu-bar version of the app icon.
enum StatusGlyph {
    private static let size = NSSize(width: 30, height: 22)
    private static let templatePNG = """
        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
        yXoTrYsAAAAASUVORK5CYII=
        """

    static var image: NSImage {
        let data = Data(base64Encoded: templatePNG, options: .ignoreUnknownCharacters)
        let image = data.flatMap(NSImage.init(data:)) ?? NSImage(size: size)
        image.size = size
        image.isTemplate = true
        return image
    }
}
