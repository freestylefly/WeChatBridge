import WeChatBridgeCore
import Foundation
import XCTest

/// The status menu has no second line and no truncation of its own, so what a
/// 最近记录 row says is decided here — and is worth pinning down, because the
/// only other way to see it is to open the menu.
final class HistoryLabelTests: XCTestCase {
    private let zone = TimeZone(identifier: "Asia/Shanghai")!
    private let locale = Locale(identifier: "zh_Hans_CN")

    func testASingleFileIsNamedByItself() {
        let batch = makeBatch(names: ["聊天记录.zip"])
        XCTAssertEqual(HistoryLabel.name(for: batch), "聊天记录.zip")
    }

    func testMoreThanOneFileCountsTheRest() {
        let batch = makeBatch(names: ["聊天记录.zip", "b.zip", "c.zip"])
        XCTAssertEqual(HistoryLabel.name(for: batch), "聊天记录.zip 等 3 个")
    }

    func testALongNameIsCutInTheMiddleSoTheExtensionSurvives() {
        let long = String(repeating: "长", count: 40) + ".zip"
        let name = HistoryLabel.name(for: makeBatch(names: [long]), limit: HistoryLabel.menuNameLimit)

        XCTAssertEqual(name.count, HistoryLabel.menuNameLimit)
        XCTAssertTrue(name.hasSuffix(".zip"), "the extension is what tells two exports apart")
        XCTAssertTrue(name.hasPrefix("长长长"))
        XCTAssertTrue(name.contains("…"))
    }

    func testANameInsideTheLimitIsLeftAlone() {
        let batch = makeBatch(names: ["a.zip"])
        XCTAssertEqual(HistoryLabel.name(for: batch, limit: HistoryLabel.menuNameLimit), "a.zip")
    }

    func testTheHistoryPanePrefersGroupOverSceneAndFilename() {
        let batch = makeBatch(
            names: ["Zip归档.zip"],
            chatName: "Q搜索",
            sceneName: "客户复盘"
        )

        XCTAssertEqual(HistoryLabel.paneTitle(for: batch), "Q搜索")
        XCTAssertTrue(HistoryLabel.paneSubtitle(for: batch).hasPrefix("客户复盘 · 1 个文件 · 16"))
    }

    func testTheHistoryPaneFallsBackToTheFilenameWithoutContext() {
        let batch = makeBatch(names: ["聊天记录.zip"], chatName: " ", sceneName: nil)

        XCTAssertEqual(HistoryLabel.paneTitle(for: batch), "聊天记录.zip")
        XCTAssertTrue(HistoryLabel.paneSubtitle(for: batch).hasPrefix("16"))
    }

    func testAFileWithAGroupButNoSceneKeepsItsFilenameInTheSubtitle() {
        let batch = makeBatch(names: ["活动方案.pdf"], chatName: "市场活动讨论群", sceneName: nil)

        XCTAssertEqual(HistoryLabel.paneTitle(for: batch), "市场活动讨论群")
        XCTAssertTrue(HistoryLabel.paneSubtitle(for: batch).hasPrefix("活动方案.pdf · 16"))
    }

    func testSearchCoversGroupSceneFilenameAndDestination() {
        let batch = makeBatch(
            names: ["客户清单.xlsx"],
            action: .custom,
            targetName: "Numbers",
            chatName: "产品研发群",
            sceneName: "需求整理"
        )

        XCTAssertTrue(HistoryLabel.matches(batch, query: "研发 需求"))
        XCTAssertTrue(HistoryLabel.matches(batch, query: "清单 numbers"))
        XCTAssertFalse(HistoryLabel.matches(batch, query: "市场"))
    }

    func testHistorySectionsNameTodayYesterdayAndOlderDates() {
        let today = date(month: 9, day: 20, hour: 22, minute: 16)
        let yesterday = date(month: 9, day: 19, hour: 18, minute: 0)
        let older = date(month: 9, day: 18, hour: 10, minute: 0)

        XCTAssertEqual(
            HistoryLabel.sectionTitle(for: today, now: today, locale: locale, timeZone: zone),
            "今天"
        )
        XCTAssertEqual(
            HistoryLabel.sectionTitle(for: yesterday, now: today, locale: locale, timeZone: zone),
            "昨天"
        )
        XCTAssertEqual(
            HistoryLabel.sectionTitle(for: older, now: today, locale: locale, timeZone: zone),
            "9月18日"
        )
    }

