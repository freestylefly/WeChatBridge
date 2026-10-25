import XCTest
@testable import WeChatBridgeiOS

final class QuickDeliverTests: XCTestCase {
    private func makeDefaults() -> UserDefaults {
        let name = "QuickDeliverTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: name)!
        addTeardownBlock { defaults.removePersistentDomain(forName: name) }
        return defaults
    }

    func testOnByDefaultAndKeepsThreeTargets() {
        let defaults = makeDefaults()
        XCTAssertTrue(QuickDeliver.isEnabled(defaults))
        QuickDeliver.store((1...5).map { QuickDeliver.Target(id: "t\($0)", name: "目标\($0)") }, in: defaults)
        XCTAssertEqual(QuickDeliver.targets(defaults).map(\.id), ["t1", "t2", "t3"])
    }

    func testContentPointsAtTheFirstTarget() {
        let id = UUID()
        let content = QuickDeliver.content(batchID: id, head: "group-a.zip", targets: [.init(id: "doubao", name: "豆包"), .init(id: "chatgpt", name: "ChatGPT")])
        XCTAssertEqual(content.body, "group-a.zip · 点一下投递到「豆包」，长按换目标")
        XCTAssertEqual(content.userInfo[QuickDeliver.batchKey] as? String, id.uuidString)
        XCTAssertEqual(content.userInfo[QuickDeliver.targetKey] as? String, "doubao")
        let category = QuickDeliver.makeCategory(targets: [.init(id: "doubao", name: "豆包"), .init(id: "chatgpt", name: "ChatGPT")])
        XCTAssertEqual(category.actions.map(\.identifier), ["deliver.doubao", "deliver.chatgpt"])
    }

    func testWithoutTargetsTheTapOpensThePanel() {
        let content = QuickDeliver.content(batchID: UUID(), head: "2 个文件", targets: [])
        XCTAssertEqual(content.body, "2 个文件 · 点一下去投递")
        XCTAssertEqual(content.userInfo[QuickDeliver.targetKey] as? String, "")
    }

    @MainActor
    func testPreferencesPublishTheArrangedTargets() {
        let defaults = makeDefaults()
        let preferences = Preferences(defaults: defaults)
        preferences.lastTarget = "qwen"
        XCTAssertEqual(QuickDeliver.targets(defaults).map(\.id), ["qwen", "chatgpt", "doubao"])
    }
}
