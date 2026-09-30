import WeChatBridgeCore
import Foundation
import XCTest

final class SceneTests: XCTestCase {
    func testScenePackageRoundTripsWithoutLocalState() throws {
        let scene = WeChatScene(
            id: "com.example.customer-review",
            name: "客户复盘",
            summary: "summary",
            instruction: "整理客户消息",
            outputSpec: "要点、结论、待办",
            keywords: ["客户", "甲方"],
            enabled: true,
            packageVersion: "1.2.3",
            author: "作者",
            applicability: "客户群"
        )
        let data = try ScenePackage.encoder().encode(ScenePackage(scene: scene))
        let decoded = try ScenePackage.decoder().decode(ScenePackage.self, from: data)
        XCTAssertEqual(decoded.id, scene.id)
        XCTAssertEqual(decoded.version, "1.2.3")
        XCTAssertEqual(decoded.keywords, ["客户", "甲方"])
        XCTAssertFalse(decoded.scene.enabled)
    }

    func testVersionsCompareByNumericComponents() {
        XCTAssertEqual(SceneVersion("1.2")!, SceneVersion("1.2.0")!)
        XCTAssertGreaterThan(SceneVersion("1.10.0")!, SceneVersion("1.2.9")!)
        XCTAssertNil(SceneVersion("1.beta"))
        XCTAssertNil(SceneVersion(""))
    }

    func testLegacyPromptBlobMigratesAndKeepsForwardSwitch() throws {
        let json = """
        {
          "prompts": [
            {"id":"11111111-1111-1111-1111-111111111111","text":"请总结"},
            {"id":"22222222-2222-2222-2222-222222222222","text":"请翻译"}
          ],
          "selectedID":"22222222-2222-2222-2222-222222222222",
          "attachToForwards":true
        }
        """
        let settings = try JSONDecoder().decode(SceneSettings.self, from: Data(json.utf8))
        XCTAssertEqual(settings.scenes.count, 7)
        XCTAssertEqual(settings.defaultSceneID, "22222222-2222-2222-2222-222222222222")
        XCTAssertTrue(settings.attachToForwards)
        XCTAssertEqual(settings.scene(id: settings.defaultSceneID)?.instruction, "请翻译")
    }

    func testV1AndV2PackagesBecomeTasksWithDefaults() throws {
        let v1 = """
        {
          "schemaVersion": 1,
          "id": "example.v1",
          "name": "旧任务",
          "version": "1.0.0",
          "instruction": "整理",
          "outputSpec": "输出"
        }
        """
        let decodedV1 = try ScenePackage.decoder().decode(ScenePackage.self, from: Data(v1.utf8))
        XCTAssertEqual(decodedV1.requiredSkillIDs, [])
        XCTAssertEqual(decodedV1.compatibleAgents, AgentID.allCases)
        XCTAssertFalse(decodedV1.isOfficial)

        let v2 = """
        {
          "schemaVersion": 2,
          "id": "example.v2",
          "name": "新任务",
          "version": "1.0.0",
          "instruction": "整理",
          "outputSpec": "输出",
          "requiredSkillIDs": ["wechatbridge.wechat-article-extract"],
          "compatibleAgents": ["chatGPTCodex"],
          "isOfficial": true
        }
        """
        let decodedV2 = try ScenePackage.decoder().decode(ScenePackage.self, from: Data(v2.utf8))
        XCTAssertEqual(decodedV2.requiredSkillIDs, ["wechatbridge.wechat-article-extract"])
        XCTAssertEqual(decodedV2.compatibleAgents, [.chatGPTCodex])
        XCTAssertTrue(decodedV2.isOfficial)
        XCTAssertEqual(AgentID.matching(.weSight), .weSight)
        XCTAssertNil(AgentID.matching(.deepSeekHarness))
    }

    func testOfficialTaskCopiesToAnEditableUserTask() {
        let official = WeChatScene(
            id: "official",
            name: "官方任务",
            instruction: "整理",
            outputSpec: "输出",
            requiredSkillIDs: ["skill"],
            compatibleAgents: [.doubao],
            isOfficial: true
        )
        let copied = SceneSettings(scenes: [official]).copiedAsUserTask(official)
        XCTAssertNotEqual(copied.id, official.id)
        XCTAssertFalse(copied.isOfficial)
        XCTAssertTrue(copied.enabled)
        XCTAssertEqual(copied.requiredSkillIDs, official.requiredSkillIDs)
        XCTAssertEqual(copied.compatibleAgents, official.compatibleAgents)
    }

