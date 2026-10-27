import Foundation
import UniformTypeIdentifiers
import WeChatBridgeCore

struct DeliveryRequest {
    /// One batch, or several for a merged delivery.
    let batches: [ReadyBatch]
    let target: TargetProfile
    let prompt: String?
}

struct DeliveryPackage {
    /// Removed when the delivery ends, and at the next launch otherwise.
    let workDirectory: URL
    /// One share sheet per group; the transcript is always first in the first.
    let shares: [[URL]]
    let prompt: String?
    /// File names this format leaves out (videos, documents), for the preview.
    let omitted: [String]
    /// Images drawn into the PDF, which the export leaves out; empty when
    /// the images go as files.
    var inPDF: Set<String> = []

    var fileCount: Int { shares.reduce(0) { $0 + $1.count } }

    /// Just the transcript: 豆包 and 千问 read it whole, and the images and
    /// attachments it names go to 「我的iPhone › 微信流」 instead (seen on
    /// device: 千问 keeps only the images when a share mixes them with a TXT).
    func transcriptOnly() -> DeliveryPackage {
        guard let transcript = shares.first?.first else { return self }
        let rest = shares.flatMap { $0 }.dropFirst().map(\.lastPathComponent)
        return DeliveryPackage(workDirectory: workDirectory, shares: [[transcript]], prompt: prompt, omitted: rest + omitted)
    }
}

/// Turns batches into the files one share sheet hands over. Pure file work, no
/// UIKit, so every rule here is covered by unit tests.
enum DeliveryPlanner {
    enum Failure: LocalizedError {
        case noArchive
        case unsupportedFormat

        var errorDescription: String? {
            switch self {
            case .noArchive: "这批内容里没有可读的文本，可以改为发送原始文件。"
            case .unsupportedFormat: "这个目标不通过分享面板投递。"
            }
        }
    }

    static let transcriptName = "聊天记录.txt"

    static var deliveriesRoot: URL {
        FileManager.default.temporaryDirectory.appendingPathComponent("Deliveries", isDirectory: true)
    }

    static func plan(_ request: DeliveryRequest, fileManager: FileManager = .default) throws -> DeliveryPackage {
        let work = deliveriesRoot.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try fileManager.createDirectory(at: work, withIntermediateDirectories: true)
        do {
            switch request.target.format {
            case .archive:
                return try archive(request, into: work, fileManager: fileManager)
            case .transcriptAndImages:
                return try transcriptAndImages(request, into: work, fileManager: fileManager)
            case .knowledgeNote, .folder:
                throw Failure.unsupportedFormat
            }
        } catch {
            try? fileManager.removeItem(at: work)
            throw error
        }
    }

    /// Leftovers from a delivery the app was killed during.
    static func cleanUp(fileManager: FileManager = .default) {
        try? fileManager.removeItem(at: deliveriesRoot)
    }

    // MARK: - Archive

    /// Copies, never the originals in the App Group: a receiving app or the
    /// system may touch what it is handed.
    private static func archive(_ request: DeliveryRequest, into work: URL, fileManager: FileManager) throws -> DeliveryPackage {
        var used = Set<String>()
        var urls: [URL] = []
        for item in request.batches.flatMap(\.items) {
            let name = uniqueName(item.displayName, used: &used)
            let destination = work.appendingPathComponent(name)
            try fileManager.copyItem(at: item.url, to: destination)
            urls.append(destination)
        }
        return DeliveryPackage(
            workDirectory: work,
            shares: group(urls, max: request.target.maxAttachments),
            prompt: request.prompt,
            omitted: []
        )
    }

    // MARK: - Transcript and images

    private struct Source {
        let body: String
        /// False for a text in another layout, which cannot be merged by time.
        let isChat: Bool
        let transcriptPath: String
        let directory: URL
        let names: [String]
    }

