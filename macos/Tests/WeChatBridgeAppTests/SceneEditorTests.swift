import AppKit
import SwiftUI
import WeChatBridgeCore
import XCTest
@testable import WeChatBridgeApp

final class SceneEditorTests: XCTestCase {
    @MainActor
    func testPromptEditPublishesAndPersistsOneCompleteScene() throws {
        let domain = "scene-save-tests-" + UUID().uuidString
        let defaults = try XCTUnwrap(UserDefaults(suiteName: domain))
        defer { defaults.removePersistentDomain(forName: domain) }
        let preferences = Preferences(defaults: defaults)
        let original = WeChatScene(id: "edited", name: "名称", instruction: "旧提示词", outputSpec: "旧规范", requiredSkillIDs: ["old-skill"])
        let untouched = WeChatScene(id: "untouched", name: "另一个场景", instruction: "保留内容", outputSpec: "保留规范")
        preferences.scenes = SceneSettings(scenes: [original, untouched])
        var published: [WeChatScene] = []
        let scene = Binding(get: { preferences.scenes.scenes[0] }, set: {
            published.append($0)
            preferences.scenes.scenes[0] = $0
        })
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(domain)
        let skills = SkillLibrary(resourcesRoot: nil, store: SkillStore(root: root, configDirectory: root))
        let editor = SceneEditor(scene: scene, enabled: .constant(false), isDefault: false, toggleDefault: {}, skills: skills,
                                 duplicate: {}, moveUp: {}, moveDown: {}, export: {}, remove: {}, save: {})
        let text = "新提示词 {{skill:new-skill}} {{skill:new-skill}}"
        editor.promptBinding.wrappedValue = text
        XCTAssertEqual(published.count, 1)
        XCTAssertEqual(published.first?.instruction, text)
        XCTAssertEqual(published.first?.outputSpec, "")
        XCTAssertEqual(published.first?.requiredSkillIDs, ["new-skill"])
        XCTAssertEqual(preferences.scenes.scenes[1], untouched)
        // 重载会补齐官方模板，只比较必须原样保留的用户场景。
        XCTAssertEqual(Preferences(defaults: defaults).scenes.scenes.filter { !$0.isOfficial }, preferences.scenes.scenes)
    }

    @MainActor
    func testPromptBindingPreservesWhitespaceAndExistingOutputSpec() {
        var scene = WeChatScene(name: "名称", instruction: "原提示词", outputSpec: "原规范")
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let skills = SkillLibrary(resourcesRoot: nil, store: SkillStore(root: root, configDirectory: root))
        let binding = Binding(get: { scene }, set: { scene = $0 })
        let editor = SceneEditor(scene: binding, enabled: .constant(false), isDefault: false, toggleDefault: {}, skills: skills,
                                 duplicate: {}, moveUp: {}, moveDown: {}, export: {}, remove: {}, save: {})
        XCTAssertTrue(editor.promptBinding.wrappedValue.contains("原提示词"))
        XCTAssertTrue(editor.promptBinding.wrappedValue.contains("原规范"))
        XCTAssertEqual(scene.outputSpec, "原规范")
        for text in ["\n", " \n\n", "", "新内容\n\n"] {
            editor.promptBinding.wrappedValue = text
            XCTAssertEqual(editor.promptBinding.wrappedValue, text)
            XCTAssertEqual(scene.instruction, text)
            XCTAssertEqual(scene.outputSpec, "")
        }
    }

    @MainActor
    func testInsertingSkillDoesNotModifyAnotherTextResponder() {
        _ = NSApplication.shared
        var scene = WeChatScene(name: "名称", instruction: "提示词", outputSpec: "")
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let skills = SkillLibrary(resourcesRoot: nil, store: SkillStore(root: root, configDirectory: root))
        let binding = Binding(get: { scene }, set: { scene = $0 })
        let editor = SceneEditor(scene: binding, enabled: .constant(false), isDefault: false, toggleDefault: {}, skills: skills,
                                 duplicate: {}, moveUp: {}, moveDown: {}, export: {}, remove: {}, save: {})
        let otherText = NSTextView()
        otherText.string = "场景名称或预览"
        otherText.setSelectedRange(NSRange(location: 2, length: 0))
        // CI 不保证桌面激活，直接传入菜单读取的响应者，验证它不会被误写。
        editor.insertSkillToken("new-skill", firstResponder: otherText)
        XCTAssertEqual(otherText.string, "场景名称或预览")
        XCTAssertEqual(scene.instruction, "提示词 {{skill:new-skill}}")
        XCTAssertEqual(scene.name, "名称")
        XCTAssertEqual(scene.requiredSkillIDs, ["new-skill"])
    }
}