    func testPromptListsDeclaredOnlySkillUnderSkillRequirements() {
        let scene = WeChatScene(
            name: "提取文章",
            instruction: "整理",
            outputSpec: "输出",
            requiredSkillIDs: ["article-extract"],
            isOfficial: true
        )
        let context = SkillRenderContext(agent: nil) { id in
            SkillResolution(id: id, displayName: "文章提取", mode: .native)
        }
        let prompt = ScenePrompt.render(
            scene: scene,
            previousSummaryAt: nil,
            skills: context
        )
        XCTAssertTrue(prompt?.contains("技能要求：") == true)
        XCTAssertTrue(prompt?.contains("- 使用「文章提取」技能（article-extract）") == true)
    }

    func testMatchingPrefersBindingThenLongestKeyword() {
        let first = WeChatScene(
            id: "customer",
            name: "客户复盘",
            instruction: "a",
            outputSpec: "b",
            keywords: ["客户"],
            enabled: true
        )
        let second = WeChatScene(
            id: "large-customer",
            name: "大客户复盘",
            instruction: "a",
            outputSpec: "b",
            keywords: ["大客户"],
            enabled: true
        )
        let settings = SceneSettings(scenes: [first, second], defaultSceneID: first.id)
        let keyword = SceneResolver.resolve(
            groupName: "华东大客户群",
            settings: settings,
            memories: [:]
        )
        XCTAssertEqual(keyword.scene?.id, second.id)
        XCTAssertEqual(keyword.source, .keyword)

        let memory = GroupMemory(displayName: "华东大客户群", boundSceneIDs: [first.id])
        let binding = SceneResolver.resolve(
            groupName: "华东大客户群",
            settings: settings,
            memories: [GroupName.normalize("华东大客户群"): memory]
        )
        XCTAssertEqual(binding.scene?.id, first.id)
        XCTAssertEqual(binding.source, .binding)
    }

    func testGroupBindingKeepsMultipleCandidateScenesInLibraryOrder() throws {
        let first = WeChatScene(id: "first", name: "一", instruction: "a", outputSpec: "", enabled: true)
        let second = WeChatScene(id: "second", name: "二", instruction: "b", outputSpec: "", enabled: true)
        let settings = SceneSettings(scenes: [first, second])
        let memory = GroupMemory(displayName: "群", boundSceneIDs: [second.id, first.id])
        let resolved = SceneResolver.resolve(
            groupName: "群",
            settings: settings,
            memories: [GroupName.normalize("群"): memory]
        )
        XCTAssertEqual(resolved.scenes.map(\.id), [first.id, second.id])

        let legacy = """
        {"displayName":"旧群","boundSceneID":"first","senders":[],"updatedAt":0}
        """
        let decoded = try JSONDecoder().decode(GroupMemory.self, from: Data(legacy.utf8))
        XCTAssertEqual(decoded.boundSceneIDs, ["first"])
    }

    func testFingerprintRequiresUniqueThresholdMatch() {
        let knownA = GroupMemory(
            displayName: "客户群 A",
            senders: ["张三", "李四", "王五"]
        )
        let knownB = GroupMemory(
            displayName: "客户群 B",
            senders: ["赵六", "钱七", "孙八"]
        )
        let memories = [
            GroupName.normalize("客户群 A"): knownA,
            GroupName.normalize("客户群 B"): knownB,
        ]
        XCTAssertEqual(
            GroupFingerprint.match(senders: ["张三", "李四"], memories: memories),
            GroupName.normalize("客户群 A")
        )
        XCTAssertNil(GroupFingerprint.match(senders: ["张三"], memories: memories))
        XCTAssertNil(
            GroupFingerprint.match(
                senders: ["张三", "赵六"],
                memories: [
                    "a": GroupMemory(displayName: "A", senders: ["张三", "甲"]),
                    "b": GroupMemory(displayName: "B", senders: ["赵六", "乙"]),
                ]
            )
        )
    }

