import Foundation
import WeChatBridgeCore

/// Watches the 蓝奏云 share folder that carries 微信流's downloads and reports
/// when a build newer than the running one appears.
///
/// This channel exists because the app ships through 蓝奏云 rather than a
/// Sparkle appcast: a password-protected folder page exposes its listing over
/// one AJAX endpoint, so "is there an update" is a page fetch, a form POST
/// carrying the share password, and a filename comparison. What a hit offers
/// is the file's own page URL — 前往下载 opens it in the browser and the
/// install stays the user's drag-to-Applications on the mounted DMG, rather
/// than an in-app download fighting 蓝奏云's anti-leech chain.
@MainActor
final class LanzouUpdateChecker: ObservableObject {
    /// The share everyone opens to download 微信流, and its password. The
    /// password is public — it rides along in every release announcement —
    /// so hardcoding it only saves the user typing it.
    nonisolated static let sharePage = URL(string: "https://wwbsr.lanzout.com/b0umjur8h")!
    nonisolated static let sharePassword = "向明"

    /// One downloadable file in the share, reduced to what an update decision
    /// and a 前往下载 button need.
    struct Release: Equatable {
        let fileName: String
        let version: [Int]
        let build: Int
        let size: String
        /// The file's own 蓝奏云 page — `id` in the listing already carries
        /// the `?webpage=` token that opens it without the share password.
        let pageURL: URL

        var versionLabel: String { version.map(String.init).joined(separator: ".") }
    }

    enum State: Equatable {
        case idle
        case checking
        case upToDate
        case updateAvailable(Release)
        case failed(String)
    }

    enum CheckError: LocalizedError {
        case badResponse(Int)
        case malformedFolderPage
        case shareRejected(String)
        case emptyShare

        var errorDescription: String? {
            switch self {
            case .badResponse(let code):
                L10n.format("蓝奏云返回 HTTP %d", code)
            case .malformedFolderPage:
                L10n.text("无法解析蓝奏云分享页")
            case .shareRejected(let info):
                info.isEmpty
                    ? L10n.text("蓝奏云拒绝了文件列表请求")
                    : L10n.format("蓝奏云：%@", info)
            case .emptyShare:
                L10n.text("分享文件夹里没有可识别的安装包")
            }
        }
    }

    @Published private(set) var state: State = .idle

    var isChecking: Bool { state == .checking }
    var availableUpdate: Release? {
        if case .updateAvailable(let release) = state { return release }
        return nil
    }

    private let folderURL: URL
    private let password: String
    private let session: URLSession
    private let currentVersion: [Int]
    private let currentBuild: Int

    init(
        folderURL: URL = LanzouUpdateChecker.sharePage,
        password: String = LanzouUpdateChecker.sharePassword,
        session: URLSession = .shared,
        currentVersion: [Int] = AppVersion.components,
        currentBuild: Int = AppVersion.buildNumber
    ) {
        self.folderURL = folderURL
        self.password = password
        self.session = session
        self.currentVersion = currentVersion
        self.currentBuild = currentBuild
    }

    func check() async {
        guard !isChecking else { return }
        state = .checking
        do {
            let html = try await folderPage()
            let form = try listingForm(in: html)
            let files = try await listing(form: form)
            let releases = files.compactMap { Self.release(from: $0, base: folderURL) }
            guard let latest = releases.max(by: Self.outranks) else {
                throw CheckError.emptyShare
            }
            if Self.newer(latest.version, latest.build, than: currentVersion, currentBuild) {
                state = .updateAvailable(latest)
            } else {
                state = .upToDate
            }
        } catch let error as CheckError {
            state = .failed(error.localizedDescription)
        } catch {
            state = .failed(error.localizedDescription)
        }
    }

    /// Ordering for the listing: higher version wins, then higher build, then
    /// a .dmg beats a .zip of the same numbers — the disk image is what the
    /// download button should land the user on.
    private static func outranks(_ a: Release, _ b: Release) -> Bool {
        func rank(_ r: Release) -> [Int] {
            r.version + [r.build, r.fileName.hasSuffix(".dmg") ? 1 : 0]
        }
        return rank(a).lexicographicallyPrecedes(rank(b))
    }

    private static func newer(
        _ lhsVersion: [Int], _ lhsBuild: Int,
        than rhsVersion: [Int], _ rhsBuild: Int
    ) -> Bool {
        for index in 0..<max(lhsVersion.count, rhsVersion.count) {
            let lhs = index < lhsVersion.count ? lhsVersion[index] : 0
            let rhs = index < rhsVersion.count ? rhsVersion[index] : 0
            if lhs != rhs { return lhs > rhs }
        }
        return lhsBuild > rhsBuild
    }

    // MARK: - 蓝奏云 wire format

