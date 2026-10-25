import XCTest
@testable import WeChatBridgeiOS

final class MessageTokenTests: XCTestCase {
    func testPlainTextStaysOneToken() {
        XCTAssertEqual(MessageToken.tokenize("第一行\n第二行"), [.text("第一行\n第二行")])
    }

    func testMediaPlaceholders() {
        XCTAssertEqual(MessageToken.tokenize("[图片] 微信图片_1.jpg"), [.image(name: "微信图片_1.jpg")])
        XCTAssertEqual(MessageToken.tokenize("[视频] 录屏 1.mp4"), [.video(name: "录屏 1.mp4")])
        XCTAssertEqual(MessageToken.tokenize("[文件] 需求文档.pdf"), [.file(name: "需求文档.pdf")])
    }

    func testLinkTakesLastURLAndTitleWithSpaces() {
        let url = URL(string: "https://mp.weixin.qq.com/s/abc123")!
        XCTAssertEqual(MessageToken.tokenize("[链接] 发布 清单 模板 https://mp.weixin.qq.com/s/abc123"), [.link(title: "发布 清单 模板", url: url)])
    }

    func testChannels() {
        let url = URL(string: "https://weixin.qq.com/sph/xyz789")!
        XCTAssertEqual(MessageToken.tokenize("[视频号] 产品演示 https://weixin.qq.com/sph/xyz789"), [.channels(title: "产品演示", url: url)])
    }

    func testLinkWithoutURLStaysText() {
        XCTAssertEqual(MessageToken.tokenize("[链接] 没有地址"), [.text("[链接] 没有地址")])
    }

    func testUnknownBracketsAndEmojiStayText() {
        XCTAssertEqual(MessageToken.tokenize("[破涕为笑]"), [.text("[破涕为笑]")])
        XCTAssertEqual(MessageToken.tokenize("[语音] 3秒"), [.text("[语音] 3秒")])
        XCTAssertEqual(MessageToken.tokenize("[图片]"), [.text("[图片]")])
    }

    func testMixedLinesKeepOrder() {
        let tokens = MessageToken.tokenize("看这个\n[图片] a.png\n再说")
        XCTAssertEqual(tokens, [.text("看这个"), .image(name: "a.png"), .text("再说")])
    }
}
