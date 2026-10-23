import AppKit
import SwiftUI
import UniformTypeIdentifiers
import WeChatBridgeCore

struct SceneSettingsView: View {
    @ObservedObject var preferences: Preferences
    @ObservedObject var skills: SkillLibrary
    @ObservedObject var router: SettingsRouter

    @State private var page = ScenePage.scenes
    @State private var selectedSceneID: String?
    @State private var selectedGroupKey: String?
    @State private var sceneSearch = ""
    @State private var groupSearch = ""
    @State private var importing = false
    @State private var pendingImport: ScenePackage?
    @State private var deferredImports: [ScenePackage] = []
    @State private var notice: SceneNotice?
    @State private var dropTargeted = false

    var body: some View {
        VStack(alignment: .leading, spacing: Space.l) {
            Text(L10n.text("管理转发时附加的提示词，以及它们适用的 Agent。"))
                .font(Typo.paneBody)
                .foregroundStyle(Theme.inkSecondary)

            pagePicker

            Group {
                switch page {
                case .scenes: scenePage
                case .groups: groupPage
                }
            }
            .frame(minHeight: 500, alignment: .top)

            if let notice { Notice(notice.message, tone: notice.tone) }
        }
        .fileImporter(
            isPresented: $importing,
            allowedContentTypes: [.json],
            allowsMultipleSelection: true
        ) { result in
            switch result {
            case .success(let urls): importFiles(urls)
            case .failure(let error): notice = SceneNotice(error.localizedDescription, tone: .bad)
            }
        }
        .alert(L10n.text("场景版本已存在"), isPresented: Binding(
            get: { pendingImport != nil },
            set: {
                if !$0 {
                    pendingImport = nil
                    importDeferredPackages()
                }
            }
        )) {
            Button(L10n.text("取消"), role: .cancel) {
                pendingImport = nil
                notice = SceneNotice(L10n.text("已取消导入。"), tone: .bad)
                importDeferredPackages()
            }
            Button(L10n.text("覆盖")) {
                let package = pendingImport
                pendingImport = nil
                if let package { apply(package, force: true) }
                importDeferredPackages()
            }
        } message: {
            Text(L10n.format(
                "「%@」已安装同版本场景。覆盖将更新场景内容，本地的启用状态和群绑定会保留。",
                pendingImport?.name ?? ""
            ))
        }
        .onAppear {
            selectedSceneID = selectedSceneID ?? preferences.scenes.scenes.first?.id
            selectedGroupKey = selectedGroupKey ?? allGroupKeys.first
        }
        .onChange(of: preferences.scenes.scenes.map(\.id)) { _, ids in
            if let selectedSceneID, ids.contains(selectedSceneID) { return }
            selectedSceneID = ids.first
        }
        .onChange(of: preferences.groupMemory.keys.sorted()) { _, keys in
            if let selectedGroupKey, keys.contains(selectedGroupKey) { return }
            selectedGroupKey = keys.first
        }
        // The skills pane's 用于 N 个场景 badge lands here.
        .onChange(of: router.skillFocus) { _, skillID in
            guard let skillID else { return }
            router.skillFocus = nil
            page = .scenes
            if let scene = preferences.scenes.scenes.first(where: {
                $0.effectiveSkillIDs.contains(skillID)
            }) {
                selectedSceneID = scene.id
            }
        }
    }