    private static let userAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) "
        + "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36"

    private func folderPage() async throws -> String {
        var request = URLRequest(url: folderURL, timeoutInterval: 20)
        request.setValue(Self.userAgent, forHTTPHeaderField: "User-Agent")
        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
            throw CheckError.badResponse((response as? HTTPURLResponse)?.statusCode ?? -1)
        }
        guard let html = String(data: data, encoding: .utf8) else {
            throw CheckError.malformedFolderPage
        }
        return html
    }

    /// The folder page embeds its listing POST's fields in a jQuery block.
    /// Values come in three spellings — `'fid':14056937`, `'uid':'1915864'`,
    /// and `'t':iay0sp` where the token hides behind a randomly named `var`
    /// declared earlier in the page — so each key resolves literal first,
    /// then chases the variable.
    private func listingForm(in html: String) throws -> [(String, String)] {
        func param(_ key: String) throws -> String {
            let pattern = "'\(key)'\\s*:\\s*(?:'([^']*)'|([A-Za-z0-9_$]+))"
            guard let match = Self.firstMatch(pattern, in: html) else {
                throw CheckError.malformedFolderPage
            }
            if let literal = Self.group(1, of: match, in: html), !literal.isEmpty {
                return literal
            }
            guard let token = Self.group(2, of: match, in: html) else {
                throw CheckError.malformedFolderPage
            }
            if token.allSatisfy(\.isNumber) { return token }
            let varPattern = "\\b\(NSRegularExpression.escapedPattern(for: token))\\s*=\\s*'([^']*)'"
            guard let varMatch = Self.firstMatch(varPattern, in: html),
                  let value = Self.group(1, of: varMatch, in: html) else {
                throw CheckError.malformedFolderPage
            }
            return value
        }

        return [
            ("lx", "2"),
            ("fid", try param("fid")),
            ("uid", try param("uid")),
            ("puid", try param("puid")),
            ("pg", "1"),
            ("rep", "0"),
            ("t", try param("t")),
            ("k", try param("k")),
            ("up", "1"),
            ("ls", "1"),
            ("pwd", password),
        ]
    }

    private struct ListedFile: Decodable {
        let name_all: String
        let id: String
        let size: String
    }

    /// `zt` is 蓝奏云's status field: 1 is success, 3 means the share password
    /// was wrong, the rest are opaque refusals whose `info` — when it is a
    /// string — is the only explanation the API gives.
    private struct ListingResponse: Decodable {
        let zt: Int
        let infoText: String?
        let text: [ListedFile]?

        enum CodingKeys: String, CodingKey { case zt, info, text }

        init(from decoder: Decoder) throws {
            let container = try decoder.container(keyedBy: CodingKeys.self)
            zt = try container.decode(Int.self, forKey: .zt)
            infoText = try? container.decode(String.self, forKey: .info)
            text = try? container.decode([ListedFile].self, forKey: .text)
        }
    }

    private func listing(form: [(String, String)]) async throws -> [ListedFile] {
        let fid = form.first { $0.0 == "fid" }?.1 ?? ""
        guard let ajaxURL = URL(string: "/filemoreajax.php?file=\(fid)", relativeTo: folderURL) else {
            throw CheckError.malformedFolderPage
        }
        var request = URLRequest(url: ajaxURL.absoluteURL, timeoutInterval: 20)
        request.httpMethod = "POST"
        request.setValue(Self.userAgent, forHTTPHeaderField: "User-Agent")
        request.setValue(folderURL.absoluteString, forHTTPHeaderField: "Referer")
        request.setValue("XMLHttpRequest", forHTTPHeaderField: "X-Requested-With")
        request.setValue(
            "application/x-www-form-urlencoded; charset=UTF-8",
            forHTTPHeaderField: "Content-Type"
        )
        request.httpBody = Data(Self.formEncode(form).utf8)

        let (data, response) = try await session.data(for: request)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
            throw CheckError.badResponse((response as? HTTPURLResponse)?.statusCode ?? -1)
        }
        guard let listing = try? JSONDecoder().decode(ListingResponse.self, from: data) else {
            throw CheckError.malformedFolderPage
        }
        guard listing.zt == 1, let files = listing.text else {
            throw CheckError.shareRejected(listing.infoText ?? "")
        }
        return files
    }

    /// Filenames on the share look like `微信流-0.1.15-b22-universal.dmg` or
    /// `WeChatBridge-0.1.15-build21-macOS-universal.zip`: a three-part version,
    /// then a build number behind `-b` or `-build`. Anything that is not a
    /// .dmg/.zip with both pieces is not a release.
    private static func release(from file: ListedFile, base: URL) -> Release? {
        let name = file.name_all
        guard name.range(of: #"\.(dmg|zip)$"#, options: [.regularExpression, .caseInsensitive]) != nil,
              let versionRange = name.range(of: #"\d+\.\d+\.\d+"#, options: .regularExpression)
        else { return nil }

        let version = name[versionRange].split(separator: ".").compactMap { Int($0) }
        guard version.count == 3 else { return nil }

        var build = 0
        let suffix = name[versionRange.upperBound...]
        if let buildRange = suffix.range(
            of: #"-(?:b|build)(\d+)"#,
            options: [.regularExpression, .caseInsensitive]
        ), let digits = suffix[buildRange].range(of: #"\d+"#, options: .regularExpression) {
            build = Int(suffix[digits]) ?? 0
        }

        // `id` is a path, sometimes already carrying the `?webpage=` token the
        // file page needs to open without the share password.
        guard let pageURL = URL(string: "/" + file.id, relativeTo: base)?.absoluteURL else {
            return nil
        }
        return Release(
            fileName: name,
            version: version,
            build: build,
            size: file.size,
            pageURL: pageURL
        )
    }

    private static func formEncode(_ fields: [(String, String)]) -> String {
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-._~"))
        return fields.map { key, value in
            let escaped = value.addingPercentEncoding(withAllowedCharacters: allowed) ?? value
            return "\(key)=\(escaped)"
        }.joined(separator: "&")
    }

    private static func firstMatch(_ pattern: String, in text: String) -> NSTextCheckingResult? {
        try? NSRegularExpression(pattern: pattern)
            .firstMatch(in: text, range: NSRange(text.startIndex..., in: text))
    }

    private static func group(_ index: Int, of match: NSTextCheckingResult, in text: String) -> String? {
        guard let range = Range(match.range(at: index), in: text) else { return nil }
        return String(text[range])
    }
}
