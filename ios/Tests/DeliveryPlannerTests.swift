import WeChatBridgeCore
import XCTest
@testable import WeChatBridgeiOS

final class DeliveryPlannerTests: XCTestCase {
    private var fixtures: FixtureBatches!
    private var packages: [DeliveryPackage] = []

    override func setUpWithError() throws {
        fixtures = try FixtureBatches()
    }

    override func tearDown() {
        for package in packages { try? FileManager.default.removeItem(at: package.workDirectory) }
        fixtures = nil
    }

    private func target(_ format: DeliveryFormat, max: Int? = nil) -> TargetProfile {
        TargetProfile(id: "t", displayName: "测试", format: format, maxAttachments: max, symbol: "circle")
    }

    private func plan(_ batches: [ReadyBatch], _ target: TargetProfile) throws -> DeliveryPackage {
        let package = try DeliveryPlanner.plan(DeliveryRequest(batches: batches, target: target, prompt: "提示"))
        packages.append(package)
        return package
    }

    func testArchiveCopiesOriginalsWithUniqueNames() throws {
        let a = try fixtures.batch("group-a.zip")
        let b = try fixtures.batch("group-a.zip")
        let package = try plan([a, b], target(.archive))
        let urls = try XCTUnwrap(package.shares.first)
        XCTAssertEqual(urls.map(\.lastPathComponent), ["group-a.zip", "group-a (2).zip"])
        XCTAssertTrue(urls.allSatisfy { $0.path.hasPrefix(package.workDirectory.path) })
        XCTAssertEqual(try Data(contentsOf: urls[0]), try Data(contentsOf: a.items[0].url))
        XCTAssertEqual(package.omitted, [])
        XCTAssertEqual(package.prompt, "提示")
    }

    func testSingleBatchSendsOriginalTranscriptBytesFirstThenImages() throws {
        let batch = try fixtures.batch("group-a.zip")
        let package = try plan([batch], target(.transcriptAndImages))
        let urls = try XCTUnwrap(package.shares.first)
        XCTAssertEqual(urls.map(\.lastPathComponent), ["聊天记录.txt", "微信图片_20261020_1.png"])
        let original = try XCTUnwrap(WeChatNativeArchive.transcript(Data(contentsOf: batch.items[0].url)))
        XCTAssertEqual(try String(contentsOf: urls[0], encoding: .utf8), original.body)
        XCTAssertEqual(Set(package.omitted), ["录屏_1.mp4", "需求文档.pdf"])
        XCTAssertFalse(try String(contentsOf: urls[0], encoding: .utf8).contains("提示"))
    }

    func testMergeRenamesConflictingImageAndRewritesReference() throws {
        let package = try plan([try fixtures.batch("group-a.zip"), try fixtures.batch("group-a-later.zip")], target(.transcriptAndImages))
        let urls = try XCTUnwrap(package.shares.first)
        XCTAssertEqual(urls.map(\.lastPathComponent), ["聊天记录.txt", "微信图片_20261020_1.png", "微信图片_20261020_1 (2).png"])
        let text = try String(contentsOf: urls[0], encoding: .utf8)
        let records = try WeChatTranscriptRecord.parse(text)
        XCTAssertEqual(records.count, 11)
        XCTAssertEqual(records[1].text, "[图片] 微信图片_20261020_1.png")
        XCTAssertEqual(records[9].text, "[图片] 微信图片_20261020_1 (2).png")
    }

    func testIdenticalImagesAreNotDuplicated() throws {
        let package = try plan([try fixtures.batch("group-a.zip"), try fixtures.batch("group-a.zip")], target(.transcriptAndImages))
        XCTAssertEqual(package.shares.flatMap { $0 }.map(\.lastPathComponent), ["聊天记录.txt", "微信图片_20261020_1.png"])
        XCTAssertEqual(try WeChatTranscriptRecord.parse(String(contentsOf: package.shares[0][0], encoding: .utf8)).count, 8)
    }

    func testMaxAttachmentsSplitsWithTranscriptFirst() throws {
        let package = try plan([try fixtures.batch("group-a.zip"), try fixtures.batch("group-a-later.zip")], target(.transcriptAndImages, max: 2))
        XCTAssertEqual(package.shares.map { $0.map(\.lastPathComponent) }, [
            ["聊天记录.txt", "微信图片_20261020_1.png"],
            ["微信图片_20261020_1 (2).png"],
        ])
    }

    func testArchiveWithoutTextCannotBeUnpacked() throws {
        XCTAssertThrowsError(try plan([try fixtures.batch("files-only.zip")], target(.transcriptAndImages)))
        let package = try plan([try fixtures.batch("files-only.zip")], target(.archive))
        XCTAssertEqual(package.fileCount, 1)
    }

    func testPlainTextGoesOutUnderItsOwnName() throws {
        let batch = try fixtures.batch("text-note.zip")
        let package = try plan([batch], target(.transcriptAndImages))
        let urls = try XCTUnwrap(package.shares.first)
        XCTAssertEqual(urls.map(\.lastPathComponent), ["资料合集.txt"])
        let original = try XCTUnwrap(WeChatNativeArchive.transcript(Data(contentsOf: batch.items[0].url)))
        XCTAssertEqual(try String(contentsOf: urls[0], encoding: .utf8), original.body)
    }

    func testPlainTextMergesWithChatInPickedOrder() throws {
        let package = try plan([try fixtures.batch("group-a.zip"), try fixtures.batch("text-note.zip")], target(.transcriptAndImages))
        let urls = try XCTUnwrap(package.shares.first)
        XCTAssertEqual(urls[0].lastPathComponent, "聊天记录.txt")
        let text = try String(contentsOf: urls[0], encoding: .utf8)
        let chat = try XCTUnwrap(text.range(of: "早上好"))
        let note = try XCTUnwrap(text.range(of: "资料合集"))
        XCTAssertLessThan(chat.lowerBound, note.lowerBound)
    }

    func testWritersHaveNoSharePlan() throws {
        XCTAssertThrowsError(try plan([try fixtures.batch("group-a.zip")], target(.knowledgeNote)))
    }

    func testUniqueName() {
        var used: Set<String> = ["a.png", "a (2).png", "b"]
        XCTAssertEqual(DeliveryPlanner.uniqueName("a.png", used: &used), "a (3).png")
        XCTAssertEqual(DeliveryPlanner.uniqueName("b", used: &used), "b (2)")
        XCTAssertEqual(DeliveryPlanner.uniqueName("c.png", used: &used), "c.png")
    }
}
