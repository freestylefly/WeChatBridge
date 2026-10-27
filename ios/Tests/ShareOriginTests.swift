import WeChatBridgeCore
import XCTest
@testable import WeChatBridgeiOS

final class ShareOriginTests: XCTestCase {
    func testXLinkWithTitle() throws {
        let url = URL(string: "https://x.com/kongge/status/1")!
        let origin = try XCTUnwrap(ShareOrigin.make(url: url, title: "空格.space", texts: [url.absoluteString]))
        XCTAssertNil(origin.text)
        XCTAssertEqual(origin.heading, "空格.space")
        // The title's dot is not an extension: the note stays a TXT.
        XCTAssertEqual(origin.noteFileName, "空格.space.txt")
        XCTAssertEqual(origin.note, "空格.space\nhttps://x.com/kongge/status/1\n")
        XCTAssertEqual(ShareSource.app(for: url), "X")
    }

    func testLinkInsideText() throws {
        let blurb = "7.94 复制打开抖音，看看【某某的作品】 https://v.douyin.com/abc/ 01/02"
        let origin = try XCTUnwrap(ShareOrigin.make(url: nil, title: nil, texts: [blurb]))
        XCTAssertEqual(origin.url?.host, "v.douyin.com")
        XCTAssertEqual(ShareSource.app(for: origin.url!), "抖音")
    }

    func testSourceFromDomain() {
        XCTAssertEqual(ShareSource.app(for: URL(string: "https://mp.weixin.qq.com/s/x")!), "微信")
        XCTAssertEqual(ShareSource.app(for: URL(string: "https://www.xiaohongshu.com/a")!), "小红书")
        XCTAssertNil(ShareSource.app(for: URL(string: "https://notx.com/a")!))
        XCTAssertNil(ShareOrigin.make(url: nil, title: "  ", texts: [" "]))
    }

    func testLinkSummary() {
        let origin = ShareOrigin(url: URL(string: "https://www.example.com/p")!, title: "标题", text: "正文第一行\n第二行")
        let summary = RelaySummary.make(origin: origin, otherFiles: 0)
        XCTAssertEqual(summary.kind, .link)
        XCTAssertEqual(summary.title, "标题")
        XCTAssertEqual(summary.preview, ["正文第一行", "🔗 example.com"])
        XCTAssertEqual(RelayCardView.counts(summary), "链接")
        XCTAssertEqual(RelaySummary.source(origin: origin, kind: .link, items: []), "网页")
        XCTAssertEqual(RelaySummary.source(origin: nil, kind: .chat, items: []), "微信")
    }
}
