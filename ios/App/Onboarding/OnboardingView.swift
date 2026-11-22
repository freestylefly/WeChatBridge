import SwiftUI

struct OnboardingView: View {
    @EnvironmentObject private var preferences: Preferences
    let finish: () -> Void
    @State private var page = 0
    @State private var picking = false
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    // The intro icon first bounces in large at screen centre, plays, then settles into the page.
    @State private var heroIn = false
    @State private var settled = false

    private let lastPage = 3

    var body: some View {
        NavigationStack {
            TabView(selection: $page) {
                intro.tag(0)
                step(
                    symbol: "tray.and.arrow.down",
                    title: "把信息接进来",
                    lines: [
                        "聊天记录：在微信里长按一条消息，点「多选」，勾选后点「转发」→「转到其他应用」。",
                        "网页、X 帖子、文件：点分享按钮。",
                        "在分享面板里选「信息桥」，看到「已保存」就接进来了。",
                    ]
                )
                .tag(1)
                step(
                    symbol: "star.square.on.square",
                    title: "把信息桥放到分享面板前面",
                    lines: [
                        "第一次在分享面板里找不到时，滑到最右边点「更多」。",
                        "点右上角「编辑」，把「信息桥」加到「个人收藏」。",
                        "以后每次分享，它都在第一排。",
                    ]
                )
                .tag(2)
                step(
                    symbol: "arrow.up.forward.app",
                    title: "交给你的 Agent",
                    lines: [
                        "在中转站点开一条记录，点「投递」。",
                        "选你在用的 AI 或 Agent，聊天记录和图片会一起带过去。",
                        "想让它长期记住，就写进 Obsidian 库或文件夹，当作知识库。",
                    ]
                ) {
                    Button(FolderBookmark.name(of: preferences.obsidianBookmark).map { "已选择：\($0)" } ?? "可选：选择 Obsidian 库") {
                        picking = true
                    }
                    .buttonStyle(.bordered)
                    .frame(maxWidth: .infinity)
                    .padding(.top, 8)
                }
                .tag(3)
            }
            .tabViewStyle(.page(indexDisplayMode: settled ? .always : .never))
            .indexViewStyle(.page(backgroundDisplayMode: .always))
            .safeAreaInset(edge: .bottom) {
                Button(page < lastPage ? "下一步" : "开始使用") {
                    if page < lastPage { withAnimation { page += 1 } } else { finish() }
                }
                .buttonStyle(.borderedProminent)
                .controlSize(.large)
                .padding()
                .revealed(settled, order: 5)
                .disabled(!settled)
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

    private var intro: some View {
        GeometryReader { proxy in
            VStack(alignment: .leading, spacing: 16) {
                // The 88pt slot holds the layout; the glyph is drawn at 220pt so it stays sharp while large.
                Color.clear
                    .frame(width: 88, height: 88)
                    .overlay {
                        BridgeGlyphAnimation(delay: 0.95, onBegin: bounceIn, onFinish: settle)
                            .frame(width: 220, height: 220)
                            .scaleEffect(settled ? 0.4 : heroIn ? 1 : 0.15)
                            .opacity(heroIn ? 1 : 0)
                            // 108 = page padding 24 + top padding 40 + half the slot 44.
                            .offset(y: settled ? 0 : proxy.size.height / 2 - 108)
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.top, 40)
                    .zIndex(1)
                Text("给 Agent 更多上下文").font(.title2.bold())
                    .revealed(settled, order: 0)
                Text("聊天记录、网页、文件散在各个 App 里，Agent 看不到。信息桥把它们接通，整理好交给你的 Agent。")
                    .foregroundStyle(.secondary)
                    .revealed(settled, order: 1)
                VStack(alignment: .leading, spacing: 14) {
                    feature("tray.and.arrow.down", "接进来", "从任何 App 的分享面板，一键存进信息桥。")
                        .revealed(settled, order: 2)
                    feature("text.document", "理清楚", "整理成 Agent 读得懂的格式，保留谁说的、什么时候说的，图片和附件都在。")
                        .revealed(settled, order: 3)
                    feature("arrow.up.forward.app", "交出去", "投递给你用的 AI 或 Agent，或写进笔记库，当作长期上下文。")
                        .revealed(settled, order: 4)
                }
                .padding(.top, 8)
                Spacer()
            }
            .padding(24)
        }
        .onAppear {
            if reduceMotion { heroIn = true; settled = true }
        }
    }

    // Lands as the cover finishes sliding up; the glyph starts drawing half a second later.
    private func bounceIn() {
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.45) {
            withAnimation(.spring(response: 0.55, dampingFraction: 0.6)) { heroIn = true }
        }
    }

    private func settle() {
        withAnimation(.spring(response: 0.6, dampingFraction: 0.85)) { settled = true }
    }

    private func feature(_ symbol: String, _ title: String, _ detail: String) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: symbol)
                .font(.title3)
                .foregroundStyle(.tint)
                .frame(width: 28)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.headline)
                Text(detail).font(.subheadline).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
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

private extension View {
    /// Hidden until `shown`, then fades and rises in, staggered by `order`.
    func revealed(_ shown: Bool, order: Int) -> some View {
        opacity(shown ? 1 : 0)
            .offset(y: shown ? 0 : 12)
            .animation(.easeOut(duration: 0.4).delay(shown ? 0.2 + Double(order) * 0.08 : 0), value: shown)
    }
}