    private var pagePicker: some View {
        HStack(spacing: 2) {
            ForEach(ScenePage.allCases) { item in
                Button {
                    page = item
                } label: {
                    Text(item.title)
                        .font(Typo.paneBodyStrong)
                        .foregroundStyle(page == item ? Theme.ink : Theme.inkSecondary)
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 8)
                        .background(
                            page == item ? Theme.brandTint : Color.clear,
                            in: RoundedRectangle(cornerRadius: Radius.row, style: .continuous)
                        )
                }
                .buttonStyle(PlainPressButtonStyle(staticFeedback: true))
            }
        }
        .padding(2)
        .frame(width: 300)
        .background(Theme.sunken, in: RoundedRectangle(cornerRadius: Radius.control, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: Radius.control, style: .continuous)
                .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
        )
    }

    private var scenePage: some View {
        HStack(alignment: .top, spacing: Space.s) {
            sceneList.frame(width: 250)

            if let scene = selectedSceneBinding {
                SceneEditor(
                    scene: scene,
                    enabled: enabledBinding(for: scene.wrappedValue.id),
                    isDefault: preferences.scenes.defaultSceneID == scene.wrappedValue.id,
                    toggleDefault: { toggleDefault(sceneID: scene.wrappedValue.id) },
                    skills: skills,
                    duplicate: { duplicate(scene.wrappedValue) },
                    moveUp: { move(scene.wrappedValue, by: -1) },
                    moveDown: { move(scene.wrappedValue, by: 1) },
                    export: { export(scene.wrappedValue) },
                    remove: { remove(scene.wrappedValue) },
                    save: { notice = SceneNotice(L10n.text("已保存更改。"), tone: .good) }
                )
                .id(scene.wrappedValue.id)
            } else {
                EmptyPanel(
                    title: L10n.text("选择一个场景"),
                    detail: L10n.text("在左侧选择场景后，可以查看说明、启停和编辑。")
                )
            }
        }
    }

    private var sceneList: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: Space.s) {
                Text(L10n.text("场景")).font(Typo.rowTitle)
                Text(L10n.format("%d 个", preferences.scenes.scenes.count))
                    .font(Typo.paneCaption)
                    .foregroundStyle(Theme.inkTertiary)
                Spacer(minLength: 0)
                Button { addScene() } label: {
                    Label(L10n.text("新建"), systemImage: "plus")
                }
                .buttonStyle(SettingsActionButtonStyle(primary: true, width: nil))
            }
            .padding(Space.m)

            TextField(L10n.text("搜索场景"), text: $sceneSearch)
                .textFieldStyle(SettingsTextFieldStyle())
                .padding(.horizontal, Space.m)
                .padding(.bottom, Space.s)

            Divider()
            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(filteredSceneIndices, id: \.self) { index in
                        let scene = preferences.scenes.scenes[index]
                        SceneListRow(
                            scene: scene,
                            hotkey: shortcutHint(for: scene.id),
                            selected: selectedSceneID == scene.id,
                            select: { selectedSceneID = scene.id }
                        )
                        if index != filteredSceneIndices.last { Divider() }
                    }
                }
            }
            .scrollBounceBehavior(.basedOnSize)
            Spacer(minLength: Space.s)
            VStack(alignment: .leading, spacing: 6) {
                Button(L10n.text("导入 JSON")) { importing = true }
                    .buttonStyle(.link)
                Text(L10n.text("快捷键：⌃⌥1–9 把对应的启用场景用于下次转发；可把场景包 JSON 拖进此列表导入。"))
                    .font(Typo.micro)
                    .foregroundStyle(Theme.inkTertiary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(Space.m)
        }
        .frame(maxHeight: .infinity, alignment: .top)
        .background(Theme.surface, in: RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
        .overlay(panelBorder)
        .background(
            dropTargeted ? Theme.accentSoft : Color.clear,
            in: RoundedRectangle(cornerRadius: Radius.card, style: .continuous)
        )
        .onDrop(of: [UTType.fileURL.identifier], isTargeted: $dropTargeted) { importDropped($0) }
    }

    private var groupPage: some View {
        HStack(alignment: .top, spacing: Space.s) {
            groupList.frame(width: 300)

            if let key = selectedGroupKey, let memory = preferences.groupMemory[key] {
                GroupBindingEditor(
                    memory: memory,
                    enabledScenes: preferences.scenes.enabledScenes,
                    disabledScenes: preferences.scenes.scenes.filter { !$0.enabled },
                    boundScenes: preferences.scenes.scenes(ids: memory.boundSceneIDs),
                    toggle: { toggleBinding(sceneID: $0, groupKey: key) },
                    clear: { clearBinding(groupKey: key) }
                )
                .id(key)
            } else {
                EmptyPanel(
                    title: L10n.text("还没有群聊记录"),
                    detail: L10n.text("从微信转发一次后，就可以在这里为群聊绑定场景。")
                )
            }
        }
    }

    private var groupList: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: Space.s) {
                Text(L10n.text("群聊")).font(Typo.rowTitle)
                Text(L10n.format("%d 个已绑定", boundGroupCount))
                    .font(Typo.paneCaption)
                    .foregroundStyle(Theme.inkTertiary)
                Spacer(minLength: 0)
            }
            .padding(Space.m)

            TextField(L10n.text("搜索群聊"), text: $groupSearch)
                .textFieldStyle(SettingsTextFieldStyle())
                .padding(.horizontal, Space.m)
                .padding(.bottom, Space.s)

            Divider()
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    if !boundGroupKeys.isEmpty { groupSection(title: L10n.text("已绑定"), keys: boundGroupKeys) }
                    if !unboundGroupKeys.isEmpty { groupSection(title: L10n.text("尚未绑定"), keys: unboundGroupKeys) }
                }
            }
            .scrollBounceBehavior(.basedOnSize)
        }
        .frame(maxHeight: .infinity, alignment: .top)
        .background(Theme.surface, in: RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
        .overlay(panelBorder)
    }

    private func groupSection(title: String, keys: [String]) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(title)
                .font(Typo.paneCaption)
                .foregroundStyle(Theme.inkTertiary)
                .padding(.horizontal, Space.m)
                .padding(.top, Space.m)
                .padding(.bottom, Space.xs)
            ForEach(keys, id: \.self) { key in
                if let memory = preferences.groupMemory[key] {
                    GroupListRow(
                        name: memory.displayName,
                        sceneNames: preferences.scenes.scenes(ids: memory.boundSceneIDs).map(\.name),
                        selected: selectedGroupKey == key,
                        select: { selectedGroupKey = key }
                    )
                }
            }
        }
    }

    private var panelBorder: some View {
        RoundedRectangle(cornerRadius: Radius.card, style: .continuous)
            .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
    }

    private var filteredSceneIndices: [Int] {
        preferences.scenes.scenes.indices.filter { index in
            let query = sceneSearch.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !query.isEmpty else { return true }
            let scene = preferences.scenes.scenes[index]
            return scene.name.localizedCaseInsensitiveContains(query)
                || scene.summary.localizedCaseInsensitiveContains(query)
        }
    }

    private var allGroupKeys: [String] {
        preferences.groupMemory.keys.sorted {
            let left = preferences.groupMemory[$0]?.displayName ?? $0
            let right = preferences.groupMemory[$1]?.displayName ?? $1
            return left.localizedStandardCompare(right) == .orderedAscending
        }
    }

    private var filteredGroupKeys: [String] {
        let query = groupSearch.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty else { return allGroupKeys }
        return allGroupKeys.filter {
            preferences.groupMemory[$0]?.displayName.localizedCaseInsensitiveContains(query) == true
        }
    }

    private var boundGroupKeys: [String] {
        filteredGroupKeys.filter { !(preferences.groupMemory[$0]?.boundSceneIDs.isEmpty ?? true) }
    }

    private var unboundGroupKeys: [String] {
        filteredGroupKeys.filter { preferences.groupMemory[$0]?.boundSceneIDs.isEmpty ?? true }
    }

    private var boundGroupCount: Int {
        preferences.groupMemory.values.filter { !$0.boundSceneIDs.isEmpty }.count
    }

    private var selectedSceneBinding: Binding<WeChatScene>? {
        guard let selectedSceneID,
              let index = preferences.scenes.scenes.firstIndex(where: { $0.id == selectedSceneID })
        else { return nil }
        return $preferences.scenes.scenes[index]
    }

    /// The ⌃⌥-digit a scene answers to, by position among enabled scenes —
    /// nil for disabled scenes and anything past the ninth.
    private func shortcutHint(for sceneID: String) -> String? {
        guard let index = preferences.scenes.enabledScenes
            .firstIndex(where: { $0.id == sceneID }),
              index < 9
        else { return nil }
        return "⌃⌥\(index + 1)"
    }

    /// 设为默认场景 / 取消默认场景 — only an enabled scene may hold the slot;
    /// the resolver falls back to it when no binding, keyword or pick answers.
    private func toggleDefault(sceneID: String) {
        preferences.scenes.defaultSceneID =
            preferences.scenes.defaultSceneID == sceneID ? nil : sceneID
    }

    private func enabledBinding(for id: String) -> Binding<Bool> {
        Binding(
            get: { preferences.scenes.scenes.first { $0.id == id }?.enabled ?? false },
            set: { enabled in
                guard let index = preferences.scenes.scenes.firstIndex(where: { $0.id == id }) else { return }
                preferences.scenes.scenes[index].enabled = enabled
                guard !enabled else { return }
                if preferences.scenes.defaultSceneID == id { preferences.scenes.defaultSceneID = nil }
                removeSceneFromBindings(id)
            }
        )
    }

    private func addScene() {
        let scene = WeChatScene(name: L10n.text("新场景"), instruction: "", outputSpec: "", enabled: true)
        preferences.scenes.add(scene)
        selectedSceneID = scene.id
    }

    private func duplicate(_ scene: WeChatScene) {
        let copy = preferences.scenes.copiedAsUserTask(scene)
        preferences.scenes.add(copy)
        selectedSceneID = copy.id
        notice = SceneNotice(L10n.text("已复制为我的场景。"), tone: .good)
    }

    private func move(_ scene: WeChatScene, by offset: Int) {
        guard let index = preferences.scenes.scenes.firstIndex(where: { $0.id == scene.id }) else { return }
        let target = index + offset
        guard preferences.scenes.scenes.indices.contains(target) else { return }
        preferences.scenes.scenes.swapAt(index, target)
    }

    private func remove(_ scene: WeChatScene) {
        guard let index = preferences.scenes.scenes.firstIndex(where: { $0.id == scene.id }) else { return }
        preferences.scenes.remove(id: scene.id)
        removeSceneFromBindings(scene.id)
        for key in Array(preferences.groupMemory.keys) where preferences.groupMemory[key]?.lastSceneID == scene.id {
            preferences.groupMemory[key]?.lastSceneID = nil
        }
        selectedSceneID = preferences.scenes.scenes.indices.contains(index)
            ? preferences.scenes.scenes[index].id
            : preferences.scenes.scenes.last?.id
    }

    private func removeSceneFromBindings(_ id: String) {
        for key in Array(preferences.groupMemory.keys) {
            preferences.groupMemory[key]?.boundSceneIDs.removeAll { $0 == id }
        }
    }

    private func toggleBinding(sceneID: String, groupKey: String) {
        guard var memory = preferences.groupMemory[groupKey] else { return }
        if memory.boundSceneIDs.contains(sceneID) {
            memory.boundSceneIDs.removeAll { $0 == sceneID }
        } else {
            memory.boundSceneIDs.append(sceneID)
        }
        let selected = Set(memory.boundSceneIDs)
        memory.boundSceneIDs = preferences.scenes.scenes
            .filter { $0.enabled && selected.contains($0.id) }
            .map(\.id)
        memory.updatedAt = Date()
        preferences.groupMemory[groupKey] = memory
    }

    private func clearBinding(groupKey: String) {
        preferences.groupMemory[groupKey]?.boundSceneIDs = []
    }

    private func export(_ scene: WeChatScene) {
        guard SceneVersion(scene.packageVersion) != nil else {
            notice = SceneNotice(L10n.text("版本号必须是 1.0.0 这样的数字格式。"), tone: .bad)
            return
        }
        let panel = NSSavePanel()
        panel.allowedContentTypes = [.json]
        panel.canCreateDirectories = true
        panel.nameFieldStringValue = "\(scene.name).wechatflow-scene.json"
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let data = try ScenePackage.encoder().encode(ScenePackage(scene: scene))
            try data.write(to: url, options: .atomic)
            notice = SceneNotice(L10n.text("场景包已导出。"), tone: .good)
        } catch {
            notice = SceneNotice(error.localizedDescription, tone: .bad)
        }
    }

    private func importDropped(_ providers: [NSItemProvider]) -> Bool {
        let group = DispatchGroup()
        let box = URLBox()
        for provider in providers where provider.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier) {
            group.enter()
            provider.loadItem(forTypeIdentifier: UTType.fileURL.identifier, options: nil) { item, _ in
                defer { group.leave() }
                let url = (item as? URL)
                    ?? (item as? Data).flatMap { URL(dataRepresentation: $0, relativeTo: nil) }
                guard let url else { return }
                box.append(url)
            }
        }
        group.notify(queue: .main) { importFiles(box.urls) }
        return !providers.isEmpty
    }

    private func importFiles(_ urls: [URL]) {
        var imported: [ScenePackage] = []
        for url in urls {
            do {
                guard url.pathExtension.lowercased() == "json" else { continue }
                let package = try ScenePackage.decoder().decode(ScenePackage.self, from: Data(contentsOf: url))
                try validate(package)
                imported.append(package)
            } catch {
                notice = SceneNotice(error.localizedDescription, tone: .bad)
                return
            }
        }
        guard !imported.isEmpty else {
            notice = SceneNotice(L10n.text("没有找到可导入的场景包。"), tone: .bad)
            return
        }
        for package in imported { apply(package, force: false) }
        if pendingImport == nil {
            notice = SceneNotice(L10n.format("已导入 %d 个场景。", imported.count), tone: .good)
        }
    }

    private func validate(_ package: ScenePackage) throws {
        guard (1...ScenePackage.currentSchemaVersion).contains(package.schemaVersion) else {
            throw SceneImportError.unsupportedSchema
        }
        guard !package.id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              !package.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              !package.instruction.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              SceneVersion(package.version) != nil
        else { throw SceneImportError.invalidPackage }
    }

    private func apply(_ package: ScenePackage, force: Bool) {
        guard force || pendingImport == nil else {
            deferredImports.append(package)
            return
        }
        let localVersion = preferences.scenes.scenes
            .first(where: { $0.id == package.id })
            .flatMap { SceneVersion($0.packageVersion) }
        let incomingVersion = SceneVersion(package.version)!
        guard force || localVersion == nil || incomingVersion > localVersion! else {
            if incomingVersion == localVersion! {
                pendingImport = package
                return
            }
            notice = SceneNotice(L10n.text("已安装的场景版本更新，未导入较旧版本。"), tone: .bad)
            return
        }
        var scene = package.scene
        SkillId.migrate(&scene)
        scene.enabled = true
        preferences.scenes.replace(scene)
        selectedSceneID = scene.id
    }

    private func importDeferredPackages() {
        while pendingImport == nil, !deferredImports.isEmpty {
            apply(deferredImports.removeFirst(), force: false)
        }
    }
}