    /// The menu bar's own clock is two centimetres above this row, so the date
    /// is only worth the space when it is not today's.
    func testTodayIsTheClockAloneInTheMenu() {
        let at = date(month: 9, day: 5, hour: 14, minute: 32)
        XCTAssertEqual(
            HistoryLabel.timestamp(at, now: at, namesToday: false, locale: locale, timeZone: zone),
            "14:32"
        )
    }

    func testTheHistoryPaneSpellsTodayOut() {
        let at = date(month: 9, day: 5, hour: 14, minute: 32)
        XCTAssertEqual(
            HistoryLabel.timestamp(at, now: at, namesToday: true, locale: locale, timeZone: zone),
            "今天 14:32"
        )
        XCTAssertEqual(
            HistoryLabel.clockTime(at, locale: locale, timeZone: zone),
            "14:32"
        )
    }

    func testAnyOtherDayCarriesItsDate() {
        let at = date(month: 9, day: 4, hour: 14, minute: 32)
        let now = date(month: 9, day: 5, hour: 9, minute: 0)
        // Yesterday is another day even though it is 19 hours ago, and the
        // abbreviated month keeps it from reading as a fraction.
        XCTAssertEqual(
            HistoryLabel.timestamp(at, now: now, namesToday: false, locale: locale, timeZone: zone),
            "9月4日 14:32"
        )
        XCTAssertEqual(
            HistoryLabel.timestamp(at, now: now, namesToday: true, locale: locale, timeZone: zone),
            "9月4日 14:32"
        )
    }

    func testAMenuRowNamesTheFilesTheEntryAndTheTime() {
        let at = date(month: 9, day: 5, hour: 14, minute: 32)
        let batch = makeBatch(names: ["聊天记录.zip", "b.zip", "c.zip"], createdAt: at, action: .claude)

        XCTAssertEqual(
            HistoryLabel.menuTitle(for: batch, now: at, locale: locale, timeZone: zone),
            "聊天记录.zip 等 3 个 · 发给 Claude · 14:32"
        )
    }

    func testAnEmptyBatchDoesNotLeaveADanglingSeparator() {
        let at = date(month: 9, day: 5, hour: 14, minute: 32)
        let batch = makeBatch(names: [], createdAt: at, action: .clipboard)

        XCTAssertEqual(
            HistoryLabel.menuTitle(for: batch, now: at, locale: locale, timeZone: zone),
            "复制到剪贴板 · 14:32"
        )
    }

    // MARK: - Fixtures

    private func makeBatch(
        names: [String],
        createdAt: Date = Date(),
        action: ShareAction = .claude,
        targetName: String? = nil,
        chatName: String? = nil,
        sceneName: String? = nil
    ) -> ReadyBatch {
        let batchID = UUID()
        let items = names.map { name in
            ReadyItem(
                id: UUID(),
                batchID: batchID,
                displayName: name,
                url: URL(fileURLWithPath: "/tmp/\(name)"),
                byteCount: 16,
                contentType: nil,
                createdAt: createdAt,
                action: action
            )
        }
        return ReadyBatch(
            id: batchID,
            directory: URL(fileURLWithPath: "/tmp"),
            createdAt: createdAt,
            action: action,
            items: items,
            outcome: nil,
            targetName: targetName,
            chatName: chatName,
            sceneName: sceneName,
            isFirstSeen: false
        )
    }

    private func date(month: Int, day: Int, hour: Int, minute: Int) -> Date {
        var components = DateComponents()
        components.year = 2026
        components.month = month
        components.day = day
        components.hour = hour
        components.minute = minute
        components.timeZone = zone
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = zone
        return calendar.date(from: components)!
    }
}
