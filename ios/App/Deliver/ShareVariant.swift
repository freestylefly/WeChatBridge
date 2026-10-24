import UIKit

/// Trial routes for 千问, which kept only the images when one share mixed
/// them with the TXT. Picked in the delivery sheet until device tests decide
/// which one stays.
enum ShareVariant: String, CaseIterable, Identifiable {
    case files, text, imagesFirst, pdf

    static let key = "com.xiangming.wechatbridge.ios.qwenShareVariant"

    var id: Self { self }

    var label: String {
        switch self {
        case .files: "TXT+图"
        case .text: "文字+图"
        case .imagesFirst: "图在前"
        case .pdf: "PDF"
        }
    }

    var detail: String {
        switch self {
        case .files: "现在的发法：TXT 文件和图片"
        case .text: "聊天记录作为文字，和图片一起"
        case .imagesFirst: "图片在前，TXT 文件在后"
        case .pdf: "只发一个 PDF，图片插在聊天对应的位置"
        }
    }

    /// `group` is the planner's share: transcript first, then images.
    func items(_ group: [URL], prompt: String) throws -> [Any] {
        guard let transcript = group.first else { return [] }
        let images = Array(group.dropFirst())
        let lead: [Any] = prompt.isEmpty ? [] : [prompt]
        switch self {
        case .files:
            return lead + group
        case .imagesFirst:
            return lead + images + [transcript]
        case .text:
            let body = try String(contentsOf: transcript, encoding: .utf8)
            return [prompt.isEmpty ? body : "\(prompt)\n\n\(body)"] + images
        case .pdf:
            return lead + [try TranscriptPDF.make(transcript: transcript, images: images)]
        }
    }
}

/// One A4 PDF of the transcript, each image drawn under the line that
/// mentions it; images no line mentions go at the end.
enum TranscriptPDF {
    static func make(transcript: URL, images: [URL]) throws -> URL {
        let body = try String(contentsOf: transcript, encoding: .utf8)
        let page = CGRect(x: 0, y: 0, width: 595, height: 842)
        let margin: CGFloat = 40
        let box = CGSize(width: page.width - margin * 2, height: page.height - margin * 2)
        let style: [NSAttributedString.Key: Any] = [.font: UIFont.systemFont(ofSize: 11), .foregroundColor: UIColor.black]

        var unplaced = images
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
        return url
    }
}
