import Foundation
import XCTest
@testable import WeChatBridgeCore

final class SkillIdTests: XCTestCase {
    func testValidationFollowsTheSpec() {
        XCTAssertTrue(SkillId.isValid("wechat-article-extract"))
        XCTAssertTrue(SkillId.isValid("a"))
        XCTAssertTrue(SkillId.isValid("skill-123-x"))
        XCTAssertTrue(SkillId.isValid(String(repeating: "a", count: SkillId.maxLength)))

        XCTAssertFalse(SkillId.isValid(nil))
        XCTAssertFalse(SkillId.isValid(""))
        XCTAssertFalse(SkillId.isValid("Wechat"))
        XCTAssertFalse(SkillId.isValid("wechat_article"))
        XCTAssertFalse(SkillId.isValid("wechat.article"))
        XCTAssertFalse(SkillId.isValid("-wechat"))
        XCTAssertFalse(SkillId.isValid("wechat-"))
        XCTAssertFalse(SkillId.isValid("wechat--article"))
        XCTAssertFalse(SkillId.isValid("微信"))
        XCTAssertFalse(SkillId.isValid(String(repeating: "a", count: SkillId.maxLength + 1)))
    }

    func testMigrateMapsLegacyDottedIDs() {
        XCTAssertEqual(
            SkillId.migrate("wechatbridge.wechat-article-extract"),
            "wechat-article-extract"
        )
        XCTAssertEqual(
            SkillId.migrate("wechatbridge.video-information-reading"),
            "video-information-reading"
        )
        XCTAssertEqual(SkillId.migrate("unrelated-id"), "unrelated-id")
    }

    func testMigrateRewritesSceneIdsAndInlineTokens() {
        var scene = WeChatScene(
            name: "s",
            instruction: "用 {{skill:wechatbridge.wechat-article-extract}} 提取",
            outputSpec: "输出",
            requiredSkillIDs: ["wechatbridge.video-information-reading", "wechat-article-extract"]
        )
        XCTAssertTrue(SkillId.migrate(&scene))
        XCTAssertEqual(
            scene.requiredSkillIDs,
            ["video-information-reading", "wechat-article-extract"]
        )
        XCTAssertTrue(scene.instruction.contains("{{skill:wechat-article-extract}}"))
        XCTAssertFalse(scene.instruction.contains("wechatbridge."))
    }
}

final class SkillReferenceTests: XCTestCase {
    func testParseCollectsValidIDsInOrder() {
        let text = "用 {{skill:wechat-article-extract}} 提取，再 {{skill:video-information-reading}}，重复 {{skill:wechat-article-extract}}"
        XCTAssertEqual(
            SkillReference.parse(text),
            ["wechat-article-extract", "video-information-reading"]
        )
        XCTAssertEqual(SkillReference.parse(nil), [])
        XCTAssertEqual(SkillReference.parse("无引用"), [])
    }

    func testInvalidCapturesMalformedIDs() {
        let text = "{{skill:Bad_Id}} {{skill:ok-id}} {{skill:WechatBridge.x}}"
        XCTAssertEqual(SkillReference.invalid(text), ["Bad_Id", "WechatBridge.x"])
    }

    func testReplaceLeavesInvalidTokensVerbatim() {
        let text = "a {{skill:Bad}} b {{skill:good-id}} c"
        let result = SkillReference.replace(text) { "<\($0)>" }
        XCTAssertEqual(result, "a {{skill:Bad}} b <good-id> c")
    }

    func testReplaceOffersLegacyIDsToTheRenderer() {
        let text = "{{skill:wechatbridge.wechat-article-extract}}"
        var seen: [String] = []
        let result = SkillReference.replace(text) {
            seen.append($0)
            return SkillReference.token(SkillId.migrate($0))
        }
        XCTAssertEqual(seen, ["wechatbridge.wechat-article-extract"])
        XCTAssertEqual(result, "{{skill:wechat-article-extract}}")
    }

    func testReplaceHandlesEmptyAndTokenOnlyText() {
        XCTAssertEqual(SkillReference.replace(nil) { $0 }, "")
        XCTAssertEqual(SkillReference.replace("") { $0 }, "")
        XCTAssertEqual(
            SkillReference.replace("{{skill:x-y}}") { "<\($0)>" },
            "<x-y>"
        )
    }
}

