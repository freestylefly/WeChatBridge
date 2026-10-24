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
            if let entry, entry.summary?.kind == .chat {
                ToolbarItem(placement: .topBarTrailing) {
                    Button("绑定场景") { binding = true }
                }
            }
        }
        // The actions float over the content; the tab bar would only stack
        // a second bar under them.
        .toolbar(.hidden, for: .tabBar)
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
        if let loaded, let text = loaded.text {
            PlainTextView(text: text)
        } else if let loaded {
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
        HStack(spacing: 8) {
            Button {
                store.addToCollection(batchID)
                toast = "已加入收集"
            } label: {
                Label(store.collectionContaining(batchID) == nil ? "加入收集" : "已在收集中", systemImage: "square.stack.3d.up")
                    .font(.subheadline.weight(.medium))
                    .padding(.horizontal, 14)
                    .frame(maxHeight: .infinity)
            }
            .disabled(store.collectionContaining(batchID) != nil)
            Button {
                delivering = DeliverySelection(batchIDs: [batchID])
            } label: {
                Label("投递", systemImage: "paperplane.fill")
                    .font(.body.weight(.semibold))
                    .foregroundStyle(.white)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .background(Color.accentColor, in: Capsule())
            }
        }
        .frame(height: 48)
        .padding(5)
        .background(.regularMaterial, in: Capsule())
        .shadow(color: .black.opacity(0.12), radius: 12, y: 4)
        .padding(.horizontal, 20)
        .padding(.bottom, 8)
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
    /// Set instead of `records` when the text is not in chat-record layout.
    var text: String?
    let mediaDirectory: URL
    /// Leaf file name → unpacked path relative to `mediaDirectory`.
    let media: [String: String]

    func url(for name: String) -> URL? {
        media[name].map { mediaDirectory.appendingPathComponent($0) }
    }

    /// nil: no WeChat archive among the batch's files.
    static func load(_ batch: ReadyBatch) -> Result<LoadedTranscript, Error>? {
        var fallback: (Data, WeChatNativeArchive.Transcript)?
        for item in batch.items where item.url.pathExtension.lowercased() == "zip" {
            do {
                let data = try Data(contentsOf: item.url, options: .mappedIfSafe)
                guard let transcript = try WeChatNativeArchive.transcript(data) else { continue }
                // A chat export later in the batch wins, as in the summary.
                if transcript.records == nil, fallback == nil { fallback = (data, transcript) }
                guard let records = transcript.records else { continue }
                return .success(try loaded(records: records, text: nil, data: data, transcript: transcript, batchID: batch.id))
            } catch {
                return .failure(error)
            }
        }
        guard let (data, transcript) = fallback else { return nil }
        return Result { try loaded(records: [], text: transcript.body, data: data, transcript: transcript, batchID: batch.id) }
    }

    private static func loaded(
        records: [WeChatTranscriptRecord], text: String?, data: Data,
        transcript: WeChatNativeArchive.Transcript, batchID: UUID
    ) throws -> LoadedTranscript {
        let extracted = try RelayFiles.extract(data, batchID: batchID)
        var media: [String: String] = [:]
        for name in extracted.names where !name.hasSuffix("/") && name != transcript.path {
            media[(name as NSString).lastPathComponent] = name
        }
        return LoadedTranscript(records: records, text: text, mediaDirectory: extracted.directory, media: media)
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
                .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 20))
                .padding(.horizontal)
                .padding(.top, 8)
                .transition(.move(edge: .top).combined(with: .opacity))
                .task(id: text) {
                    // Two seconds for a short line; a few-line report needs reading time.
                    let seconds = max(2, Double(text.count) / 12)
                    try? await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
                    withAnimation { self.text = nil }
                }
        }
    }
}