private enum ScenePage: String, CaseIterable, Identifiable {
    case scenes
    case groups
    var id: String { rawValue }
    var title: String { self == .scenes ? L10n.text("场景管理") : L10n.text("群聊匹配") }
}

private struct SceneListRow: View {
    let scene: WeChatScene
    /// The ⌃⌥N badge for scenes reachable by a global shortcut.
    let hotkey: String?
    let selected: Bool
    let select: () -> Void

    var body: some View {
        Button(action: select) {
            HStack(alignment: .top, spacing: 10) {
                Circle()
                    .fill(scene.enabled ? Theme.brandPrimary : Theme.disabled)
                    .frame(width: 7, height: 7)
                    .padding(.top, 6)
                VStack(alignment: .leading, spacing: 3) {
                    Text(scene.name.isEmpty ? L10n.text("未命名场景") : scene.name)
                        .font(Typo.paneBodyStrong)
                        .foregroundStyle(Theme.ink)
                        .lineLimit(1)
                    Text(scene.summary.isEmpty ? L10n.text("没有一句话说明") : scene.summary)
                        .font(Typo.paneCaption)
                        .foregroundStyle(Theme.inkSecondary)
                        .lineLimit(2)
                        .multilineTextAlignment(.leading)
                }
                Spacer(minLength: 0)
                if let hotkey {
                    Text(hotkey)
                        .font(Typo.paneCaption.monospaced())
                        .foregroundStyle(Theme.inkTertiary)
                        .padding(.top, 2)
                }
            }
            .padding(.horizontal, Space.m)
            .padding(.vertical, 11)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(selected ? Theme.brandTint : Color.clear)
            .contentShape(Rectangle())
        }
        .buttonStyle(PlainPressButtonStyle(staticFeedback: true))
        .accessibilityIdentifier("scene.row.\(scene.id)")
    }
}

