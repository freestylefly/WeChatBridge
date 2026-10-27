import XCTest
@testable import WeChatBridgeiOS

final class TargetProfileTests: XCTestCase {
    func testBuiltInTable() {
        let formats = Dictionary(uniqueKeysWithValues: TargetProfile.builtIn.map { ($0.id, $0.format) })
        XCTAssertEqual(TargetProfile.builtIn.map(\.id), ["chatgpt", "doubao", "qwen", "workbuddy", "claude", "deepseek", "obsidian", "folder", "other"])
        XCTAssertEqual(formats["chatgpt"], .archive)
        XCTAssertEqual(formats["doubao"], .transcriptAndImages)
        XCTAssertEqual(formats["qwen"], .transcriptAndImages)
        XCTAssertEqual(formats["workbuddy"], .archive)
        XCTAssertEqual(formats["obsidian"], .knowledgeNote)
        XCTAssertEqual(formats["folder"], .folder)
        XCTAssertEqual(formats["other"], .archive)
    }

    func testLogoForTargetsTheProjectShipsArtFor() {
        XCTAssertEqual(TargetProfile.builtIn.compactMap(\.logo), ["Logo-chatgpt", "Logo-doubao", "Logo-qwen", "Logo-workbuddy", "Logo-claude", "Logo-obsidian"])
    }

    func testHint() {
        XCTAssertEqual(TargetProfile.builtIn[1].hint, "请在下一屏选择「豆包」")
        XCTAssertEqual(TargetProfile.builtIn.first { $0.id == "obsidian" }?.hint, "")
    }

    func testPasteHintOnlyForTextDroppersWithPrompt() throws {
        let doubao = try XCTUnwrap(TargetProfile.builtIn.first { $0.id == "doubao" })
        XCTAssertEqual(doubao.sendHint(hasPrompt: true), "请在下一屏选择「豆包」\n提示词已复制，在输入框长按粘贴")
        XCTAssertEqual(doubao.sendHint(hasPrompt: false), "请在下一屏选择「豆包」")
        let chatgpt = try XCTUnwrap(TargetProfile.builtIn.first { $0.id == "chatgpt" })
        XCTAssertEqual(chatgpt.sendHint(hasPrompt: true), chatgpt.hint)
    }

    func testArrangedAppliesOrderHiddenOverridesAndLastUsed() {
        let arranged = TargetProfile.arranged(
            TargetProfile.builtIn,
            order: ["qwen", "chatgpt"],
            hidden: ["deepseek", "other"],
            overrides: ["chatgpt": "transcriptAndImages", "obsidian": "archive", "qwen": "bogus"],
            lastUsed: "folder"
        )
        XCTAssertEqual(arranged.map(\.id), ["folder", "qwen", "chatgpt", "doubao", "workbuddy", "claude", "obsidian"])
        XCTAssertEqual(arranged.first { $0.id == "chatgpt" }?.format, .transcriptAndImages)
        // Writers keep their format; unknown values are ignored.
        XCTAssertEqual(arranged.first { $0.id == "obsidian" }?.format, .knowledgeNote)
        XCTAssertEqual(arranged.first { $0.id == "qwen" }?.format, .transcriptAndImages)
    }

    func testUnknownIDsInOrderAreSkipped() {
        let arranged = TargetProfile.arranged(TargetProfile.builtIn, order: ["gone", "other"], hidden: [], overrides: [:], lastUsed: "gone")
        XCTAssertEqual(arranged.first?.id, "other")
        XCTAssertEqual(arranged.count, TargetProfile.builtIn.count)
    }
}
