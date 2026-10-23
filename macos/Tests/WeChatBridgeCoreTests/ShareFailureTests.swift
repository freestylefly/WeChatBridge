import WeChatBridgeCore
import Foundation
import XCTest

/// The one channel a UI-less share extension has for bad news.
///
/// The extension writes a file and dies; the app reads it on its next scan and
/// says it out loud. Nothing else records that the failure was shown, so
/// "reading deletes" is the whole protocol and it is what these assert.
final class ShareFailureTests: XCTestCase {
    private var temporary: TemporaryInbox!

    override func setUp() {
        super.setUp()
        temporary = TemporaryInbox()
    }

    override func tearDown() {
        temporary.tearDown()
        super.tearDown()
    }

    func testAFailureSurvivesTheRoundTripAndIsDeletedByReadingIt() throws {
        ShareFailure.record(
            ShareFailure(at: Date(), action: .codex, message: "系统没有返回文件。"),
            in: temporary.inbox
        )

        let consumed = temporary.reader.consumeFailures()

        XCTAssertEqual(consumed.count, 1)
        XCTAssertEqual(consumed.first?.action, .codex)
        XCTAssertEqual(consumed.first?.message, "系统没有返回文件。")
        // Read means gone: a second scan must not repeat a message the user has
        // already been shown, and there is no other place that remembers.
        XCTAssertTrue(temporary.reader.consumeFailures().isEmpty)
    }

    func testFailuresComeBackOldestFirst() throws {
        let base = Date(timeIntervalSince1970: 1_772_000_000)
        for offset in [60.0, 0.0, 30.0] {
            ShareFailure.record(
                ShareFailure(
                    at: base.addingTimeInterval(offset),
                    action: .custom,
                    message: "\(Int(offset))"
                ),
                in: temporary.inbox
            )
        }

        // The toast replaces its own content rather than queueing, so the last
        // one handed over is the one left on screen — which has to be the most
        // recent failure, not whatever order the directory happened to list.
        XCTAssertEqual(temporary.reader.consumeFailures().map(\.message), ["0", "30", "60"])
    }

    func testAnUnreadableReportIsDroppedRatherThanRetriedForever() throws {
        try Data("not json".utf8).write(
            to: temporary.inbox.failures.appendingPathComponent("\(UUID().uuidString).json")
        )

        XCTAssertTrue(temporary.reader.consumeFailures().isEmpty)
        XCTAssertEqual(
            try FileManager.default.contentsOfDirectory(
                atPath: temporary.inbox.failures.path
            ).count,
            0
        )
    }

    func testAnEmptyFailuresDirectoryIsNotAnError() {
        XCTAssertTrue(temporary.reader.consumeFailures().isEmpty)
    }
}

/// 「发送到自定义」 without a panel: what the app does with the list it finds.
///
/// The rule the user asked for is the middle case — one app is not a choice —
/// and it is the reason this is a value rather than three branches buried in
/// `ActionRunner`, which has no test target of its own.
final class CustomForwardDecisionTests: XCTestCase {
    private func target(_ identifier: String) -> ForwardTarget {
        ForwardTarget(
            bundleIdentifier: identifier,
            displayName: identifier,
            addedAt: Date(timeIntervalSince1970: 1_772_000_000)
        )
    }

    func testAnEmptyListAsksNothingAndSendsNothing() {
        XCTAssertEqual(CustomForwardDecision.decide(targets: []), .none)
    }

    func testASingleAppIsForwardedWithoutAsking() {
        let only = target("com.apple.TextEdit")
        XCTAssertEqual(CustomForwardDecision.decide(targets: [only]), .single(only))
    }

    func testTwoOrMoreAppsAreOfferedInTheOrderGiven() {
        let list = [target("a"), target("b"), target("c")]
        // Order is preserved because the caller has already put the last-used
        // app first, and the panel's first row is the one Return picks.
        XCTAssertEqual(CustomForwardDecision.decide(targets: list), .choose(list))
    }
}
