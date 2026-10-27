import SwiftUI

struct OnboardingView: View {
    @EnvironmentObject private var preferences: Preferences
    let finish: () -> Void
    @State private var page = 0
    @State private var picking = false

    var body: some View {
        NavigationStack {
            TabView(selection: $page) {
                step(
                    symbol: "square.and.arrow.up.on.square",
                    title: "从微信发到微信流",
                    lines: [
                        "在微信聊天里长按一条消息，点「多选」，勾选要带走的消息。",
                        "点「转发」→「转到其他应用」→「选择手机中的应用」。",
                        "在系统分享面板里选「微信流」，看到「已保存」就好了。",
                    ]
                )
                .tag(0)
                step(
                    symbol: "star.square.on.square",
                    title: "把微信流放到分享面板前面",
                    lines: [
                        "第一次在分享面板里找不到时，滑到最右边点「更多」。",
                        "点右上角「编辑」，把「微信流」加到「个人收藏」。",
                        "以后每次分享，它都在第一排。",
                    ]
                )
                .tag(1)
                step(
                    symbol: "diamond",
                    title: "可选：选择 Obsidian 库",
                    lines: [
                        "选好后，聊天记录可以直接写成 Obsidian 笔记。",
                        "以后也可以在「设置」里选择或更换。",
                    ]
                ) {
                    Button(FolderBookmark.name(of: preferences.obsidianBookmark).map { "已选择：\($0)" } ?? "选择 Obsidian 库") {
                        picking = true
                    }
                    .buttonStyle(.bordered)
                    .frame(maxWidth: .infinity)
                    .padding(.top, 8)
                }
                .tag(2)
            }
            .tabViewStyle(.page(indexDisplayMode: .always))
            .indexViewStyle(.page(backgroundDisplayMode: .always))
            .safeAreaInset(edge: .bottom) {
                Button(page < 2 ? "下一步" : "开始使用") {
                    if page < 2 { withAnimation { page += 1 } } else { finish() }
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .padding()
            }
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) { Button("跳过", action: finish) }
            }
            .sheet(isPresented: $picking) {
                FolderPicker { url in
                    preferences.obsidianBookmark = try? FolderBookmark.make(from: url)
                }
                .ignoresSafeArea()
            }
        }
    }

    // The accessory sits above the step's spacer so it never slides under the page dots.
    private func step(symbol: String, title: String, lines: [String], @ViewBuilder accessory: () -> some View = { EmptyView() }) -> some View {
        VStack(alignment: .leading, spacing: 16) {
            Image(systemName: symbol)
                .font(.system(size: 56))
                .foregroundStyle(.tint)
                .frame(maxWidth: .infinity)
                .padding(.top, 40)
            Text(title).font(.title2.bold())
            ForEach(Array(lines.enumerated()), id: \.offset) { index, line in
                HStack(alignment: .top, spacing: 10) {
                    Text("\(index + 1)").font(.headline).foregroundStyle(.tint)
                    Text(line)
                }
            }
            accessory()
            Spacer()
        }
        .padding(24)
    }
}