final class SceneSkillTests: XCTestCase {
    func testEffectiveSkillIDsOrderAndDedup() {
        let scene = WeChatScene(
            name: "s",
            instruction: "用 {{skill:a-first}} 和 {{skill:b-second}}",
            outputSpec: "输出 {{skill:a-first}} 加 {{skill:c-third}}",
            requiredSkillIDs: ["b-second", "d-declared"]
        )
        XCTAssertEqual(
            scene.effectiveSkillIDs,
            ["a-first", "b-second", "c-third", "d-declared"]
        )
    }

    func testPackageExportsEffectiveIDsAndSchema3() throws {
        let scene = WeChatScene(
            name: "s",
            instruction: "用 {{skill:inline-skill}} 做",
            outputSpec: "输出",
            requiredSkillIDs: ["declared-skill"]
        )
        let package = ScenePackage(scene: scene)
        XCTAssertEqual(package.schemaVersion, 3)
        XCTAssertEqual(package.requiredSkillIDs, ["inline-skill", "declared-skill"])

        let data = try ScenePackage.encoder().encode(package)
        let decoded = try ScenePackage.decoder().decode(ScenePackage.self, from: data)
        XCTAssertEqual(decoded.schemaVersion, 3)
        XCTAssertEqual(decoded.requiredSkillIDs, ["inline-skill", "declared-skill"])
    }

    func testStarterScenesUseInlineReferences() {
        for scene in SceneSettings.starterScenes {
            // Every declared skill must be reachable through effectiveSkillIDs,
            // and every effective id must follow the spec.
            for id in scene.effectiveSkillIDs {
                XCTAssertTrue(SkillId.isValid(id), "\(id) must be spec-compliant")
            }
            for id in scene.requiredSkillIDs {
                XCTAssertTrue(
                    scene.effectiveSkillIDs.contains(id),
                    "\(id) dropped from effectiveSkillIDs"
                )
            }
            // Scenes that reference a skill do so inline, not only by declaration.
            if !scene.requiredSkillIDs.isEmpty {
                XCTAssertFalse(SkillReference.parse(scene.instruction).isEmpty)
            }
        }
    }

    private func context(_ modes: [String: SkillRenderMode]) -> SkillRenderContext {
        SkillRenderContext(agent: nil) { id in
            SkillResolution(
                id: id,
                displayName: "名称-\(id)",
                mode: modes[id] ?? .unknown,
                skillFile: modes[id] == .path ? "/tmp/lib/\(id)/SKILL.md" : nil
            )
        }
    }

    func testPromptRendersEachMode() {
        let scene = WeChatScene(
            name: "s",
            instruction: "用 {{skill:a-native}}、{{skill:b-path}}、{{skill:c-missing}}、{{skill:d-unknown}} 处理",
            outputSpec: "输出"
        )
        let prompt = ScenePrompt.render(
            scene: scene,
            previousSummaryAt: nil,
            skills: context([
                "a-native": .native,
                "b-path": .path,
                "c-missing": .missing,
                "d-unknown": .unknown,
            ])
        )!
        XCTAssertTrue(prompt.contains("「名称-a-native」技能（a-native）"))
        XCTAssertTrue(prompt.contains("/tmp/lib/b-path/SKILL.md"))
        XCTAssertTrue(prompt.contains("「名称-c-missing」技能（技能文件不可用，请直接完成）"))
        XCTAssertTrue(prompt.contains("「d-unknown」技能（未找到该技能）"))
        // All four are inline — no separate 技能要求 section.
        XCTAssertFalse(prompt.contains("技能要求："))
    }

    func testPromptListsDeclaredOnlySkillsOnce() {
        let scene = WeChatScene(
            name: "s",
            instruction: "用 {{skill:inline-one}} 处理",
            outputSpec: "输出",
            requiredSkillIDs: ["inline-one", "declared-two", "declared-two"]
        )
        let prompt = ScenePrompt.render(
            scene: scene,
            previousSummaryAt: nil,
            skills: context(["inline-one": .native, "declared-two": .native])
        )!
        XCTAssertTrue(prompt.contains("技能要求："))
        XCTAssertTrue(prompt.contains("- 使用「名称-declared-two」技能（declared-two）"))
        // The inline skill is rendered in place, never listed again.
        XCTAssertEqual(
            prompt.components(separatedBy: "inline-one").count - 1,
            2 // once in the rendered phrase: 「名称-inline-one」技能（inline-one）
        )
    }

