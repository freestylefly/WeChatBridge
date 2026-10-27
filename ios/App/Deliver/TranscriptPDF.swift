import UIKit

extension DeliveryPackage {
    /// The transcript and its images folded into one PDF, for targets that
    /// keep only the images when a share mixes them with the TXT. A share
    /// with no images stays as it is. An image that does not decode joins
    /// `omitted`, so it is exported rather than lost.
    func foldedIntoPDF() throws -> DeliveryPackage {
        guard let group = shares.first, let transcript = group.first else { return self }
        let images = shares.flatMap { $0 }.dropFirst().filter { DeliveryPlanner.isImage($0.lastPathComponent) }
        guard !images.isEmpty else { return self }
        let (pdf, drawn) = try TranscriptPDF.make(transcript: transcript, images: Array(images))
        let undrawn = images.filter { !drawn.contains($0) }.map(\.lastPathComponent)
        return DeliveryPackage(
            workDirectory: workDirectory, shares: [[pdf]], prompt: prompt,
            omitted: undrawn + omitted, inPDF: Set(drawn.map(\.lastPathComponent))
        )
    }
}

/// One A4 PDF of the transcript, each image drawn under the line that
/// mentions it; images no line mentions go at the end. Returns the images it
/// could draw.
enum TranscriptPDF {
    static func make(transcript: URL, images: [URL]) throws -> (url: URL, drawn: [URL]) {
        let body = try String(contentsOf: transcript, encoding: .utf8)
        let page = CGRect(x: 0, y: 0, width: 595, height: 842)
        let margin: CGFloat = 40
        let box = CGSize(width: page.width - margin * 2, height: page.height - margin * 2)
        let style: [NSAttributedString.Key: Any] = [.font: UIFont.systemFont(ofSize: 11), .foregroundColor: UIColor.black]

        var unplaced = images
        var drawn: [URL] = []
        let text = NSMutableAttributedString()
        func appendImage(_ url: URL) {
            guard let image = UIImage(contentsOfFile: url.path), image.size.width > 0, image.size.height > 0 else { return }
            // Half a page at most, so a tall screenshot never needs a page of its own.
            let scale = min(1, box.width / image.size.width, box.height / 2 / image.size.height)
            let attachment = NSTextAttachment()
            attachment.image = image
            attachment.bounds = CGRect(x: 0, y: 0, width: image.size.width * scale, height: image.size.height * scale)
            text.append(NSAttributedString(attachment: attachment))
            text.append(NSAttributedString(string: "\n", attributes: style))
            drawn.append(url)
        }
        for line in body.components(separatedBy: .newlines) {
            text.append(NSAttributedString(string: line + "\n", attributes: style))
            if let index = unplaced.firstIndex(where: { line.contains("[图片] \($0.lastPathComponent)") }) {
                appendImage(unplaced.remove(at: index))
            }
        }
        unplaced.forEach(appendImage)

        let storage = NSTextStorage(attributedString: text)
        let layout = NSLayoutManager()
        storage.addLayoutManager(layout)
        var containers: [NSTextContainer] = []
        repeat {
            let container = NSTextContainer(size: box)
            container.lineFragmentPadding = 0
            layout.addTextContainer(container)
            containers.append(container)
        } while NSMaxRange(layout.glyphRange(for: containers[containers.count - 1])) < layout.numberOfGlyphs && containers.count < 1000

        let url = transcript.deletingPathExtension().appendingPathExtension("pdf")
        try UIGraphicsPDFRenderer(bounds: page).writePDF(to: url) { context in
            for container in containers {
                context.beginPage()
                let range = layout.glyphRange(for: container)
                layout.drawBackground(forGlyphRange: range, at: CGPoint(x: margin, y: margin))
                layout.drawGlyphs(forGlyphRange: range, at: CGPoint(x: margin, y: margin))
            }
        }
        return (url, drawn)
    }
}
