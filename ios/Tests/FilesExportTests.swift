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

    func testExportsImagesAndAttachmentsButNotTheTranscript() throws {
        let folder = try XCTUnwrap(FilesExport.export(try fixtures.batch("group-a.zip"), name: "甲等3人的聊天", into: root).folder)
        let names = try FileManager.default.contentsOfDirectory(atPath: folder.path).sorted()
        XCTAssertEqual(names, ["录屏_1.mp4", "微信图片_20261020_1.png", "需求文档.pdf"].sorted())
    }

    func testMediaGoesApartForPhotos() throws {
        let media = root.appendingPathComponent("media", isDirectory: true)
        let exported = try FilesExport.export(try fixtures.batch("group-a.zip"), name: "聊天", into: root, mediaInto: media)
        XCTAssertEqual(exported.media.map(\.lastPathComponent).sorted(), ["录屏_1.mp4", "微信图片_20261020_1.png"].sorted())
        let folder = try XCTUnwrap(exported.folder)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: folder.path), ["需求文档.pdf"])
    }

    func testImagesInThePDFAreLeftOut() throws {
        let media = root.appendingPathComponent("media", isDirectory: true)
        let exported = try FilesExport.export(try fixtures.batch("group-a.zip"), name: "聊天", into: root, mediaInto: media, inPDF: ["微信图片_20261020_1.png"])
        XCTAssertEqual(exported.media.map(\.lastPathComponent), ["录屏_1.mp4"])
    }

    func testMergedChatsKeepSameNamedMediaApart() throws {
        let media = root.appendingPathComponent("media", isDirectory: true)
        let jobs = [
            FilesExport.Job(batch: try fixtures.batch("group-a.zip"), name: "聊天"),
            FilesExport.Job(batch: try fixtures.batch("group-a-later.zip"), name: "聊天"),
        ]
        let staged = try FilesExport.stage(jobs, into: root, mediaRoot: media)
        XCTAssertEqual(staged.names, ["聊天", "聊天 (2)"])
        XCTAssertEqual(staged.media.map { $0.map(\.lastPathComponent).sorted() }, [["录屏_1.mp4", "微信图片_20261020_1.png"].sorted(), ["微信图片_20261020_1.png"]])
    }

    func testMediaPhotosDidNotTakeGoIntoTheFolder() throws {
        let media = root.appendingPathComponent("media", isDirectory: true)
        let exported = try FilesExport.export(try fixtures.batch("group-a.zip"), name: "聊天", into: root, mediaInto: media)
        try FilesExport.keep(exported.media, in: "聊天", root: root)
        let names = try FileManager.default.contentsOfDirectory(atPath: root.appendingPathComponent("聊天").path).sorted()
        XCTAssertEqual(names, ["录屏_1.mp4", "微信图片_20261020_1.png", "需求文档.pdf"].sorted())
    }

    func testFailedExportKeepsTheOldFolder() throws {
        let batch = try fixtures.batch("group-a.zip")
        let folder = try XCTUnwrap(FilesExport.export(batch, name: "聊天", into: root).folder)
        try FileManager.default.removeItem(at: batch.items[0].url)
        XCTAssertThrowsError(try FilesExport.export(batch, name: "聊天", into: root))
        XCTAssertTrue(FileManager.default.fileExists(atPath: folder.appendingPathComponent("需求文档.pdf").path))
    }

    func testSideNoteSaysWhereEachGoes() {
        XCTAssertEqual(
            DeliverySheet.sideNote(["a.png", "b.mp4", "c.docx"]),
            "发送时图片和视频存到「照片」：a.png、b.mp4；附件放到「我的iPhone › 微信流」：c.docx"
        )
    }

    func testTextOnlyChatMakesNoFolder() throws {
        XCTAssertNil(try FilesExport.export(try fixtures.batch("text-note.zip"), name: "资料", into: root).folder)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: root.path), [])
    }

    func testExportingAgainReplacesTheFolder() throws {
        let batch = try fixtures.batch("group-a.zip")
        let folder = try XCTUnwrap(FilesExport.export(batch, name: "聊天", into: root).folder)
        try Data("旧".utf8).write(to: folder.appendingPathComponent("stale.txt"))
        _ = try FilesExport.export(batch, name: "聊天", into: root)
        XCTAssertFalse(FileManager.default.fileExists(atPath: folder.appendingPathComponent("stale.txt").path))
    }

    func testGuideKeepsTheFolderVisibleBeforeAnyExport() throws {
        FilesExport.placeGuide(in: root)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: root.path), ["使用说明.txt"])
    }

    func testFolderNameDropsCharactersFilesRejects() {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = .current
        let date = calendar.date(from: DateComponents(year: 2026, month: 10, day: 20, hour: 9, minute: 0))!
        XCTAssertEqual(FilesExport.folderName(title: "A/B:C", date: date), "A-B-C 10-20 09.00")
    }
}