    func testPromptWithoutContextStillRendersReferences() {
        let scene = WeChatScene(
            name: "s",
            instruction: "用 {{skill:a-b}} 处理",
            outputSpec: "输出"
        )
        let prompt = ScenePrompt.render(scene: scene, previousSummaryAt: nil)!
        XCTAssertTrue(prompt.contains("「a-b」技能（技能文件不可用，请直接完成）"))
    }
}

final class SkillStoreTests: XCTestCase {
    private var root: URL!
    private var resources: URL!
    private var config: URL!
    private var store: SkillStore!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory
            .appendingPathComponent("SkillStoreTests-\(UUID().uuidString)", isDirectory: true)
        resources = root.appendingPathComponent("Resources", isDirectory: true)
        config = root.appendingPathComponent("Config", isDirectory: true)
        try FileManager.default.createDirectory(at: resources, withIntermediateDirectories: true)
        store = SkillStore(
            root: root.appendingPathComponent("Library", isDirectory: true),
            configDirectory: config,
            backupRoot: root.appendingPathComponent("Backups", isDirectory: true)
        )
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: root)
    }

    func testSyncAddsMissingAndReportsConflicts() throws {
        let catalog = OfficialSkillCatalog(skills: [try makePackage(id: "sync-a")])
        var report = try store.syncOfficial(catalog: catalog, resourcesRoot: resources)
        XCTAssertEqual(report.added, 1)
        XCTAssertEqual(report.updated, 0)
        XCTAssertTrue(report.conflicts.isEmpty)
        XCTAssertEqual(store.state("sync-a"), .ready)
        XCTAssertNotNil(store.skillFile("sync-a"))
        XCTAssertEqual(store.state("never-seen"), .missing)

        // Same version twice is a no-op.
        report = try store.syncOfficial(catalog: catalog, resourcesRoot: resources)
        XCTAssertEqual(report.added, 0)
        XCTAssertEqual(report.updated, 0)
        XCTAssertTrue(report.conflicts.isEmpty)

        // A directory dropped in by hand is a conflict, never overwritten.
        let foreign = store.skillDirectory("sync-b")
        try FileManager.default.createDirectory(at: foreign, withIntermediateDirectories: true)
        try Data("foreign".utf8).write(to: foreign.appendingPathComponent("SKILL.md"))
        let catalogB = OfficialSkillCatalog(skills: [try makePackage(id: "sync-b")])
        report = try store.syncOfficial(catalog: catalogB, resourcesRoot: resources)
        XCTAssertEqual(report.conflicts, ["sync-b"])
        XCTAssertEqual(store.state("sync-b"), .conflict)
        XCTAssertEqual(
            try String(contentsOf: foreign.appendingPathComponent("SKILL.md"), encoding: .utf8),
            "foreign"
        )
    }

    func testSyncUpdatesUntouchedCopyAndBacksItUp() throws {
        let skill = try makePackage(id: "upgradable")
        try store.syncOfficial(
            catalog: OfficialSkillCatalog(skills: [skill]),
            resourcesRoot: resources
        )

        let newer = OfficialSkill(
            id: skill.id,
            name: skill.name,
            summary: skill.summary,
            version: "2.0.0",
            package: skill.package,
            supportedAgents: skill.supportedAgents
        )
        try rewritePackage(skill.package!, id: skill.id, version: "2.0.0")
        let report = try store.syncOfficial(
            catalog: OfficialSkillCatalog(skills: [newer]),
            resourcesRoot: resources
        )
        XCTAssertEqual(report.updated, 1)
        XCTAssertEqual(store.state(skill.id), .ready)
        XCTAssertEqual(store.entries()[skill.id]?.version, "2.0.0")

        let backups = try FileManager.default.contentsOfDirectory(
            atPath: store.backupRoot.path
        )
        XCTAssertEqual(backups.count, 1)
        XCTAssertTrue(backups[0].hasPrefix("\(skill.id)-1.0.0"))
    }

    func testSyncDoesNotTouchExternallyModifiedCopy() throws {
        let skill = try makePackage(id: "modified")
        try store.syncOfficial(
            catalog: OfficialSkillCatalog(skills: [skill]),
            resourcesRoot: resources
        )
        let file = store.skillDirectory(skill.id).appendingPathComponent("SKILL.md")
        try Data("user edited".utf8).write(to: file)
        XCTAssertEqual(store.state(skill.id), .conflict)

        let newer = OfficialSkill(
            id: skill.id,
            name: skill.name,
            summary: skill.summary,
            version: "9.9.9",
            package: skill.package,
            supportedAgents: skill.supportedAgents
        )
        let report = try store.syncOfficial(
            catalog: OfficialSkillCatalog(skills: [newer]),
            resourcesRoot: resources
        )
        XCTAssertEqual(report.conflicts, [skill.id])
        XCTAssertEqual(
            try String(contentsOf: file, encoding: .utf8),
            "user edited"
        )
    }

    func testImportRegistersAndReplacesWithBackup() throws {
        let source = root.appendingPathComponent("incoming", isDirectory: true)
        try FileManager.default.createDirectory(at: source, withIntermediateDirectories: true)
        try Data("---\nname: imported-skill\n---\n# 导入技能\n".utf8)
            .write(to: source.appendingPathComponent("SKILL.md"))

        let entry = try store.import(source: source, id: "imported-skill", version: "1.2.0")
        XCTAssertEqual(entry.source, SkillStore.importedSource)
        XCTAssertEqual(entry.version, "1.2.0")
        XCTAssertEqual(store.state("imported-skill"), .ready)

        // Byte-identical re-import keeps the slot, just refreshes the stamp.
        let again = try store.import(source: source, id: "imported-skill", version: "1.2.0")
        XCTAssertEqual(again.digest, entry.digest)

        // A changed package replaces the old one, which lands in the backups.
        try Data("---\nname: imported-skill\n---\n# 导入技能 v2\n".utf8)
            .write(to: source.appendingPathComponent("SKILL.md"))
        try store.import(source: source, id: "imported-skill", version: "1.3.0")
        XCTAssertEqual(store.entries()["imported-skill"]?.version, "1.3.0")
        let backups = try FileManager.default.contentsOfDirectory(atPath: store.backupRoot.path)
        XCTAssertEqual(backups.count, 1)
    }

    func testRemoveMovesCopyToBackupsAndDropsRegistry() throws {
        let skill = try makePackage(id: "removable")
        try store.syncOfficial(
            catalog: OfficialSkillCatalog(skills: [skill]),
            resourcesRoot: resources
        )
        XCTAssertTrue(try store.remove(skill.id))
        XCTAssertEqual(store.state(skill.id), .missing)
        XCTAssertNil(store.entries()[skill.id])
        XCTAssertFalse(try store.remove(skill.id))
        let backups = try FileManager.default.contentsOfDirectory(atPath: store.backupRoot.path)
        XCTAssertTrue(backups.contains { $0.hasPrefix("\(skill.id)-removed-") })
    }

    // MARK: - Catalog loading

    func testCatalogLoaderRejectsDuplicateIDs() throws {
        try writeCatalog("""
        [
          {"id":"same","name":"A","summary":"","version":"1.0.0","package":null,"supported_agents":["doubao"]},
          {"id":"same","name":"B","summary":"","version":"1.0.0","package":null,"supported_agents":["claude"]}
        ]
        """)
        XCTAssertThrowsError(try OfficialSkillCatalog.load(from: resources)) { error in
            XCTAssertEqual(
                (error as? SkillError)?.errorDescription,
                L10n.text("技能清单内容无效。")
            )
        }
    }

    func testCatalogLoaderRejectsInvalidSkillIDs() throws {
        try writeCatalog("""
        [
          {"id":"Bad.Id","name":"A","summary":"","version":"1.0.0","package":null,"supported_agents":["doubao"]}
        ]
        """)
        XCTAssertThrowsError(try OfficialSkillCatalog.load(from: resources)) { error in
            XCTAssertTrue(
                ((error as? SkillError)?.errorDescription ?? "")
                    .contains("不符合规范")
            )
        }
    }

    private func writeCatalog(_ skillsJSON: String) throws {
        let skills = resources.appendingPathComponent("Skills", isDirectory: true)
        try FileManager.default.createDirectory(at: skills, withIntermediateDirectories: true)
        let json = "{\"schema_version\":1,\"skills\":\(skillsJSON)}"
        try Data(json.utf8).write(to: skills.appendingPathComponent("catalog.json"))
    }

    private func makePackage(id: String, version: String = "1.0.0") throws -> OfficialSkill {
        let source = resources
            .appendingPathComponent("Skills", isDirectory: true)
            .appendingPathComponent(id, isDirectory: true)
        try FileManager.default.createDirectory(at: source, withIntermediateDirectories: true)
        try Data("---\nname: \(id)\nversion: \(version)\n---\n# \(id)\n".utf8)
            .write(to: source.appendingPathComponent("SKILL.md"))
        return OfficialSkill(
            id: id,
            name: id,
            summary: "",
            version: version,
            package: id,
            supportedAgents: AgentID.allCases
        )
    }

    private func rewritePackage(_ package: String, id: String, version: String) throws {
        let file = resources
            .appendingPathComponent("Skills", isDirectory: true)
            .appendingPathComponent(package, isDirectory: true)
            .appendingPathComponent("SKILL.md")
        try Data("---\nname: \(id)\nversion: \(version)\n---\n# \(id) \(version)\n".utf8)
            .write(to: file)
    }
}

