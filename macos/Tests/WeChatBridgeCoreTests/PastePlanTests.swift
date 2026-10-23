import WeChatBridgeCore
import Foundation
import XCTest

final class PastePlanTests: XCTestCase {
    private let a = URL(fileURLWithPath: "/tmp/聊天记录 a.zip")
    private let b = URL(fileURLWithPath: "/tmp/b.zip")

    func testNoPromptMeansJustTheFiles() {
        XCTAssertEqual(PastePlan.make(urls: [a], pathOnly: false, prompt: nil), [.files([a])])
        XCTAssertEqual(PastePlan.make(urls: [a], pathOnly: false, prompt: "  \n"), [.files([a])])
        XCTAssertEqual(PastePlan.make(urls: [a], pathOnly: true, prompt: nil), [.text("'/tmp/聊天记录 a.zip' ")])
    }

    func testAPromptBeforeTheFilesIsTwoPastes() {
        XCTAssertEqual(
            PastePlan.make(urls: [a, b], pathOnly: false, prompt: "总结一下"),
            [.text("总结一下"), .files([a, b])]
        )
        XCTAssertEqual(
            PastePlan.make(urls: [a, b], pathOnly: false, prompt: "总结一下\n"),
            [.text("总结一下"), .files([a, b])]
        )
    }

    func testAPromptForATerminalIsOneLine() {
        XCTAssertEqual(
            PastePlan.make(urls: [a], pathOnly: true, prompt: "读一下\n再总结  "),
            [.text("读一下 再总结 '/tmp/聊天记录 a.zip' ")]
        )
        XCTAssertEqual(
            PastePlan.make(urls: [a, b], pathOnly: true, prompt: "总结"),
            [.text("总结 '/tmp/聊天记录 a.zip' '/tmp/b.zip' ")]
        )
    }

    func testAManualPasteGetsTheFilesNotTheBarePrompt() {
        let plan = PastePlan.make(urls: [a], pathOnly: false, prompt: "p")
        XCTAssertEqual(PastePlan.manualPayload(plan), .files([a]))
        let line = PastePlan.make(urls: [a], pathOnly: true, prompt: "p")
        XCTAssertEqual(PastePlan.manualPayload(line), line.first)
        XCTAssertNil(PastePlan.manualPayload([]))
    }
}