private struct SceneEditor: View {
    @Binding var scene: WeChatScene
    @Binding var enabled: Bool
    /// Whether this scene is the library's default — the menu offers 设为默认场景.
    let isDefault: Bool
    let toggleDefault: () -> Void
    let skills: SkillLibrary
    let duplicate: () -> Void
    let moveUp: () -> Void
    let moveDown: () -> Void
    let export: () -> Void
    let remove: () -> Void
    /// 保存更改 — fields already save through bindings; the button just shows
    /// the notice, mirroring the Windows Save_Click nudge.
    let save: () -> Void

    /// Which destination the prompt preview renders for — defaults to the
    /// first agent, mirroring the Windows preview; `.none` means clipboard /
    /// a custom app with no known agent.
    @State private var previewTarget: SkillPreviewTarget = .agent(AgentID.allCases[0])

    var body: some View {
        VStack(alignment: .leading, spacing: Space.l) {
            HStack(alignment: .center, spacing: Space.s) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(scene.name.isEmpty ? L10n.text("未命名场景") : scene.name).font(Typo.paneTitle)
                    Text(enabled ? L10n.text("已启用") : L10n.text("已停用"))
                        .font(Typo.paneCaption)
                        .foregroundStyle(enabled ? Theme.positive : Theme.inkTertiary)
                }
                Spacer(minLength: Space.s)
                Toggle(L10n.text("启用"), isOn: $enabled).toggleStyle(SwitchToggleStyle())
                menu
            }

