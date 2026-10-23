import QuickLook
import SwiftUI
import WeChatBridgeCore

/// A batch read back as the chat it came from.
struct RelayDetailView: View {
    @EnvironmentObject private var store: RelayStore
    @EnvironmentObject private var preferences: Preferences
    let batchID: UUID

    @State private var loaded: LoadedTranscript?
    @State private var loadError: String?
    @State private var delivering: DeliverySelection?
    @State private var binding = false
    @State private var previewURL: URL?
    @State private var toast: String?

    private var entry: RelayEntry? { store.entry(batchID) }

    var body: some View {
        Group {
            if let entry {
                content(entry)
            } else {
                ContentUnavailableView("这批记录已删除", systemImage: "tray")
            }
        }
        .navigationTitle(entry?.summary?.title ?? "聊天记录")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            if let entry, entry.summary?.parseFailed == false {
                ToolbarItem(placement: .topBarTrailing) {
                    Button("绑定场景") { binding = true }
                }
            }
        }
        .safeAreaInset(edge: .bottom) {
            if entry != nil { actionBar }
        }
        .task(id: batchID) { await load() }
        .sheet(item: $delivering) { DeliverySheet(selection: $0) }
        .sheet(isPresented: $binding) {
            if let summary = entry?.summary { SceneBindingView(summary: summary) }
        }
        .quickLookPreview($previewURL)
        .overlay(alignment: .top) { ToastView(text: $toast) }
    }

    @ViewBuilder
    private func content(_ entry: RelayEntry) -> some View {
        if let loaded {
            TranscriptView(transcript: loaded)
        } else if entry.summary?.parseFailed == true || loadError != nil {
            List {
                if let loadError { Text(loadError).foregroundStyle(.secondary) }
                Section("文件") {
                    ForEach(entry.batch.items) { item in
                        Button { previewURL = item.url } label: {
                            HStack {
                                Label(item.displayName, systemImage: "doc")
                                Spacer()
                                Text(ByteFormat.string(item.byteCount)).foregroundStyle(.secondary)
                            }
                        }
                    }
                }
            }
        } else {
            ProgressView("正在读取…").frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    private var actionBar: some View {
        HStack(spacing: 12) {
            Button {
                store.addToCollection(batchID)
                toast = "已加入收集"
            } label: {
                Label(store.collectionContaining(batchID) == nil ? "加入收集" : "已在收集中", systemImage: "square.stack.3d.up")
                    .frame(maxWidth: .infinity)
            }
            .buttonStyle(.bordered)
            .disabled(store.collectionContaining(batchID) != nil)
            Button {
                delivering = DeliverySelection(batchIDs: [batchID])
            } label: {
                Label("投递", systemImage: "paperplane.fill").frame(maxWidth: .infinity)
            }
            .buttonStyle(.borderedProminent)
        }
        .controlSize(.large)
        .padding()
        .background(.bar)
    }

    private func load() async {
        guard let entry, loaded == nil else { return }
        let batch = entry.batch
        let result = await Task.detached(priority: .userInitiated) { LoadedTranscript.load(batch) }.value
        switch result {
        case .success(let transcript): loaded = transcript
        case .failure(let error): loadError = error.localizedDescription
        case nil: break
        }
    }
}

/// The parsed transcript plus where its media was unpacked.
struct LoadedTranscript {
    let records: [WeChatTranscriptRecord]
    let mediaDirectory: URL
    /// Leaf file name → unpacked path relative to `mediaDirectory`.
    let media: [String: String]

    func url(for name: String) -> URL? {
        media[name].map { mediaDirectory.appendingPathComponent($0) }
    }

    /// nil: no WeChat archive among the batch's files.
    static func load(_ batch: ReadyBatch) -> Result<LoadedTranscript, Error>? {
        for item in batch.items where item.url.pathExtension.lowercased() == "zip" {
            do {
                let data = try Data(contentsOf: item.url, options: .mappedIfSafe)
                guard let transcript = try WeChatNativeArchive.transcript(data), let records = transcript.records else { continue }
                let extracted = try RelayFiles.extract(data, batchID: batch.id)
                var media: [String: String] = [:]
                for name in extracted.names where !name.hasSuffix("/") && name != transcript.path {
                    media[(name as NSString).lastPathComponent] = name
                }
                return .success(LoadedTranscript(records: records, mediaDirectory: extracted.directory, media: media))
            } catch {
                return .failure(error)
            }
        }
        return nil
    }
}

struct ToastView: View {
    @Binding var text: String?

    var body: some View {
        if let text {
            Text(text)
                .font(.subheadline)
                .padding(.horizontal, 16)
                .padding(.vertical, 10)
                .background(.regularMaterial, in: Capsule())
                .padding(.top, 8)
                .transition(.move(edge: .top).combined(with: .opacity))
                .task(id: text) {
                    try? await Task.sleep(nanoseconds: 2_000_000_000)
                    withAnimation { self.text = nil }
                }
        }
    }
}
