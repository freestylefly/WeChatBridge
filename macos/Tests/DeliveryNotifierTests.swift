@testable import WeChatBridgeApp
import WeChatBridgeCore
import XCTest
import UserNotifications

final class DeliveryNotifierTests: XCTestCase {
    @MainActor
    func testStartupRegistersActionsBeforeAnyNewDelivery() async {
        let center = TestNotificationCenter()
        let notifier = DeliveryNotifier(center: center)
        notifier.configure()
        XCTAssertTrue(center.delegate === notifier)
        XCTAssertEqual(Set(center.categories.map(\.identifier)), [DeliveryNotifier.folderCategoryIdentifier, DeliveryNotifier.obsidianCategoryIdentifier])
        let actions = Set(center.categories.flatMap(\.actions).map(\.identifier))
        XCTAssertEqual(actions, [DeliveryNotifier.revealActionIdentifier, DeliveryNotifier.openNoteActionIdentifier])
    }

    @MainActor
    func testRestartReplacesDelegateWithoutSendingANewNotification() async {
        let center = TestNotificationCenter()
        var previous: DeliveryNotifier? = DeliveryNotifier(center: center)
        previous?.configure()
        previous = nil
        XCTAssertNil(center.delegate)
        let restarted = DeliveryNotifier(center: center)
        restarted.configure()
        XCTAssertTrue(center.delegate === restarted)
        XCTAssertEqual(center.registrationCount, 2)
    }


    func testRevealRoundTripPreservesPathsOfEveryShape() {
        let notes = [
            URL(fileURLWithPath: "/Users/甲/沉淀/微信群 聊天记录.md"),
            URL(fileURLWithPath: "/tmp/emoji-🗂- attachment (2).zip"),
            URL(fileURLWithPath: "/tmp/wxbridge/a'b\"c/hyphen-name.txt"),
        ]
        XCTAssertEqual(DeliveryNotifier.revealPaths(from: DeliveryNotifier.userInfo(forRevealing: notes)), notes)
    }

    func testEmptyRevealRoundTripsToEmpty() {
        XCTAssertEqual(DeliveryNotifier.revealPaths(from: DeliveryNotifier.userInfo(forRevealing: [])), [])
    }

    func testObsidianRoundTripPreservesVaultAndFiles() throws {
        let userInfo = DeliveryNotifier.userInfo(
            forOpeningIn: "我的 知识库",
            files: ["微信群/张三.md", "群聊/会议 🗂 记录.md"]
        )
        let urls = DeliveryNotifier.obsidianURLs(from: userInfo)
        XCTAssertEqual(urls.count, 2)
        let components = try urls.map {
            try XCTUnwrap(URLComponents(url: $0, resolvingAgainstBaseURL: false))
        }
        XCTAssertEqual(
            components.map { $0.queryItems?.first { $0.name == "vault" }?.value },
            ["我的 知识库", "我的 知识库"]
        )
        XCTAssertEqual(
            components.map { $0.queryItems?.first { $0.name == "file" }?.value },
            ["微信群/张三.md", "群聊/会议 🗂 记录.md"]
        )
        XCTAssertTrue(urls.allSatisfy { $0.scheme == "obsidian" && $0.host == "open" })
    }

    func testObsidianURLSurvivesReservedCharacters() throws {
        // 「?」「#」「&」 break a carelessly concatenated URL; the query
        // encoding has to carry them through to Obsidian intact.
        let url = try XCTUnwrap(DeliveryNotifier.obsidianURL(vault: "vault?x", file: "a?b#c&d=e.md"))
        let components = try XCTUnwrap(URLComponents(url: url, resolvingAgainstBaseURL: false))
        XCTAssertEqual(components.queryItems?.first { $0.name == "vault" }?.value, "vault?x")
        XCTAssertEqual(components.queryItems?.first { $0.name == "file" }?.value, "a?b#c&d=e.md")
    }

    func testForeignOrMissingUserInfoDecodesToNothing() {
        XCTAssertEqual(DeliveryNotifier.revealPaths(from: [:]), [])
        XCTAssertEqual(DeliveryNotifier.revealPaths(from: ["unrelated": "value"]), [])
        // A wrong type under the right key — say a future change puts a single
        // path there — must not crash a notification callback.
        XCTAssertEqual(DeliveryNotifier.revealPaths(from: ["revealPaths": "/a/single/path.md"]), [])
        XCTAssertEqual(DeliveryNotifier.obsidianURLs(from: [:]), [])
        XCTAssertEqual(DeliveryNotifier.obsidianURLs(from: ["unrelated": "value"]), [])
        // Half an Obsidian target — a vault without its files — decodes to
        // nothing rather than opening something half-built.
        XCTAssertEqual(DeliveryNotifier.obsidianURLs(from: ["obsidianVault": "知识库"]), [])
        XCTAssertEqual(DeliveryNotifier.obsidianURLs(from: ["obsidianFiles": ["a.md"]]), [])
    }

    func testIdentifiersAreStableAndDistinct() {
        // Categories and actions are matched by string on both sides of the
        // userInfo round trips; equal identifiers would make one notification's
        // plain click indistinguishable from another's button.
        XCTAssertFalse(DeliveryNotifier.folderCategoryIdentifier.isEmpty)
        XCTAssertFalse(DeliveryNotifier.revealActionIdentifier.isEmpty)
        XCTAssertFalse(DeliveryNotifier.obsidianCategoryIdentifier.isEmpty)
        XCTAssertFalse(DeliveryNotifier.openNoteActionIdentifier.isEmpty)
        XCTAssertNotEqual(DeliveryNotifier.folderCategoryIdentifier, DeliveryNotifier.revealActionIdentifier)
        XCTAssertNotEqual(DeliveryNotifier.obsidianCategoryIdentifier, DeliveryNotifier.openNoteActionIdentifier)
        // The two destinations' notifications must never be confusable.
        XCTAssertNotEqual(DeliveryNotifier.folderCategoryIdentifier, DeliveryNotifier.obsidianCategoryIdentifier)
        XCTAssertNotEqual(DeliveryNotifier.revealActionIdentifier, DeliveryNotifier.openNoteActionIdentifier)
    }
}

private final class TestNotificationCenter: DeliveryNotificationCenter {
    weak var delegate: UNUserNotificationCenterDelegate?
    private(set) var categories = Set<UNNotificationCategory>()
    private(set) var registrationCount = 0
    func requestAuthorization(options: UNAuthorizationOptions) async throws -> Bool { false }
    func add(_ request: UNNotificationRequest) async throws {}
    func setNotificationCategories(_ categories: Set<UNNotificationCategory>) {
        self.categories = categories
        registrationCount += 1
    }
}