            if scene.isOfficial {
                VStack(alignment: .leading, spacing: Space.l) {
                    ReadOnlySceneField(title: L10n.text("名称"), text: scene.name)
                    ReadOnlySceneField(title: L10n.text("说明"), text: scene.summary)
                    ReadOnlySceneField(title: L10n.text("提示词"), text: promptText)
                }
            } else {
                SceneField(title: L10n.text("名称"), text: $scene.name)
                SceneField(title: L10n.text("说明"), text: $scene.summary)
                // Windows EditFields: 插入技能 sits on the prompt label row
                // so the caret is one click away.
                VStack(alignment: .leading, spacing: 5) {
                    HStack {
                        Text(L10n.text("提示词"))
                            .font(Typo.captionStrong)
                            .foregroundStyle(Theme.inkSecondary)
                        Spacer(minLength: Space.s)
                        insertSkillMenu
                    }
                    TextField(L10n.text("提示词"), text: promptBinding, axis: .vertical)
                        .lineLimit(5...9)
                        .textFieldStyle(SettingsTextFieldStyle(multiline: true))
                }
            }

            skillPanel

            VStack(alignment: .leading, spacing: Space.s) {
                Text(L10n.text("适用 Agent"))
                    .font(Typo.captionStrong)
                    .foregroundStyle(Theme.inkSecondary)
                Text(L10n.text("仅在转发到已选择的 Agent 时使用这个提示词。"))
                    .font(Typo.paneCaption)
                    .foregroundStyle(Theme.inkTertiary)
                LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: Space.s), count: 3), spacing: Space.s) {
                    ForEach(AgentID.allCases) { agent in
                        AgentChoice(
                            agent: agent,
                            selected: scene.compatibleAgents.contains(agent),
                            editable: !scene.isOfficial,
                            resourcesRoot: skills.resourcesRoot,
                            toggle: { toggle(agent) }
                        )
                    }
                }
            }

            if scene.isOfficial {
                HStack {
                    Text(L10n.text("官方模板保持只读，复制后可以修改。"))
                        .font(Typo.paneCaption)
                        .foregroundStyle(Theme.inkSecondary)
                    Spacer(minLength: Space.s)
                    Button(L10n.text("复制并编辑"), action: duplicate)
                        .buttonStyle(SettingsActionButtonStyle(primary: true, width: nil))
                }
            } else {
                // Windows EditorActions — duplicate / delete / save. Fields
                // save live through bindings, so 保存更改 is just a nudge.
                HStack(spacing: Space.s) {
                    Spacer(minLength: 0)
                    Button(L10n.text("复制"), action: duplicate)
                        .buttonStyle(SettingsActionButtonStyle(width: nil))
                    Button(L10n.text("删除"), role: .destructive, action: remove)
                        .buttonStyle(SettingsActionButtonStyle(width: nil))
                    Button(L10n.text("保存更改"), action: save)
                        .buttonStyle(SettingsActionButtonStyle(primary: true, width: nil))
                }
            }
            Spacer(minLength: 0)
        }
        .padding(Space.l)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(Theme.surface, in: RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: Radius.card, style: .continuous)
                .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
        )
    }

    private var menu: some View {
        Menu {
            if scene.isOfficial { Button(L10n.text("复制为我的场景"), action: duplicate) }
            Button(L10n.text("导出场景包"), action: export)
            Divider()
            if enabled {
                Button(
                    isDefault ? L10n.text("取消默认场景") : L10n.text("设为默认场景"),
                    action: toggleDefault
                )
            }
            Button(L10n.text("上移"), action: moveUp)
            Button(L10n.text("下移"), action: moveDown)
            if !scene.isOfficial {
                Divider()
                Button(L10n.text("删除场景"), role: .destructive, action: remove)
            }
        } label: { Image(systemName: "ellipsis") }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .accessibilityLabel(Text(L10n.text("更多")))
    }

    private var promptText: String {
        [scene.instruction, scene.outputSpec.isEmpty ? "" : L10n.format("输出规范：\n%@", scene.outputSpec)]
            .filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
            .joined(separator: "\n\n")
    }

    private var promptBinding: Binding<String> {
        Binding(
            get: { promptText },
            set: { applyPrompt($0) }
        )
    }

    /// User scenes declare skills by referencing them inline; the stored list
    /// follows the prompt so removing a `{{skill:id}}` drops the dependency.
    private func applyPrompt(_ text: String) {
        scene.instruction = text
        scene.outputSpec = ""
        var seen = Set<String>()
        scene.requiredSkillIDs = SkillReference.parse(text)
            .filter { seen.insert($0).inserted }
    }

    // MARK: - Skill references & preview

    private var previewAgent: AgentID? {
        if case .agent(let agent) = previewTarget { return agent }
        return nil
    }

    private var skillPanel: some View {
        let agent = previewAgent
        let context = skills.promptContext(agent: agent)
        let resolved = scene.effectiveSkillIDs.map { context.resolve($0) }
        let invalid = SkillReference.invalid(scene.instruction)
            + SkillReference.invalid(scene.outputSpec)
        let warnings = invalid.map {
            L10n.format("「%@」不是有效的技能 ID（只能使用小写字母、数字和连字符）。", $0)
        } + resolved.filter { $0.mode == .unknown }.map {
            L10n.format("未找到技能「%@」，转发时会提示 Agent 该技能不存在。", $0.id)
        }

        return VStack(alignment: .leading, spacing: Space.s) {
            Text(L10n.text("技能引用"))
                .font(Typo.captionStrong)
                .foregroundStyle(Theme.inkSecondary)

            if resolved.isEmpty {
                Text(L10n.text("没有引用技能。点「插入技能」可在光标处插入 {{skill:id}}。"))
                    .font(Typo.paneCaption)
                    .foregroundStyle(Theme.inkTertiary)
            } else {
                VStack(alignment: .leading, spacing: 3) {
                    ForEach(resolved, id: \.id) { skill in
                        Text("• \(skill.displayName)（\(skill.id)）：\(modeText(for: skill.mode, agent: agent))")
                            .font(Typo.paneCaption)
                            .foregroundStyle(Theme.inkSecondary)
                    }
                }
            }

            ForEach(warnings, id: \.self) { warning in
                Notice(warning, tone: .warn)
            }

            VStack(alignment: .leading, spacing: 5) {
                HStack(spacing: Space.s) {
                    Text(L10n.text("转发提示词预览"))
                        .font(Typo.captionStrong)
                        .foregroundStyle(Theme.inkSecondary)
                    Spacer(minLength: Space.s)
                    SettingsSelect(
                        title: L10n.text("预览目标"),
                        selection: $previewTarget,
                        choices: previewChoices,
                        identifier: "scene.skillPreviewAgent"
                    )
                    .frame(width: 190)
                }
                Text(previewText(context: context))
                    .font(Typo.paneCaption)
                    .foregroundStyle(Theme.inkSecondary)
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 10)
                    .padding(.vertical, 8)
                    .background(Theme.sunken, in: RoundedRectangle(cornerRadius: Radius.row))
            }
        }
    }

    /// 插入技能 — a menu of referenceable skills; the pick lands at the caret
    /// of the prompt field when it has focus, else at the end.
    private var insertSkillMenu: some View {
        Menu {
            let choices = skills.referenceableSkills()
            if choices.isEmpty {
                Text(L10n.text("没有可引用的技能"))
            }
            ForEach(choices, id: \.id) { choice in
                Button("\(choice.name)（\(choice.id)）\(librarySuffix(for: choice.id))") {
                    insertSkillToken(choice.id)
                }
            }
        } label: {
            Text(L10n.text("插入技能"))
                .font(.system(size: 11.5, weight: .medium))
                .foregroundStyle(Theme.ink)
                .padding(.horizontal, 10)
                .padding(.vertical, 3)
                .background(Theme.sunken, in: RoundedRectangle(cornerRadius: SettingsControlMetrics.radius))
                .overlay(
                    RoundedRectangle(cornerRadius: SettingsControlMetrics.radius)
                        .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
                )
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .help(L10n.text("在光标处插入 {{skill:id}} 技能引用"))
    }

    private func librarySuffix(for id: String) -> String {
        switch skills.store.state(id) {
        case .ready: return ""
        case .conflict: return L10n.text(" · 技能库中已被修改")
        case .missing: return L10n.text(" · 暂无技能包")
        }
    }

    /// Inserts the token at the live prompt field's caret when it owns the
    /// focus — the field editor is an NSTextView — otherwise appends it.
    private func insertSkillToken(_ id: String) {
        let token = SkillReference.token(id)
        if let textView = NSApp.keyWindow?.firstResponder as? NSTextView {
            textView.insertText(token, replacementRange: textView.selectedRange())
            return
        }
        var text = promptText
        if !text.isEmpty, !text.hasSuffix(" "), !text.hasSuffix("\n") {
            text += " "
        }
        applyPrompt(text + token)
    }

    private func modeText(for mode: SkillRenderMode, agent: AgentID?) -> String {
        switch mode {
        case .native:
            L10n.text("已安装到该 Agent")
        case .path:
            L10n.text("通过技能库中的 SKILL.md 引用")
        case .missing where agent == nil:
            L10n.text("技能库中暂无技能包")
        case .missing:
            L10n.text("该 Agent 无法使用，转发时会要求说明未完成部分")
        case .unknown:
            L10n.text("未找到该技能")
        }
    }

    private var previewChoices: [SettingsChoice<SkillPreviewTarget>] {
        [SettingsChoice(id: .none, title: L10n.text("无 Agent（剪贴板 / 自定义）"))]
            + AgentID.allCases.map {
                SettingsChoice(id: .agent($0), title: $0.displayName)
            }
    }

    private func previewText(context: SkillRenderContext) -> String {
        if let agent = previewAgent, !scene.compatibleAgents.contains(agent) {
            return L10n.format("这个场景不适用于 %@，转发时不会附加提示词。", agent.displayName)
        }
        return ScenePrompt.render(scene: scene, previousSummaryAt: nil, skills: context)
            ?? L10n.text("（提示词为空）")
    }

    private func toggle(_ agent: AgentID) {
        if scene.compatibleAgents.contains(agent) {
            scene.compatibleAgents.removeAll { $0 == agent }
        } else {
            scene.compatibleAgents.append(agent)
        }
    }
}