    func testContinuationPromptIncludesOnlyNewerBoundary() {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = TimeZone(secondsFromGMT: 8 * 3600)!
        let date = calendar.date(from: DateComponents(year: 2026, month: 9, day: 17, hour: 8, minute: 30))!
        let scene = WeChatScene(
            name: "日常",
            instruction: "总结附件",
            outputSpec: "输出要点",
            enabled: true
        )
        let prompt = ScenePrompt.render(
            scene: scene,
            previousSummaryAt: date,
            currentEnd: date.addingTimeInterval(60),
            timeZone: TimeZone(secondsFromGMT: 8 * 3600)!
        )
        XCTAssertTrue(prompt?.contains("2026-09-17 08:30") == true)
        XCTAssertTrue(prompt?.contains("不要重复") == true)

        XCTAssertFalse(
            ScenePrompt.render(
                scene: scene,
                previousSummaryAt: date,
                currentEnd: nil,
                timeZone: TimeZone(secondsFromGMT: 8 * 3600)!
            )?.contains("不要重复") == true
        )
        XCTAssertFalse(
            ScenePrompt.render(
                scene: scene,
                previousSummaryAt: date,
                currentEnd: date,
                timeZone: TimeZone(secondsFromGMT: 8 * 3600)!
            )?.contains("不要重复") == true
        )
    }

    func testGroupTitleParserHandlesCountsAndPlainNames() {
        XCTAssertEqual(
            GroupTitleParser.parse(["华东客户群 (128)"])?.name,
            "华东客户群"
        )
        XCTAssertEqual(
            GroupTitleParser.parse(["本周项目会（12）"])?.memberCount,
            12
        )
        XCTAssertEqual(
            GroupTitleParser.parse(["没有人数"])?.name,
            "没有人数"
        )
        XCTAssertNil(GroupTitleParser.parse(["   "]))

        XCTAssertEqual(
            GroupTitleParser.parse(["微信", "华东客户群 (128)", "搜索"])?.name,
            "华东客户群"
        )
    }

    func testDefaultSceneRequiresExplicitSelection() {
        let scene = WeChatScene(
            name: "客户复盘",
            instruction: "总结",
            outputSpec: "要点",
            enabled: true
        )
        let settings = SceneSettings(scenes: [scene])
        XCTAssertNil(settings.defaultSceneID)
        XCTAssertNil(settings.defaultScene())
        XCTAssertNil(
            SceneResolver.resolve(groupName: "未匹配群", settings: settings, memories: [:]).scene
        )
    }

    func testExactGroupCanFallBackToItsLastScene() {
        let last = WeChatScene(
            id: "last",
            name: "上次场景",
            instruction: "总结",
            outputSpec: "要点",
            enabled: true
        )
        let settings = SceneSettings(scenes: [last])
        let memory = GroupMemory(displayName: "项目群", lastSceneID: last.id)
        let resolved = SceneResolver.resolve(
            groupName: "项目群",
            settings: settings,
            memories: [GroupName.normalize("项目群"): memory]
        )
        XCTAssertEqual(resolved.scene?.id, last.id)
        XCTAssertEqual(resolved.source, .lastUsed)
    }

    func testGroupMemoryAdvancesOnlyForward() {
        let old = Date(timeIntervalSince1970: 100)
        let newer = Date(timeIntervalSince1970: 200)
        let evenNewer = Date(timeIntervalSince1970: 300)
        let first = GroupMemory.advancing(
            nil,
            displayName: "群",
            sceneID: "a",
            senders: ["张三"],
            end: newer,
            at: old
        )
        let second = GroupMemory.advancing(
            first,
            displayName: "群",
            sceneID: "b",
            senders: ["李四"],
            end: old,
            at: newer
        )
        XCTAssertEqual(second.lastSummaryAt, newer)
        XCTAssertEqual(second.lastSceneID, "b")
        XCTAssertEqual(second.senders, Set(["张三", "李四"]))

        let third = GroupMemory.advancing(
            second,
            displayName: "群",
            sceneID: "c",
            senders: [],
            end: evenNewer
        )
        XCTAssertEqual(third.lastSummaryAt, evenNewer)
    }

    func testBatchStateContextIsBackwardCompatible() throws {
        let oldJSON = """
        {"schemaVersion":1,"shelved":[],"outcome":null}
        """
        let decoded = try BatchManifest.decoder().decode(BatchState.self, from: Data(oldJSON.utf8))
        XCTAssertNil(decoded.chatName)
        let updated = decoded.withContext(chatName: "群", sceneID: "s", sceneName: "场景")
        XCTAssertEqual(updated.chatName, "群")
        XCTAssertEqual(updated.sceneName, "场景")
    }
}