    private static func transcriptAndImages(_ request: DeliveryRequest, into work: URL, fileManager: FileManager) throws -> DeliveryPackage {
        var sources: [Source] = []
        var omitted: [String] = []
        for batch in request.batches {
            var found = false
            for item in batch.items {
                if item.url.pathExtension.lowercased() == "zip",
                   let data = try? Data(contentsOf: item.url, options: .mappedIfSafe),
                   let transcript = try WeChatNativeArchive.transcript(data) {
                    let directory = work.appendingPathComponent("src/\(batch.id.uuidString)/\(item.id.uuidString)", isDirectory: true)
                    try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
                    let names = try WeChatNativeArchive.extract(data, to: directory)
                    sources.append(Source(body: transcript.body, isChat: transcript.records != nil, transcriptPath: transcript.path, directory: directory, names: names))
                    found = true
                } else {
                    omitted.append(item.displayName)
                }
            }
            if !found { throw Failure.noArchive }
        }

        let output = work.appendingPathComponent("out", isDirectory: true)
        try fileManager.createDirectory(at: output, withIntermediateDirectories: true)

        // Images first, so a renamed duplicate is known before the merged
        // transcript is written and can point at its new name.
        var used: Set<String> = [transcriptName]
        var byName: [String: URL] = [:]
        var images: [URL] = []
        var renames: [[String: String]] = []
        for source in sources {
            var renamed: [String: String] = [:]
            for name in source.names where name != source.transcriptPath && !name.hasSuffix("/") {
                let file = source.directory.appendingPathComponent(name)
                let leaf = (name as NSString).lastPathComponent
                guard isImage(leaf) else {
                    omitted.append(leaf)
                    continue
                }
                if let existing = byName[leaf], fileManager.contentsEqual(atPath: existing.path, andPath: file.path) {
                    continue
                }
                let finalName = uniqueName(leaf, used: &used)
                if finalName != leaf { renamed[leaf] = finalName }
                let destination = output.appendingPathComponent(finalName)
                try fileManager.copyItem(at: file, to: destination)
                if byName[leaf] == nil { byName[leaf] = destination }
                images.append(destination)
            }
            renames.append(renamed)
        }

        // A text that is not a chat record keeps its own name.
        let textName = sources.contains(where: \.isChat) ? transcriptName : (sources[0].transcriptPath as NSString).lastPathComponent
        let transcriptURL = output.appendingPathComponent(textName)
        if sources.count == 1 {
            // Byte for byte: a single export goes out exactly as WeChat wrote it.
            let original = sources[0].directory.appendingPathComponent(sources[0].transcriptPath)
            try fileManager.copyItem(at: original, to: transcriptURL)
        } else {
            let bodies = zip(sources, renames).map { source, renamed in
                renamed.reduce(source.body) { body, pair in
                    body.replacingOccurrences(of: "[图片] \(pair.key)", with: "[图片] \(pair.value)")
                }
            }
            // Only chat records carry times to interleave by; anything else
            // goes out in the order it was picked.
            let merged = sources.allSatisfy(\.isChat)
                ? try TranscriptMerge.merge(bodies)
                : bodies.map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }.joined(separator: "\n\n")
            try merged.write(to: transcriptURL, atomically: true, encoding: .utf8)
        }

        try? fileManager.removeItem(at: work.appendingPathComponent("src"))
        return DeliveryPackage(
            workDirectory: work,
            shares: group([transcriptURL] + images, max: request.target.maxAttachments),
            prompt: request.prompt,
            omitted: omitted
        )
    }

    // MARK: - Helpers

    static func isImage(_ name: String) -> Bool {
        guard let type = UTType(filenameExtension: (name as NSString).pathExtension) else { return false }
        return type.conforms(to: .image)
    }

    /// `name.jpg`, then `name (2).jpg`, `name (3).jpg`…
    static func uniqueName(_ name: String, used: inout Set<String>) -> String {
        var candidate = name
        var index = 2
        let base = (name as NSString).deletingPathExtension
        let ext = (name as NSString).pathExtension
        while used.contains(candidate) {
            candidate = ext.isEmpty ? "\(base) (\(index))" : "\(base) (\(index)).\(ext)"
            index += 1
        }
        used.insert(candidate)
        return candidate
    }

    /// Splits into share sheets of at most `max` files. The transcript (first
    /// url) only goes in the first group either way.
    static func group(_ urls: [URL], max: Int?) -> [[URL]] {
        guard let max, max > 0, urls.count > max else { return urls.isEmpty ? [] : [urls] }
        return stride(from: 0, to: urls.count, by: max).map { Array(urls[$0..<min($0 + max, urls.count)]) }
    }
}