final class SkillArchiveTests: XCTestCase {
    private var root: URL!

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory
            .appendingPathComponent("SkillArchiveTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: root)
    }

    func testExtractReadsWrappedPackageAndFrontmatter() throws {
        let package = makePackageDirectory(id: "zipped-skill")
        let zip = root.appendingPathComponent("wrapped.zip")
        try SkillZip.write(directory: package, rootName: "zipped-skill", to: zip)

        let stagingParent = root.appendingPathComponent("staging", isDirectory: true)
        let result = try SkillArchive.extractToStaging(
            archivePath: zip,
            stagingParent: stagingParent
        )
        XCTAssertEqual(result.info.id, "zipped-skill")
        XCTAssertEqual(result.info.version, "1.0.0")
        XCTAssertFalse(result.info.displayName.isEmpty)
        XCTAssertTrue(
            FileManager.default.fileExists(
                atPath: result.directory.appendingPathComponent("SKILL.md").path
            )
        )
    }

    func testExtractFallsBackToFolderNameWhenFrontmatterHasNoName() throws {
        let package = root.appendingPathComponent("bare-dir", isDirectory: true)
        try FileManager.default.createDirectory(at: package, withIntermediateDirectories: true)
        try Data("# 只有标题\n".utf8).write(to: package.appendingPathComponent("SKILL.md"))
        let zip = root.appendingPathComponent("bare.zip")
        try SkillZip.write(directory: package, rootName: "folder-id", to: zip)

        let info = try SkillArchive.readInfo(
            packageDirectory: package,
            fallbackID: "folder-id"
        )
        XCTAssertEqual(info.id, "folder-id")
        XCTAssertEqual(info.displayName, "只有标题")
        XCTAssertEqual(info.version, "1.0.0")

        let result = try SkillArchive.extractToStaging(
            archivePath: zip,
            stagingParent: root.appendingPathComponent("s2", isDirectory: true)
        )
        XCTAssertEqual(result.info.id, "folder-id")
    }

