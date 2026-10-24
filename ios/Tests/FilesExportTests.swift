import WeChatBridgeCore
import XCTest
@testable import WeChatBridgeiOS

final class FilesExportTests: XCTestCase {
    private var root: URL!
    private var fixtures: FixtureBatches!

    override func setUpWithError() throws {
        fixtures = try FixtureBatches()
        root = FileManager.default.temporaryDirectory.appendingPathComponent("files-export-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    override func tearDown() {
        try? FileManager.default.removeItem(at: root)
        fixtures = nil
    }

    func testExportsTranscriptAndEveryFileFlat() throws {
        let batch = try fixtures.batch("group-a.zip")
        let folder = try FilesExport.export(batch, name: "甲等3人的聊天", into: root)
        let names = try FileManager.default.contentsOfDirectory(atPath: folder.path).sorted()
        XCTAssertEqual(names, ["录屏_1.mp4", "微信图片_20261020_1.png", "聊天记录.txt", "需求文档.pdf"].sorted())
    }

    func testExportingAgainReplacesTheFolder() throws {
        let batch = try fixtures.batch("group-a.zip")
        let folder = try FilesExport.export(batch, name: "聊天", into: root)
        try Data("旧".utf8).write(to: folder.appendingPathComponent("stale.txt"))
        _ = try FilesExport.export(batch, name: "聊天", into: root)
        XCTAssertFalse(FileManager.default.fileExists(atPath: folder.appendingPathComponent("stale.txt").path))
    }

    func testFolderNameDropsCharactersFilesRejects() {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = .current
        let date = calendar.date(from: DateComponents(year: 2026, month: 10, day: 20, hour: 9, minute: 0))!
        XCTAssertEqual(FilesExport.folderName(title: "A/B:C", date: date), "A-B-C 10-20 09.00")
    }
}
