@testable import WeChatBridgeApp
import Foundation
import WeChatBridgeCore
import XCTest

final class KnowledgeDeliveryTests: XCTestCase {
    /// 聊天记录.txt with three messages (甲 twice, 乙 once), the last one
    /// naming images/photo.png, plus that image — the same fixture
    /// WeChatNativeArchiveTests parses.
    private let storedZIP = "UEsDBBQAAAgAAMBAJV0fGRySiQAAAIkAAAAQAAAA6IGK5aSp6K6w5b2VLnR4dMK355SyCjIwMjblubQ55pyINeaXpSAwODowNQrkvaDlpb0K56ys5LqM6KGMCgrCt+S5mQoyMDI25bm0OeaciDXml6UgMDg6MDUK5L2g5aW9CuesrOS6jOihjAoKwrfnlLIKMjAyNuW5tDnmnIg15pelIDA4OjA2CmltYWdlcy9waG90by5wbmcKUEsDBBQAAAAAAMBAJV2KfiaRIAAAACAAAAAQAAAAaW1hZ2VzL3Bob3RvLnBuZwABAgMEBQYHCAkKCwwNDg8QERITFBUWFxgZGhscHR4fUEsBAhQDFAAACAAAwEAlXR8ZHJKJAAAAiQAAABAAAAAAAAAAAAAAAIABAAAAAOiBiuWkqeiusOW9lS50eHRQSwECFAMUAAAAAADAQCVdin4mkSAAAAAgAAAAEAAAAAAAAAAAAAAAgAG3AAAAaW1hZ2VzL3Bob3RvLnBuZ1BLBQYAAAAAAgACAHwAAAAFAQAAAAA="

    private var root: URL!
    private var destination: URL!
    private let fm = FileManager.default

    override func setUpWithError() throws {
        root = fm.temporaryDirectory.appendingPathComponent("wechatbridge-knowledge-tests-" + UUID().uuidString, isDirectory: true)
        destination = root.appendingPathComponent("Notes", isDirectory: true)
        try fm.createDirectory(at: destination, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws { try fm.removeItem(at: root) }

    private func source() throws -> URL {
        let url = root.appendingPathComponent("聊天记录.zip")
        try XCTUnwrap(Data(base64Encoded: storedZIP)).write(to: url)
        return url
    }

    /// The folder entry's shape, the same as the Obsidian entry's: the note
    /// and its 附件/ sit in a 微信流 subfolder of the chosen folder, never
    /// directly in the folder itself.
    func testFolderDeliveryWritesNoteAndAttachmentsIntoAWeChatFlowSubfolder() throws {
        let notes = try KnowledgeDelivery.deliver(
            urls: [try source()],
            folderPath: destination.path,
            subfolder: "",
            chatName: "项目群",
            sceneName: nil
        )
        let flow = destination.appendingPathComponent("微信流", isDirectory: true)
        let note = flow.appendingPathComponent("项目群的聊天.md")
        XCTAssertEqual(notes, [note])
        XCTAssertEqual(try fm.contentsOfDirectory(atPath: destination.path), ["微信流"])
        XCTAssertEqual(try fm.contentsOfDirectory(atPath: flow.path).sorted(), ["附件", "项目群的聊天.md"])

        let markdown = try String(contentsOf: note, encoding: .utf8)
        XCTAssertTrue(markdown.contains("# 项目群的聊天"))
        XCTAssertTrue(markdown.contains("甲"))
        XCTAssertTrue(markdown.contains("[[附件/聊天记录.zip]]"))
        XCTAssertTrue(markdown.contains("![[附件/photo.png]]"))

        let attachments = try fm.contentsOfDirectory(atPath: flow.appendingPathComponent("附件").path).sorted()
        XCTAssertEqual(attachments, ["photo.png", "聊天记录.zip"])
    }

    /// A folder the user has not got (deleted, unmounted) is a failure, not a
    /// silently recreated directory — the subfolder never brings it back.
    func testFolderDeliveryRejectsAMissingDestinationFolder() throws {
        let archive = try source()
        let missing = root.appendingPathComponent("gone", isDirectory: true)
        XCTAssertThrowsError(
            try KnowledgeDelivery.deliver(urls: [archive], folderPath: missing.path, subfolder: "", chatName: nil, sceneName: nil)
        )
        XCTAssertFalse(fm.fileExists(atPath: missing.path))
        XCTAssertEqual(try fm.contentsOfDirectory(atPath: destination.path), [])
    }

    /// A same-named note WeChatBridge did not write is never touched; the new
    /// note takes the next free number instead.
    func testFolderDeliveryNumbersANoteItDidNotWrite() throws {
        let flow = destination.appendingPathComponent("微信流", isDirectory: true)
        let existing = flow.appendingPathComponent("项目群的聊天.md")
        try fm.createDirectory(at: flow, withIntermediateDirectories: true)
        try Data("user's own note".utf8).write(to: existing)
        let notes = try KnowledgeDelivery.deliver(
            urls: [try source()],
            folderPath: destination.path,
            subfolder: "",
            chatName: "项目群",
            sceneName: nil
        )
        XCTAssertEqual(notes, [flow.appendingPathComponent("项目群的聊天 2.md")])
        XCTAssertEqual(try String(contentsOf: existing, encoding: .utf8), "user's own note")
    }

    /// A subfolder the user named is honoured level by level, the same way the
    /// Obsidian entry honours one inside the vault.
    func testFolderDeliveryHonoursACustomSubfolder() throws {
        let notes = try KnowledgeDelivery.deliver(
            urls: [try source()],
            folderPath: destination.path,
            subfolder: "参考/微信流",
            chatName: "项目群",
            sceneName: nil
        )
        XCTAssertEqual(notes, [destination.appendingPathComponent("参考/微信流/项目群的聊天.md")])
        XCTAssertEqual(try fm.contentsOfDirectory(atPath: destination.path), ["参考"])
        XCTAssertTrue(fm.fileExists(atPath: destination.appendingPathComponent("参考/微信流/附件/聊天记录.zip").path))
    }

    /// The Obsidian entry keeps its own landing place: the note goes into the
    /// 微信流 subfolder (or the one the user named), not the vault root.
    func testObsidianDeliveryKeepsItsSubfolder() throws {
        let vault = root.appendingPathComponent("Vault", isDirectory: true)
        try fm.createDirectory(at: vault, withIntermediateDirectories: true)
        let notes = try KnowledgeDelivery.deliver(
            urls: [try source()],
            vaultPath: vault.path,
            subfolder: "参考/微信流",
            chatName: "项目群",
            sceneName: nil
        )
        XCTAssertEqual(notes, [vault.appendingPathComponent("参考/微信流/项目群的聊天.md")])
        XCTAssertEqual(try fm.contentsOfDirectory(atPath: vault.path), ["参考"])
        XCTAssertTrue(fm.fileExists(atPath: vault.appendingPathComponent("参考/微信流/附件/聊天记录.zip").path))
    }
}
