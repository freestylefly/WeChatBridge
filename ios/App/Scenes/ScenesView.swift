import SwiftUI
import UIKit
import UniformTypeIdentifiers
import WeChatBridgeCore

struct ScenesView: View {
    @EnvironmentObject private var preferences: Preferences
    @State private var importing = false
    @State private var message: String?
    @State private var exportURL: URL?

    private var official: [WeChatScene] { preferences.scenes.scenes.filter(\.isOfficial) }
    private var mine: [WeChatScene] { preferences.scenes.scenes.filter { !$0.isOfficial } }

    var body: some View {
        NavigationStack {
            List {
                Section("官方场景") {
                    ForEach(official) { row($0) }
                }
                Section("我的场景") {
                    ForEach(mine) { row($0) }
                        .onDelete { offsets in
                            for scene in offsets.map({ mine[$0] }) { preferences.scenes.remove(id: scene.id) }
                        }
                    NavigationLink {
                        SceneEditorView(scene: WeChatScene(name: "", instruction: "", outputSpec: SceneSettings.standardOutputSpec, enabled: true), isNew: true)
                    } label: {
                        Label("新建场景", systemImage: "plus")
                    }
                }
            }
            .navigationTitle("场景")
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Menu {
                        Button { importing = true } label: { Label("导入场景 JSON", systemImage: "square.and.arrow.down") }
                        Button(action: exportMine) { Label("导出我的场景", systemImage: "square.and.arrow.up") }
                            .disabled(mine.isEmpty)
                    } label: {
                        Image(systemName: "ellipsis.circle")
                    }
                }
            }
            .fileImporter(isPresented: $importing, allowedContentTypes: [.json], allowsMultipleSelection: true) { result in
                importScenes(result)
            }
            .alert("场景", isPresented: Binding(get: { message != nil }, set: { if !$0 { message = nil } })) {
                Button("好") { message = nil }
            } message: {
                Text(message ?? "")
            }
        }
    }

    private func row(_ scene: WeChatScene) -> some View {
        NavigationLink {
            SceneEditorView(scene: scene, isNew: false)
        } label: {
            Toggle(isOn: enabled(scene)) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(scene.name)
                    if !scene.summary.isEmpty {
                        Text(scene.summary).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                    }
                }
            }
        }
    }

    private func enabled(_ scene: WeChatScene) -> Binding<Bool> {
        // Not scene(id:) / replace(_:): the first only finds enabled scenes and
        // the second keeps the stored switch, so neither can flip it.
        Binding {
            preferences.scenes.scenes.first { $0.id == scene.id }?.enabled ?? false
        } set: { on in
            guard let index = preferences.scenes.scenes.firstIndex(where: { $0.id == scene.id }) else { return }
            preferences.scenes.scenes[index].enabled = on
        }
    }

    private func importScenes(_ result: Result<[URL], Error>) {
        do {
            var count = 0
            for url in try result.get() {
                let accessing = url.startAccessingSecurityScopedResource()
                defer { if accessing { url.stopAccessingSecurityScopedResource() } }
                let data = try Data(contentsOf: url)
                let decoder = ScenePackage.decoder()
                let packages = try (try? decoder.decode([ScenePackage].self, from: data)) ?? [decoder.decode(ScenePackage.self, from: data)]
                for package in packages {
                    // replace(_:) adds when missing but keeps a stored switch,
                    // so an import turns the scene on afterwards.
                    preferences.scenes.replace(package.scene)
                    if let index = preferences.scenes.scenes.firstIndex(where: { $0.id == package.scene.id }) {
                        preferences.scenes.scenes[index].enabled = true
                    }
                    count += 1
                }
            }
            message = "已导入 \(count) 个场景"
        } catch {
            message = "导入失败：\(error.localizedDescription)"
        }
    }

    private func exportMine() {
        do {
            let data = try ScenePackage.encoder().encode(mine.map { ScenePackage(scene: $0) })
            let url = FileManager.default.temporaryDirectory.appendingPathComponent("微信流场景.json")
            try data.write(to: url, options: .atomic)
            ActivitySheet.present(items: [url]) { _ in }
        } catch {
            message = "导出失败：\(error.localizedDescription)"
        }
    }
}

struct SceneEditorView: View {
    @EnvironmentObject private var preferences: Preferences
    @Environment(\.dismiss) private var dismiss
    @State var scene: WeChatScene
    let isNew: Bool
    @State private var keywords = ""

    var body: some View {
        Form {
            if scene.isOfficial {
                Section {
                    Text("官方场景不能直接修改，可以复制成我的场景后再改。")
                    Button("复制为我的场景") {
                        let copy = preferences.scenes.copiedAsUserTask(scene)
                        preferences.scenes.add(copy)
                        dismiss()
                    }
                }
            }
            Section("名称") {
                TextField("比如：项目群日报", text: $scene.name)
                TextField("一句话说明", text: $scene.summary)
            }
            Section("指令") {
                TextEditor(text: $scene.instruction).frame(minHeight: 120)
            }
            Section("输出规范") {
                TextEditor(text: $scene.outputSpec).frame(minHeight: 100)
            }
            Section {
                TextField("用逗号分隔", text: $keywords)
            } header: {
                Text("群名关键词")
            } footer: {
                Text("群名含有这些词时，自动选用这个场景。")
            }
        }
        .disabled(scene.isOfficial)
        .navigationTitle(isNew ? "新建场景" : scene.name)
        .toolbar {
            if !scene.isOfficial {
                ToolbarItem(placement: .confirmationAction) {
                    Button("保存", action: save)
                        .disabled(scene.name.trimmingCharacters(in: .whitespaces).isEmpty)
                }
            }
        }
        .onAppear { keywords = scene.keywords.joined(separator: "，") }
    }

    private func save() {
        scene.keywords = keywords
            .split(whereSeparator: { $0 == "," || $0 == "，" || $0 == "、" })
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
        if isNew { preferences.scenes.add(scene) } else { preferences.scenes.replace(scene) }
        dismiss()
    }
}

/// Binds a chat to scenes. iOS never sees WeChat's title bar, so the chat is
/// known by its summary title and remembered by who speaks in it.
struct SceneBindingView: View {
    @EnvironmentObject private var preferences: Preferences
    @Environment(\.dismiss) private var dismiss
    let summary: RelaySummary
    @State private var chosen: [String] = []

    var body: some View {
        NavigationStack {
            List(preferences.scenes.enabledScenes) { scene in
                Button {
                    if let index = chosen.firstIndex(of: scene.id) { chosen.remove(at: index) } else { chosen.append(scene.id) }
                } label: {
                    HStack {
                        Text(scene.name).foregroundStyle(.primary)
                        Spacer()
                        if chosen.contains(scene.id) { Image(systemName: "checkmark").foregroundStyle(.tint) }
                    }
                }
            }
            .navigationTitle("「\(summary.title)」用的场景")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("取消") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button("保存") {
                        preferences.bind(displayName: summary.title, sceneIDs: chosen, senders: Set(summary.senders))
                        dismiss()
                    }
                }
            }
            .onAppear {
                chosen = preferences.groupMemory[GroupName.normalize(summary.title)]?.boundSceneIDs ?? []
            }
        }
        .presentationDetents([.medium, .large])
    }
}