    func testExtractRejectsTraversalAndAbsolutePaths() throws {
        for bad in ["../evil.md", "/abs/x.md", "a/../../evil.md"] {
            let zip = root.appendingPathComponent("bad-\(UUID().uuidString).zip")
            try writeRawZip(entries: [
                (name: "pkg/SKILL.md", body: "---\nname: ok\n---\n"),
                (name: bad, body: "x"),
            ], to: zip)
            XCTAssertThrowsError(
                try SkillArchive.extractToStaging(
                    archivePath: zip,
                    stagingParent: root.appendingPathComponent("s-\(UUID().uuidString)", isDirectory: true)
                )
            ) { error in
                XCTAssertEqual(
                    (error as? SkillError)?.errorDescription,
                    L10n.text("压缩包包含不安全的路径。")
                )
            }
        }
    }

    func testExtractRejectsMissingAndMultipleSkillMD() throws {
        let none = root.appendingPathComponent("none.zip")
        try writeRawZip(entries: [(name: "pkg/readme.txt", body: "hi")], to: none)
        XCTAssertThrowsError(
            try SkillArchive.extractToStaging(
                archivePath: none,
                stagingParent: root.appendingPathComponent("s3", isDirectory: true)
            )
        )

        let many = root.appendingPathComponent("many.zip")
        try writeRawZip(entries: [
            (name: "a/SKILL.md", body: "---\nname: a\n---\n"),
            (name: "b/SKILL.md", body: "---\nname: b\n---\n"),
        ], to: many)
        XCTAssertThrowsError(
            try SkillArchive.extractToStaging(
                archivePath: many,
                stagingParent: root.appendingPathComponent("s4", isDirectory: true)
            )
        ) { error in
            XCTAssertEqual(
                (error as? SkillError)?.errorDescription,
                L10n.text("压缩包中有多个 SKILL.md，无法确定技能目录。")
            )
        }
    }