private struct AgentChoice: View {
    let agent: AgentID
    let selected: Bool
    let editable: Bool
    let resourcesRoot: URL?
    let toggle: () -> Void

    var body: some View {
        Button(action: { if editable { toggle() } }) {
            HStack(spacing: Space.s) {
                AgentLogo(agent: agent, resourcesRoot: resourcesRoot, size: 28)
                Text(agent.displayName)
                    .font(Typo.paneCaption)
                    .foregroundStyle(Theme.ink)
                    .lineLimit(1)
                Spacer(minLength: 0)
                Image(systemName: selected ? "checkmark.circle.fill" : "circle")
                    .foregroundStyle(selected ? Theme.brandPrimary : Theme.inkTertiary)
            }
            .padding(9)
            .background(selected ? Theme.brandTint : Theme.sunken, in: RoundedRectangle(cornerRadius: Radius.row))
            .overlay(
                RoundedRectangle(cornerRadius: Radius.row)
                    .strokeBorder(selected ? Theme.brandPrimary : Theme.stroke, lineWidth: Stroke.hairline)
            )
        }
        .buttonStyle(PlainPressButtonStyle(staticFeedback: true))
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

private struct GroupListRow: View {
    let name: String
    let sceneNames: [String]
    let selected: Bool
    let select: () -> Void

    var body: some View {
        Button(action: select) {
            VStack(alignment: .leading, spacing: 3) {
                Text(name).font(Typo.paneBodyStrong).foregroundStyle(Theme.ink).lineLimit(1)
                Text(sceneNames.isEmpty ? L10n.text("转发时选择或直接转发") : sceneNames.joined(separator: "、"))
                    .font(Typo.paneCaption)
                    .foregroundStyle(Theme.inkSecondary)
                    .lineLimit(2)
            }
            .padding(.horizontal, Space.m)
            .padding(.vertical, 11)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(selected ? Theme.brandTint : Color.clear)
            .overlay(alignment: .leading) {
                if selected { Rectangle().fill(Theme.brandPrimary).frame(width: 3) }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(PlainPressButtonStyle(staticFeedback: true))
    }
}

private struct GroupBindingEditor: View {
    let memory: GroupMemory
    let enabledScenes: [WeChatScene]
    let disabledScenes: [WeChatScene]
    let boundScenes: [WeChatScene]
    let toggle: (String) -> Void
    let clear: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: Space.l) {
            VStack(alignment: .leading, spacing: 3) {
                Text(memory.displayName).font(Typo.paneTitle)
                Text(L10n.format("已关联 %d 个可选场景", boundScenes.count))
                    .font(Typo.paneCaption)
                    .foregroundStyle(Theme.inkSecondary)
            }
            Notice(L10n.text("每次转发只加载其中一个场景；不适用当前 Agent 的场景会自动跳过。"))
            VStack(alignment: .leading, spacing: 3) {
                Text(L10n.text("可选场景")).font(Typo.rowTitle)
                Text(L10n.text("勾选这个群聊可使用的场景；转发时再选择其中一个。"))
                    .font(Typo.paneCaption)
                    .foregroundStyle(Theme.inkSecondary)
            }

            VStack(spacing: 0) {
                ForEach(enabledScenes) { scene in
                    let selected = memory.boundSceneIDs.contains(scene.id)
                    Button { toggle(scene.id) } label: {
                        HStack(spacing: Space.m) {
                            Image(systemName: selected ? "checkmark.square.fill" : "square")
                                .font(.system(size: 15, weight: .medium))
                                .foregroundStyle(selected ? Theme.brandPrimary : Theme.inkTertiary)
                            VStack(alignment: .leading, spacing: 3) {
                                Text(scene.name).font(Typo.paneBodyStrong).foregroundStyle(Theme.ink)
                                Text(scene.summary.isEmpty ? L10n.text("没有一句话说明") : scene.summary)
                                    .font(Typo.paneCaption)
                                    .foregroundStyle(Theme.inkSecondary)
                                    .lineLimit(1)
                                Text(scene.compatibleAgents.map(\.displayName).joined(separator: " · "))
                                    .font(Typo.micro)
                                    .foregroundStyle(Theme.inkTertiary)
                                    .lineLimit(1)
                            }
                            Spacer(minLength: 0)
                        }
                        .padding(.horizontal, Space.m)
                        .padding(.vertical, 10)
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(PlainPressButtonStyle(staticFeedback: true))
                    if scene.id != enabledScenes.last?.id { Divider() }
                }
            }
            .overlay(
                RoundedRectangle(cornerRadius: Radius.control)
                .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
            )

            if !disabledScenes.isEmpty {
                VStack(alignment: .leading, spacing: 3) {
                    Text(L10n.text("已关闭的场景")).font(Typo.captionStrong)
                    Text(disabledScenes.map(\.name).joined(separator: "、"))
                        .font(Typo.paneCaption)
                        .foregroundStyle(Theme.inkSecondary)
                    Text(L10n.text("启用后才能绑定到群聊。"))
                        .font(Typo.paneCaption)
                        .foregroundStyle(Theme.inkTertiary)
                }
            }

            Spacer(minLength: 0)
            if !memory.boundSceneIDs.isEmpty {
                HStack {
                    Spacer()
                    Button(L10n.text("取消绑定"), role: .destructive, action: clear)
                        .buttonStyle(.borderless)
                }
            }
        }
        .padding(Space.l)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(Theme.surface, in: RoundedRectangle(cornerRadius: Radius.card, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: Radius.card, style: .continuous)
                .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
        )
    }

}

private struct EmptyPanel: View {
    let title: String
    let detail: String

    var body: some View {
        VStack(spacing: Space.s) {
            Image(systemName: "square.stack.3d.up")
                .font(.system(size: 24))
                .foregroundStyle(Theme.inkTertiary)
            Text(title).font(Typo.paneBodyStrong)
            Text(detail)
                .font(Typo.paneCaption)
                .foregroundStyle(Theme.inkSecondary)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Theme.surface, in: RoundedRectangle(cornerRadius: Radius.card))
        .overlay(
            RoundedRectangle(cornerRadius: Radius.card)
                .strokeBorder(Theme.stroke, lineWidth: Stroke.hairline)
        )
    }
}

private struct SceneField: View {
    let title: String
    @Binding var text: String
    var lines: ClosedRange<Int> = 1...1

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(title).font(Typo.captionStrong).foregroundStyle(Theme.inkSecondary)
            if lines.upperBound == 1 {
                TextField(title, text: $text).textFieldStyle(SettingsTextFieldStyle())
            } else {
                TextField(title, text: $text, axis: .vertical)
                    .lineLimit(lines)
                    .textFieldStyle(SettingsTextFieldStyle(multiline: true))
            }
        }
    }
}

private struct ReadOnlySceneField: View {
    let title: String
    let text: String

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(title).font(Typo.captionStrong).foregroundStyle(Theme.inkSecondary)
            Text(text.isEmpty ? L10n.text("无") : text)
                .font(Typo.paneBody)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 10)
                .padding(.vertical, 8)
                .background(Theme.sunken, in: RoundedRectangle(cornerRadius: Radius.row))
        }
    }
}

private enum SkillPreviewTarget: Hashable {
    case none
    case agent(AgentID)
}

private struct SceneNotice {
    let message: String
    let tone: Notice<EmptyView>.Tone
    enum Tone { case good, bad }
    init(_ message: String, tone: Tone) {
        self.message = message
        self.tone = tone == .good ? .good : .bad
    }
}

private enum SceneImportError: LocalizedError {
    case unsupportedSchema
    case invalidPackage
    var errorDescription: String? {
        switch self {
        case .unsupportedSchema: L10n.text("这个场景包由更新版本生成，当前微信流无法导入。")
        case .invalidPackage: L10n.text("场景包缺少 id、名称、版本、指令或版本格式无效。")
        }
    }
}

private final class URLBox: @unchecked Sendable {
    private let lock = NSLock()
    private var storage: [URL] = []
    var urls: [URL] {
        lock.lock(); defer { lock.unlock() }
        return storage
    }
    func append(_ url: URL) {
        lock.lock(); storage.append(url); lock.unlock()
    }
}