    func testExtractRejectsInvalidSkillIDAndCorruptArchive() throws {
        let badID = root.appendingPathComponent("badid.zip")
        try writeRawZip(entries: [
            (name: "pkg/SKILL.md", body: "---\nname: Bad_Id\n---\n# x\n")
        ], to: badID)
        XCTAssertThrowsError(
            try SkillArchive.extractToStaging(
                archivePath: badID,
                stagingParent: root.appendingPathComponent("s5", isDirectory: true)
            )
        )

        let corrupt = root.appendingPathComponent("corrupt.zip")
        try Data("not a zip".utf8).write(to: corrupt)
        XCTAssertThrowsError(
            try SkillArchive.extractToStaging(
                archivePath: corrupt,
                stagingParent: root.appendingPathComponent("s6", isDirectory: true)
            )
        )
    }

    private func makePackageDirectory(id: String) -> URL {
        let directory = root.appendingPathComponent("pkg-\(id)", isDirectory: true)
        try? FileManager.default.createDirectory(
            at: directory.appendingPathComponent("scripts", isDirectory: true),
            withIntermediateDirectories: true
        )
        try? Data("---\nname: \(id)\nversion: \"1.0.0\"\n---\n# 标题 \(id)\n".utf8)
            .write(to: directory.appendingPathComponent("SKILL.md"))
        try? Data("echo hi\n".utf8)
            .write(to: directory.appendingPathComponent("scripts/run.sh"))
        return directory
    }

    /// A minimal stored-zip writer that accepts arbitrary entry names, so
    /// tests can mint archives `SkillZip.write` would never produce.
    private func writeRawZip(
        entries: [(name: String, body: String)],
        to destination: URL
    ) throws {
        var archive = Data()
        var central = Data()
        for entry in entries {
            let name = Data(entry.name.utf8)
            let body = Data(entry.body.utf8)
            var crc: UInt32 = 0xFFFFFFFF
            for byte in body {
                crc ^= UInt32(byte)
                for _ in 0..<8 {
                    crc = (crc & 1 == 1) ? (crc >> 1) ^ 0xEDB88320 : crc >> 1
                }
            }
            crc ^= 0xFFFFFFFF
            let offset = UInt32(archive.count)
            archive.appendLE32(0x04034b50)
            archive.appendLE16(20); archive.appendLE16(0x0800)
            archive.appendLE16(0); archive.appendLE16(0); archive.appendLE16(0)
            archive.appendLE32(crc)
            archive.appendLE32(UInt32(body.count)); archive.appendLE32(UInt32(body.count))
            archive.appendLE16(UInt16(name.count)); archive.appendLE16(0)
            archive.append(name); archive.append(body)

            central.appendLE32(0x02014b50)
            central.appendLE16(20); central.appendLE16(20); central.appendLE16(0x0800)
            central.appendLE16(0); central.appendLE16(0); central.appendLE16(0)
            central.appendLE32(crc)
            central.appendLE32(UInt32(body.count)); central.appendLE32(UInt32(body.count))
            central.appendLE16(UInt16(name.count))
            central.appendLE16(0); central.appendLE16(0); central.appendLE16(0)
            central.appendLE16(0); central.appendLE32(0); central.appendLE32(offset)
            central.append(name)
        }
        let centralOffset = UInt32(archive.count)
        archive.append(central)
        archive.appendLE32(0x06054b50)
        archive.appendLE16(0); archive.appendLE16(0)
        archive.appendLE16(UInt16(entries.count)); archive.appendLE16(UInt16(entries.count))
        archive.appendLE32(UInt32(central.count)); archive.appendLE32(centralOffset)
        archive.appendLE16(0)
        try archive.write(to: destination)
    }
}

private extension Data {
    mutating func appendLE16(_ value: UInt16) {
        append(UInt8(value & 0xFF))
        append(UInt8(value >> 8))
    }

    mutating func appendLE32(_ value: UInt32) {
        append(UInt8(value & 0xFF))
        append(UInt8((value >> 8) & 0xFF))
        append(UInt8((value >> 16) & 0xFF))
        append(UInt8((value >> 24) & 0xFF))
    }
}
