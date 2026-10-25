# 微信流 iOS v1 · 开发技术文档（可交给 Agent 执行）

> 日期：2026-10-23
> 读者：接手开发的 Agent 或工程师。**只读这一份加上 [档期文档](ios-schedule.zh-CN.md)，就应该能把 iOS v1 做完并验证交付。**
> 产品设计依据：[中转站第一版设计](ios-relay-v1.zh-CN.md)、[功能点确认](ios-feature-baseline.zh-CN.md)。产品形态以那两份为准；本文件把它们落成可执行的工程规格。两者冲突时，以本文件为准，并回头修正设计文档。
> 验证素材：[assets/ios-验证素材/](assets/ios-验证素材/)

---

## 0. 给执行者的工作规则（必须遵守）

1. **工作位置**：只在工作树 `/Users/wn/me-code/WeChatBridge-HUB/.worktrees/ios-relay`（分支 `feat_ai_ios_relay`）里改代码。下文所有相对路径都以这个目录为根，记作 `$ROOT`。
2. **不要碰** `/Users/wn/me-code/WeChatBridge-HUB/WeChatBridge`（主检出）。那里有用户尚未提交的云端积分改动和 `stash@{0}`。
3. **提交规则**：每个任务（§9 的 T 编号）完成并通过验收后单独提交。提交标题必须以 `[AI]` 开头，例如 `[AI] feat(ios): 分享扩展写入收件箱`。**不 push、不开 PR**，除非用户在对话里明确要求。
4. **Mac 不能退化**：任何改到 `macos/` 的提交，提交前都必须跑 `cd macos && swift test`，结果是 186 个测试（含 1 个跳过）0 失败。新增测试后，基线数相应增加。
5. **不能验证的就写明没验证**：真机步骤无法自动完成时，在 `docs/design/ios-验收记录.zh-CN.md` 里标「待真机」，不要标通过。
6. **语言**：界面文字是简体中文；代码注释用英文，风格跟 `macos/Sources/WeChatBridgeCore` 一致（解释“为什么”，不复述代码）。
7. **不做**：画中画、剪贴板监听、自定义键盘、iCloud、Handoff、邮件、云端积分、`wcb`、Shortcuts、小组件。见 §12。

---

## 1. 环境前提

| 项 | 要求 | 当前这台 Mac | 怎么检查 / 怎么补 |
|---|---|---|---|
| Xcode | 26.6 | ✅ 26.6 | `xcodebuild -version` |
| iOS 平台（含模拟器运行时） | iOS 26.5 | ❌ 未安装；装着一个 Xcode 26.6 用不了的 26.4.1 运行时（7.9 GB） | 见下方命令。约需 8–9 GB 磁盘 |
| 磁盘 | 安装平台后至少还剩 5 GB | 约 17 GB 可用 | `df -h /` |
| XcodeGen | 任意 2.x | ✅ `/opt/homebrew/bin/xcodegen` | `xcodegen --version` |
| 签名（模拟器） | 不需要账号 | ✅ | — |
| 签名（真机，含 App Group） | **付费** Apple Developer Program 团队。免费的 Personal Team 不支持 App Group，主 App 和扩展无法共享收件箱，真机端到端走不通 | 代码里使用的 Team 是 `DLKMC3ZRZQ`，但这台 Mac 的钥匙串里只有自签名的 “WeChatBridge Dev” 证书 | 用户在 Xcode → Settings → Accounts 登录付费团队账号后才能做真机验收 |

安装 iOS 平台（需要用户同意后再执行，删除操作不可撤销）：

```bash
xcrun simctl runtime delete DAE0A5F5-0151-4E6F-BC40-B5B2C8D2F37D
```

```bash
xcodebuild -downloadPlatform iOS
```

装好后确认能看到模拟器目标：

```bash
xcrun simctl list runtimes | grep "iOS 26.5"
```

**平台未安装时**仍然可以做的验证：`swift test`（Mac）和 §8.2 的 iOS SDK 类型检查。档期里依赖模拟器的任务在平台装好之前一律算阻塞，不能标完成。

---

## 2. 仓库现状（开工前先核对）

```bash
cd /Users/wn/me-code/WeChatBridge-HUB/.worktrees/ios-relay && git log --oneline -3 && git status --short
```

预期结果：

```
aa1f63a [AI] docs: 新增 iOS 功能点确认、中转站第一版设计与验证素材
af3ef33 [AI] feat: WeChatBridgeCore 支持 iOS 平台编译
b630373 Merge pull request #47 ...
```

工作区干净（本文件和档期文件除外）。

目录：

```
$ROOT/
├── macos/                 Swift 包：WeChatBridgeApp、WeChatBridgeShare、WeChatBridgeCore、测试。依赖 Sparkle 2.9.6
│   ├── Package.swift      platforms: [.macOS(.v14), .iOS(.v17)]
│   ├── Sources/WeChatBridgeCore/     ← iOS 复用的全部逻辑
│   ├── Sources/WeChatBridgeShare/    ← Mac 分享扩展，iOS 扩展的参照
│   └── Tests/
├── shared/                Skills、Screenshots、AppLogos、Design
├── windows/               C# WPF，与本任务无关
├── docs/design/           设计文档与本文件
└── ios/                   ← 本任务新建
```

已经完成的 Core 移植（`af3ef33`），**不要重做**：

| 文件 | 改动 |
|---|---|
| `Package.swift` | 加 `.iOS(.v17)` |
| `FilePasteboard.swift` | `NSPasteboard` 的 4 个方法放进 `#if canImport(AppKit)`；`shellLine`、`shellQuoted` 两端共用（`PastePlan` 依赖） |
| `AppGroup.swift` | `StorageMode.sharedRoot` 的 macOS 逻辑放进 `#if os(macOS)`；iOS 回退到本进程 Application Support，只给测试用 |
| `InboxSignal.swift` | iOS 用 Darwin 通知中心发 `com.xiangming.wechatbridge.inbox.didChange` |
| `PointerPlacement.swift` | 补 `import CoreGraphics` |

---

## 3. 输入数据规格（微信 iOS 导出）

来源：用户真机，2026-10-24。iOS 微信路径：聊天记录多选 → 转发 → 转到其他应用 → 选择手机中的应用 → 系统分享面板。

- 分享面板里给出的是**一个文件**：`聊天记录_<YYYYMMDD>_<HHMMSS>.zip`。
- ZIP：deflate 压缩；条目名是 UTF-8 字节，但**没有设置 UTF-8 标志位**（和 Mac 版导出一样，Core 已经处理）。
- 结构：

```
聊天记录.txt
聊天记录内的图片、视频和文件/
    微信图片_202610240015_1.jpg
    …
```

- `聊天记录.txt` 的格式（真实字节）：

```
·蜗牛\n2026年10月24日 00:14\n你好\n\n·蜗牛\n2026年10月24日 00:14\n[图片] 微信图片_202610240015_1.jpg\n\n·蜗牛\n2026年10月24日 00:14\n[破涕为笑]\n
```

  每条消息是 `·<发送者>` 一行、`yyyy年M月d日 HH:mm` 一行、正文（可以多行），消息之间空一行。正文里可能出现的占位：

| 占位 | 例子 | 详情页怎么显示 |
|---|---|---|
| 图片 | `[图片] 微信图片_xxx.jpg` | 内联显示同名文件 |
| 视频 | `[视频] xxx.mp4`（待真机样本确认写法） | 缩略图加播放按钮；文件不在包里时显示“未包含视频文件” |
| 文件 | `[文件] xxx.pdf`（待确认） | 文件行，点击用 QuickLook 预览 |
| 链接 | `[链接] <标题> <https://mp.weixin.qq.com/...>` | 标题卡片，点击用 Safari 打开 |
| 视频号 | `[视频号] <标题> <https://weixin.qq.com/sph/...>` | 视频号卡片，点击打开 URL |
| 表情 | `[破涕为笑]` | 原样显示文字 |

  无法识别的方括号内容一律原样显示，**不要因为解析失败而丢消息**。

测试样本：

| 样本 | 位置 | 说明 |
|---|---|---|
| 真机单人样本 | `/Users/wn/Downloads/聊天记录_20261024_001520.zip`（仓库外，用户私人数据，**不要提交进仓库**） | 6 条消息、1 张图 |
| Mac 测试夹具 | `macos/Tests/WeChatBridgeAppTests/Fixtures/collection-86.zip`、`collection-100.zip` | 可以复制给 iOS 测试用 |
| 合成样本 | 由 T1.4 的脚本生成，提交进仓库 | 多人、链接、视频号、图片、视频、文件 |

---

## 4. 架构

```
┌──────────── 微信 ────────────┐
│ 分享 ZIP                      │
└──────────────┬───────────────┘
               ▼
┌─────── WeChatBridgeiOSShare（iOS 分享扩展） ───────┐
│ ShareViewController                                │
│  ① AttachmentImporter → BatchStaging（复制原字节）  │
│  ② staging.commit(manifest, action: .collect)       │
│  ③ DiagnosticsLog / ShareFailure                    │
│  ④ InboxSignal.post()                               │
│  ⑤ ReceiptView：已存入 · N 条 · N 个附件，1.2 秒后自动关闭 │
└──────────────┬────────────────────────────────────┘
               │ App Group 容器 /Inbox/Ready/<batchID>/
               ▼
┌────────── WeChatBridgeiOS（iOS 主 App） ───────────┐
│ RelayStore（@MainActor, ObservableObject）          │
│   InboxReader.loadBatches() → [RelayEntry]          │
│   RelayEntry = ReadyBatch + 解析结果（缓存）          │
│ 页面：中转站 / 详情 / 投递面板 / 场景 / 设置 / 引导     │
│ 投递：DeliveryPlanner → DeliveryPackage             │
│   ├ App 类目标 → UIPasteboard + UIActivityViewController │
│   └ Obsidian/文件夹 → KnowledgeDelivery（书签目录）  │
└────────────────────────────────────────────────────┘
```

关键约束：

- **扩展只收和存**。扩展里不解压、不渲染详情、不弹第二个分享面板。iOS 分享扩展内存上限约 120 MB，大文件必须流式复制（`AttachmentImporter` 已经按 `Representation` 选择文件 URL 或文件表示）。
- 扩展提交的批次 `action` 一律用 `ShareAction.collect`（iOS 没有“分享即投递”的入口；`collect.needsIntent == false`，不会写 intent）。扩展 Info.plist 的 `DKShareAction` 设为 `collect`，`ShareAction.declared` 就会读到它。
- **主 App 是 Ready 目录的唯一删除者**（与 Mac 相同的约定）。
- 主 App 的解析结果（消息数、发言人、首尾时间、附件数、摘要两条）缓存在 `Ready/<batchID>/relay-cache.json`，由主 App 写；缓存缺失或 `schemaVersion` 不符时重算。

---

## 5. 工程搭建（T1.x）

### 5.1 目录

```
ios/
├── project.yml                     XcodeGen 规格（提交）；WeChatBridge.xcodeproj 不提交，加进 .gitignore
├── Config/
│   ├── App.entitlements
│   ├── Share.entitlements
│   ├── App-Info.plist
│   ├── Share-Info.plist
│   └── Signing.xcconfig            DEVELOPMENT_TEAM 等；本地覆盖放 Signing.local.xcconfig（不提交）
├── App/
│   ├── WeChatBridgeApp.swift       @main
│   ├── Model/        RelayStore.swift  RelayEntry.swift  RelayCache.swift  Preferences.swift  InboxObserver.swift
│   ├── Relay/        RelayListView.swift  RelayCardView.swift
│   ├── Detail/       TranscriptView.swift  MessageRow.swift  MessageToken.swift
│   ├── Deliver/      TargetProfile.swift  DeliveryPlanner.swift  TranscriptMerge.swift  DeliverySheet.swift  ActivitySheet.swift
│   ├── Scenes/       SceneListView.swift  SceneEditorView.swift
│   ├── Settings/     SettingsView.swift  FolderBookmark.swift
│   └── Onboarding/   OnboardingView.swift
├── ShareExtension/
│   ├── ShareViewController.swift
│   └── ReceiptView.swift
├── Resources/        Assets.xcassets（AppIcon 用 shared/AppLogos）  Localizable.xcstrings
├── Tests/            WeChatBridgeiOSTests/*.swift  Fixtures/*.zip
└── Scripts/
    ├── make-fixtures.py
    └── verify.sh                   一键跑 §8 的全部自动验证
```

### 5.2 Core 的引用方式

v1 **不搬动 Core**，iOS 工程把 `../macos` 当作本地 Swift 包引用，只链接 `WeChatBridgeCore` 这个 product。Sparkle 会被解析下载，但不会被链接进 iOS 产物。

`macos/Sources/WeChatBridgeShare/AttachmentImporter.swift` 只依赖 Foundation、UniformTypeIdentifiers 和 Core，**移进 Core**（T1.2），两端共用：

- 移到 `macos/Sources/WeChatBridgeCore/AttachmentImporter.swift`，把 `AttachmentSource`、`ImportedAttachment`、`AttachmentImporter`、`ImportRejection` 改成 `public`（含需要的 `init` 和属性）。
- Mac 的 `ShareViewController.swift` 不需要改调用，只要能编译。
- 移完跑 `swift test` 和 §8.2 的类型检查。

`macos/Sources/WeChatBridgeApp/KnowledgeDelivery.swift` 和 `FolderDelivery.swift` 也只依赖 Foundation、Darwin 和 Core，**同样移进 Core**（T4.1）并改为 `public`，测试 `Tests/KnowledgeDeliveryTests.swift`、`Tests/FolderDeliveryTests.swift` 跟着改 import（必要时把它们移到 `WeChatBridgeCoreTests`，并在 `Package.swift` 的 `WeChatBridgeDeliveryTests` 的 `sources` 中去掉对应文件）。

### 5.3 project.yml 要点

以 `ios/project.yml` 为准。target 名带 `iOS`，是为了不和 Mac 包里的 `WeChatBridgeApp`/`WeChatBridgeShare` 产品重名：

| target | 类型 | 源码 | Bundle ID |
|---|---|---|---|
| `WeChatBridgeiOS` | application | `App`、`Resources` | `com.xiangming.wechatbridge.ios` |
| `WeChatBridgeiOSShare` | app-extension | `ShareExtension` | `com.xiangming.wechatbridge.ios.share` |
| `WeChatBridgeiOSTests` | bundle.unit-test | `Tests`（`Tests/Fixtures` 作为文件夹资源） | `com.xiangming.wechatbridge.ios.tests` |

- 两个 target 都依赖本地包 `WeChatBridgeKit`（`path: ../macos`）的 `WeChatBridgeCore` 产品，主 App 内嵌扩展。
- Info.plist 和 entitlements 用 `INFOPLIST_FILE` / `CODE_SIGN_ENTITLEMENTS` 指向 `Config/` 下的文件（不用 XcodeGen 的 `info:` 生成，避免覆盖手写键）。
- 主 App 和扩展 `SWIFT_VERSION = 5.0`（和 Mac App 层一致）；Core 保持 Swift 6。
- 测试 target 用 `TEST_HOST = $(BUILT_PRODUCTS_DIR)/WeChatBridgeiOS.app/WeChatBridgeiOS`，`@testable import WeChatBridgeiOS`。
- scheme 是 `WeChatBridgeiOS`（build 两个 target，test `WeChatBridgeiOSTests`）。

`Config/Signing.xcconfig`：

```
DEVELOPMENT_TEAM = DLKMC3ZRZQ
CODE_SIGN_STYLE = Automatic
#include? "Signing.local.xcconfig"
```


### 5.4 标识与配置（全部写死，不要另起名字）

| 项 | 值 |
|---|---|
| 主 App Bundle ID | `com.xiangming.wechatbridge.ios` |
| 扩展 Bundle ID | `com.xiangming.wechatbridge.ios.share` |
| App Group | `group.com.xiangming.wechatbridge`（iOS 的 App Group 必须以 `group.` 开头；Mac 的 `DLKMC3ZRZQ.com.xiangming.wechatbridge.shared` 是 macOS 专用写法，iOS 不能用） |
| 显示名 | 微信流 |
| 最低系统 | iOS 17.0，仅 iPhone |

两个 entitlements 文件内容相同：

```xml
<key>com.apple.security.application-groups</key>
<array><string>group.com.xiangming.wechatbridge</string></array>
```

两个 Info.plist **都要**加（缺了 `DKStorageMode`，`StorageMode.current` 会默认成 `.sharedDirectory`，主 App 和扩展就各读各的目录）：

```xml
<key>DKAppGroupIdentifier</key><string>group.com.xiangming.wechatbridge</string>
<key>DKStorageMode</key><string>app-group</string>
<key>CFBundleDisplayName</key><string>微信流</string>
```

扩展 Info.plist 另加：

```xml
<key>DKShareAction</key><string>collect</string>
<key>NSExtension</key>
<dict>
  <key>NSExtensionPointIdentifier</key><string>com.apple.share-services</string>
  <key>NSExtensionPrincipalClass</key><string>$(PRODUCT_MODULE_NAME).ShareViewController</string>
  <key>NSExtensionAttributes</key>
  <dict>
    <key>NSExtensionActivationRule</key>
    <dict>
      <key>NSExtensionActivationSupportsFileWithMaxCount</key><integer>32</integer>
      <key>NSExtensionActivationSupportsImageWithMaxCount</key><integer>32</integer>
      <key>NSExtensionActivationSupportsMovieWithMaxCount</key><integer>32</integer>
    </dict>
  </dict>
</dict>
```

（`DKShareAction` 是 `ShareAction.infoDictionaryKey`，`DKAppGroupIdentifier`、`DKStorageMode` 是 `AppGroup.infoDictionaryKey`、`AppGroup.storageModeKey`，已与源码核对。）

主 App Info.plist 另加 `UIApplicationSceneManifest`（SwiftUI 默认）和 URL scheme `wechatbridge`（v1 只用于本地通知点开后定位批次：`wechatbridge://batch/<uuid>`）。

---

## 6. 模块规格

### 6.1 分享扩展 `ShareExtension/ShareViewController.swift`（T2.1）

`UIViewController` 子类；`viewDidLoad` 里启动导入，流程照搬 Mac 版 `macos/Sources/WeChatBridgeShare/ShareViewController.swift` 的 `run(action:context:)`，差异只有这些：

| Mac | iOS |
|---|---|
| 1×1 透明视图，导入完马上 `completeRequest` | 用 `UIHostingController(rootView: ReceiptView(...))` 显示收据；导入成功 1.2 秒后自动 `completeRequest`，用户也可以点「完成」立即关闭 |
| `FilePasteboard.write` | 不写剪贴板 |
| `launchContainingApp()` | 删除。iOS 扩展不能正式拉起主 App |
| `action` 有多种 | 固定 `.collect` |

必须保留的行为（Mac 版已经过真机验证）：

1. 先 `Inbox.resolve()`，再判空；容器拿不到时报 `InboxError.containerUnavailable`。
2. `BatchStaging.create(in:)` 失败与容器失败分开报错。
3. 任何一个附件失败，整批 `staging.discard()`，不留半批。
4. 提交成功后才写诊断、`inbox.pruneStaging()`、`InboxSignal.post()`。
5. 失败时 `ShareFailure.record(...)` 写进收件箱，`cancelRequest(withError:)`，收据卡显示失败原因。
6. `hasStarted` 防重入。

`ReceiptView`（SwiftUI）显示：

- 成功：「已存入中转站」、文件名、`N 个文件 · 大小`（`ByteFormat`）。**扩展里不解析消息条数**，避免为显示一个数字而读整包 ZIP（主 App 打开时再算）。
- 失败：错误文案加「完成」按钮。
- 进行中：转圈加「正在存入…」。

验收：§8.4 的 S1–S4。

### 6.2 收件箱监听 `App/Model/InboxObserver.swift`（T2.2）

- 用 `CFNotificationCenterAddObserver(CFNotificationCenterGetDarwinNotifyCenter(), ...)` 监听 `InboxSignal.didChange.rawValue`；回调切回主线程触发 `RelayStore.reload()`。
- 另外在 `scenePhase == .active` 时也 `reload()`，因为主 App 在后台时收不到 Darwin 通知。
- 回调是 C 函数指针，不能捕获上下文；用 `Unmanaged.passUnretained(self).toOpaque()` 传 observer，或用一个全局的 `AsyncStream` continuation。

### 6.3 `RelayStore` 和 `RelayEntry`（T3.1）

```swift
struct RelayEntry: Identifiable, Hashable {
    let batch: ReadyBatch                 // 来自 InboxReader
    var summary: RelaySummary?            // nil 表示还没解析完
    var id: UUID { batch.id }
}

struct RelaySummary: Codable, Hashable {
    static let schemaVersion = 1
    var schemaVersion: Int
    var title: String                     // ObsidianNote.title(chatName: batch.chatName, transcript:, archiveName:)
    var messageCount: Int?
    var senders: [String]                 // 出现顺序去重
    var start: Date?
    var end: Date?
    var preview: [String]                 // 前两条：「发送者：正文首行」，各截到 40 个字符
    var mediaCount: Int                   // ZIP 里「聊天记录内的图片、视频和文件/」下的文件数
    var parseFailed: Bool                 // 不是微信导出 ZIP（例如用户分享了普通文件）
}

@MainActor final class RelayStore: ObservableObject {
    @Published private(set) var entries: [RelayEntry] = []   // createdAt 倒序
    @Published var failures: [ShareFailure] = []             // InboxReader.consumeFailures()，展示一次
    func reload()
    func delete(_ ids: Set<UUID>) throws                     // InboxReader(removal: .delete).discard(batchID:)
    func entry(_ id: UUID) -> RelayEntry?
    func markDelivered(_ ids: [UUID], target: String, scene: String?)   // InboxReader.recordOutcome / recordContext
}
```

- 解析放在 `Task.detached(priority: .utility)`，每批一次：读 ZIP `Data`，调 `WeChatNativeArchive.transcript(_:)`、`WeChatBatchInsightsReader.read(urls:)`，数媒体条目，写 `relay-cache.json`。
- 一批里有多个文件时，只解析第一个能被 `WeChatNativeArchive.transcript` 认出的 ZIP；其余文件计入附件数。
- `markDelivered` 写 `recordOutcome(BatchOutcome(kind: .delivered, detail: <activityType 或 "obsidian"/"folder">, at: Date()), targetName: target.displayName, for: id)`，再 `recordContext(chatName:sceneID:sceneName:for:)`。状态标签从 `ReadyBatch.outcome` 和 `targetName` 读。
- 启动时调用 `InboxReader.pruneHistory(olderThan: 保留天数 * 86400, excluding: 收集中的批次ID)`。保留天数默认 7 天（与 Mac 的 `defaultHistoryRetentionDays` 一致），设置页可改为 1 / 7 / 30 / 永久。
- `InboxReader` 在 iOS 上一律用 `InboxReader(inbox:, removal: .delete)`（`Removal` 只有 `.trash` 和 `.delete` 两个 case；iOS 的 App 容器没有可用的废纸篓）。

### 6.4 中转站列表 `Relay/`（T3.2）

- `NavigationStack` + `List`。卡片：标题（`summary.title`）、两行预览、`N 条消息 · N 个附件`、时间范围（`M月d日 HH:mm–HH:mm`；跨天时两端都写日期）、状态标签（未投递 / 已投递到 X / 收集中 / 解析失败）。
- 筛选 `Picker`：全部 / 未投递 / 收集中。
- 编辑模式多选：底部工具栏「合并投递」「删除」。
- 左滑删除；删除前不二次确认，但提供 5 秒「撤销」toast。实现方式：`RelayStore` 先把这些 ID 放进 `pendingDeletion` 并从 `entries` 隐藏，5 秒后（或 App 进入后台时）才调用 `discard(batchID:)`；撤销就是从 `pendingDeletion` 移除。**不要用** `InboxReader.trashRecoverably`：它在 `.trash` 模式下依赖系统废纸篓（iOS 的 App 容器里不可用），在 `.delete` 模式下会移到名为 `TestTrash` 的测试目录。
- 空状态：一句说明加「查看如何从微信发过来」按钮（打开引导第 1 步）。
- `ShareFailure` 有内容时，列表顶部显示一次横幅。

### 6.5 详情 `Detail/`（T3.3）

- 首次打开时把 ZIP 解压到 `Caches/Relay/<batchID>/`（`WeChatNativeArchive.extract(_:to:)`，**目标目录必须先创建**）。缓存可被系统清理，打开时不存在就重解。
- 消息列表用 `LazyVStack`；`WeChatTranscriptRecord.parse(body)` 拿到 `[WeChatTranscriptRecord]`。
- 同一发送者连续消息合并头像和名字；日期变化时插入日期分隔。
- `MessageToken.tokenize(_ text: String) -> [MessageToken]`，纯函数，必须有单元测试：

```swift
enum MessageToken: Equatable {
    case text(String)
    case image(name: String)
    case video(name: String)
    case file(name: String)
    case link(title: String, url: URL)
    case channels(title: String, url: URL)   // [视频号]
}
```

  规则：一行以 `[图片] `、`[视频] `、`[文件] ` 开头，取剩下的部分作为文件名；以 `[链接] ` 或 `[视频号] ` 开头，取行内最后一个 `http(s)://` 开始的连续非空白串作为 URL，中间部分去掉首尾空白作为标题；都不匹配就是 `.text`。
- 图片：在解压目录里按文件名找，用 `UIImage(contentsOfFile:)` 降采样到屏宽显示；点开全屏、双指缩放、可分享单张。
- 解析失败（`summary.parseFailed`）：详情页显示文件列表，可以用 QuickLook 预览，并且仍然可以投递（按“其他 App”处理）。
- 底部按钮：「投递」「加入收集」。

### 6.6 目标能力表 `Deliver/TargetProfile.swift`（T5.1）

```swift
enum DeliveryFormat: String, Codable { case archive, transcriptAndImages, knowledgeNote, folder }

struct TargetProfile: Identifiable, Hashable, Codable {
    let id: String
    let displayName: String
    let format: DeliveryFormat
    let maxAttachments: Int?          // nil = 不限；超出时分多次分享，见 6.7
    let hint: String                  // 分享面板弹出前的提示：「请在下一屏选择 豆包」
    static let builtIn: [TargetProfile]
}
```

内置表（顺序就是投递面板网格的默认顺序）：

| id | 显示名 | format | maxAttachments | 依据 |
|---|---|---|---|---|
| `chatgpt` | ChatGPT | archive | nil | 真机：接受 ZIP |
| `doubao` | 豆包 | transcriptAndImages | nil（待 R2 实测后填） | 真机：拒 ZIP，收 TXT 和图片 |
| `qwen` | 千问 | transcriptAndImages | nil（待 R2） | 真机：「格式不支持」，收 TXT 和图片 |
| `workbuddy` | WorkBuddy | archive | nil | 真机：接受 ZIP |
| `claude` | Claude | transcriptAndImages | nil | 未测，先按不收 ZIP；R4 后改 |
| `deepseek` | DeepSeek | transcriptAndImages | nil | 未测，同上 |
| `obsidian` | Obsidian | knowledgeNote | — | 写入书签目录 |
| `folder` | 文件夹 | folder | — | 写入书签目录 |
| `other` | 其他 App | archive | nil | 通用兜底 |

设置页可以给每个目标切换「发送 ZIP / 发送 TXT 和图片」，覆盖值存在偏好里（键 `com.xiangming.wechatbridge.ios.targetFormatOverrides`，`[String: String]`）。用户还可以隐藏目标、调整顺序。

### 6.7 投递规划 `Deliver/DeliveryPlanner.swift`（T5.2）

纯逻辑，不碰 UIKit，必须有单元测试。

```swift
struct DeliveryRequest {
    let batches: [ReadyBatch]          // 1 个或多个（合并投递）
    let target: TargetProfile
    let prompt: String?                // ScenePrompt.render 的结果，可为空
}

struct DeliveryPackage {
    let workDirectory: URL             // tmp/Deliveries/<uuid>/，投递完成或下次启动时清理
    let shares: [[URL]]                // 一次或多次 UIActivityViewController，每次的文件列表
    let prompt: String?
    let omitted: [String]              // 这个格式没带上的文件名（视频、文件），预览里列出
}

enum DeliveryPlanner {
    static func plan(_ request: DeliveryRequest, fileManager: FileManager = .default) throws -> DeliveryPackage
}
```

规则：

- **archive**：把每批原始 ZIP 复制到 `workDirectory`（**用复制，不要直接分享 App Group 里的原件**，防止接收方或系统改动原件）。文件名保持原名，重名时加 ` (2)`。`omitted` 为空。
- **transcriptAndImages**：
  1. 每批解压到 `workDirectory/src/<batchID>/`。
  2. 一批时：输出 `聊天记录.txt` 原文（**字节不改**）和所有 `UTType(filenameExtension:)` 符合 `.image` 的文件。
  3. 多批时：用 `TranscriptMerge` 合成一份 `聊天记录.txt`；图片重名且内容不同的，改名为 `名字 (2).jpg`，并把合并后 TXT 里对应的 `[图片] 名字.jpg` 改成新名字。
  4. 视频和其他文件放进 `omitted`。
  5. TXT 永远排在第一个。
  6. `maxAttachments` 不为 nil 且文件数超出时，按 `maxAttachments` 切成多组，TXT 只放在第一组。
- **knowledgeNote / folder**：不产生 `shares`，由 6.9 处理。
- 提示词**不写进** TXT。

`TranscriptMerge`（同文件或单独文件）：

```swift
enum TranscriptMerge {
    static func render(_ records: [WeChatTranscriptRecord], timeZone: TimeZone = .current) -> String
    static func merge(_ bodies: [String], timeZone: TimeZone = .current) throws -> String
}
```

- `render` 输出与微信相同的格式：`·<sender>\n<yyyy年M月d日 HH:mm>\n<text>\n\n`，最后一条后面只留一个 `\n`。
- `merge`：各份分别 `WeChatTranscriptRecord.parse`，按 `date` 稳定排序（时间相同的保持原顺序），去掉 sender、date、text 三者完全相同的重复条，再 `render`。
- 必须满足的测试：`parse(render(records)) == records`（往返一致）；两份交错的记录合并后按时间有序；完全重复的一批合并后条数不变。

### 6.8 投递面板 `Deliver/DeliverySheet.swift` + `ActivitySheet.swift`（T5.3）

底部弹层（`.presentationDetents([.medium, .large])`）：

1. **目标网格**：`TargetProfile` 列表（去掉隐藏的），上次选过的排第一（键 `com.xiangming.wechatbridge.ios.lastTarget`）。
2. **场景**：`SceneResolver.resolve(groupName: batch.chatName, settings:, memories:, senders:)` 的结果预选；可以改成别的场景或「不带场景」。提示词由 `ScenePrompt.render(scene:previousSummaryAt:currentEnd:)` 生成，显示在可编辑的文本框里。
3. **预览**：`将发送：聊天记录_xxx.zip` 或 `将发送：聊天记录.txt + 3 张图片`；`omitted` 不为空时，显示「未随附 2 个文件」和「改发原始 ZIP」按钮（把本次的 format 临时改成 archive）。
4. **发送**：
   - 提示词非空：先 `UIPasteboard.general.string = prompt`，显示 toast「提示词已复制，可在对方输入框粘贴」。
   - 弹出 `UIActivityViewController(activityItems: [prompt(若有)] + shares[0], applicationActivities: nil)`。弹出前在面板上显示 `target.hint` 1 秒。
   - `completionWithItemsHandler`：`completed == true` 时记一次投递（`RelayStore.markDelivered`），更新 `GroupMemory.advancing(...)`；如果还有下一组 `shares`，弹出「继续发送第 2/3 组」按钮。`completed == false` 不记录。
   - **注意**：`completed` 只代表用户选了某个目标，不代表对方 App 真的收到了；`activityType` 可能是对方扩展的 Bundle ID，记录下来写进诊断，方便以后修正能力表。

`ActivitySheet` 用 `UIViewControllerRepresentable` 包装 `UIActivityViewController`。

### 6.9 Obsidian 和文件夹 `Settings/FolderBookmark.swift`（T6.1）

- 设置页「选择 Obsidian 库」「选择默认文件夹」：`UIDocumentPickerViewController(forOpeningContentTypes: [.folder])`。
- 选中后 `url.bookmarkData(options: [], includingResourceValuesForKeys: nil, relativeTo: nil)` 存进偏好（键 `com.xiangming.wechatbridge.ios.obsidianBookmark`、`...folderBookmark`）。iOS 上**不要**用 `.withSecurityScope`（那是 macOS 选项）。
- 使用时 `URL(resolvingBookmarkData:options:relativeTo:bookmarkDataIsStale:)`，`startAccessingSecurityScopedResource()`，写完 `stop...`；`isStale` 为真时重新生成书签并保存。
- 写入复用移进 Core 的 `KnowledgeDelivery.deliver(urls:vaultPath:subfolder:chatName:sceneName:)` / `deliver(urls:folderPath:...)`。子文件夹默认「微信流」。
- 成功后 toast「已保存到 Obsidian · <笔记名>」；Obsidian 目标额外提供「在 Obsidian 中打开」：`obsidian://open?path=<笔记的绝对路径，百分号编码>`（打不开就不显示按钮）。

### 6.10 场景 `Scenes/`（T7.1）

- 数据：`SceneSettings`，存在 App Group 的 `UserDefaults(suiteName: "group.com.xiangming.wechatbridge")` 里，键沿用 Mac 的 `com.xiangming.wechatbridge.scenes.v3`；首次启动用 `SceneSettings.makeDefault()`，再调用 `installOfficialScenes()`。
- 群记忆：`[String: GroupMemory]`，键 `com.xiangming.wechatbridge.groupMemory.v1`，字典键用 `GroupName.normalize(displayName)`。
- 列表：官方场景 / 我的场景；开关启用；左滑删除（官方场景只能停用）；编辑页编辑 `name`、`summary`、`instruction`、`outputSpec`、`keywords`。
- JSON 导入导出：`ScenePackage.encoder()/decoder()`，导出走分享面板，导入走文档选择器。
- 群绑定：详情页标题旁「绑定场景」，选择后写入 `GroupMemory.boundSceneIDs`。没有群名时（iOS 拿不到微信标题栏），用 `summary.title` 作为 `displayName`，并且记录 `senders`。
- 识别：投递面板打开时调用 `SceneResolver.resolve(..., senders: Set(summary.senders))`。`GroupFingerprint` 至少要 2 个发言人，阈值 0.7。单人聊天识别不到是预期行为。

### 6.11 合并收集（T8.1）

- 两种入口：列表多选后「合并投递」（一次性，不建收集）；详情页「加入收集」（复用 `BatchCollectionLedger`，存在 App Group 的 `Inbox/collections.json`，用 `load(from:)` / `save(to:)`）。
- 收集也显示成一张卡片（顶部置顶），可以改名、移出批次、投递。投递时 `beginDelivery` → 投递 → `finishDelivery(succeeded:target:detail:scene:)`；启动时 `recoverInterruptedDeliveries()`。
- 收集中的批次不被 `pruneHistory` 清理（`ledger.protectedBatchIDs`）。

### 6.12 首次引导（T8.2）

三步，可以跳过，设置页里可以重新打开：

1. 「在微信里找到微信流」：步骤图用 `docs/design/assets/ios-验证素材/01-微信转发-转到其他应用入口.png` 和 `03-微信导出ZIP-系统分享面板.png` 重新截图或裁剪成的引导图（不要直接放用户聊天内容，**打码后**再放进 Assets）。
2. 「把微信流加到分享面板的个人收藏」：文字说明加示意图。
3. 「可选：选择 Obsidian 库」：调用 6.9。

完成状态存键 `com.xiangming.wechatbridge.ios.onboardingDone`。

### 6.13 本地化

只做简体中文。所有界面字符串放进 `Localizable.xcstrings`；Core 里的 `L10n.text(...)` 已经返回中文，不要重复翻译。

### 6.14 隐私清单

主 App 和扩展都加 `PrivacyInfo.xcprivacy`：不收集数据、不跟踪；`NSPrivacyAccessedAPITypes` 声明 `NSPrivacyAccessedAPICategoryUserDefaults`（理由 `1C8F.1`，App Group 共享）和 `NSPrivacyAccessedAPICategoryFileTimestamp`（理由 `C617.1`，`pruneStaging` 读修改时间）。

---

## 7. 偏好键汇总

全部存在 `UserDefaults(suiteName: "group.com.xiangming.wechatbridge")`。

| 键 | 类型 | 说明 |
|---|---|---|
| `com.xiangming.wechatbridge.scenes.v3` | Data（JSON `SceneSettings`） | 与 Mac 同名 |
| `com.xiangming.wechatbridge.groupMemory.v1` | Data（JSON `[String: GroupMemory]`） | 与 Mac 同名 |
| `com.xiangming.wechatbridge.historyRetentionDays` | Int | 默认 7；0 表示永久 |
| `com.xiangming.wechatbridge.obsidianSubfolder` | String | 默认「微信流」 |
| `com.xiangming.wechatbridge.folderDeliverySubfolder` | String | 默认「微信流」 |
| `com.xiangming.wechatbridge.ios.obsidianBookmark` | Data | 书签 |
| `com.xiangming.wechatbridge.ios.folderBookmark` | Data | 书签 |
| `com.xiangming.wechatbridge.ios.targetFormatOverrides` | [String: String] | 目标 id → `DeliveryFormat.rawValue` |
| `com.xiangming.wechatbridge.ios.hiddenTargets` | [String] | |
| `com.xiangming.wechatbridge.ios.targetOrder` | [String] | |
| `com.xiangming.wechatbridge.ios.lastTarget` | String | |
| `com.xiangming.wechatbridge.ios.onboardingDone` | Bool | |

---

## 8. 验证方法

### 8.1 Mac 回归（每次改到 `macos/` 都要跑）

```bash
cd /Users/wn/me-code/WeChatBridge-HUB/.worktrees/ios-relay/macos && swift test 2>&1 | grep -E "Executed .* tests" | tail -1
```

通过标准：`Executed N tests, with 1 test skipped and 0 failures`，N ≥ 186。

### 8.2 iOS SDK 类型检查（不需要安装 iOS 平台）

```bash
cd /Users/wn/me-code/WeChatBridge-HUB/.worktrees/ios-relay && SDK=$(xcrun --sdk iphonesimulator --show-sdk-path) && xcrun swiftc -typecheck -sdk "$SDK" -target arm64-apple-ios17.0-simulator -swift-version 6 -application-extension -module-name WeChatBridgeCore macos/Sources/WeChatBridgeCore/*.swift && echo TYPECHECK_OK
```

通过标准：输出 `TYPECHECK_OK`。

### 8.3 iOS 构建和单元测试（需要 iOS 26.5 平台）

```bash
cd /Users/wn/me-code/WeChatBridge-HUB/.worktrees/ios-relay/ios && xcodegen generate && xcodebuild -project WeChatBridge.xcodeproj -scheme WeChatBridgeiOS -destination 'platform=iOS Simulator,name=iPhone 17' -derivedDataPath build/DerivedData CODE_SIGNING_ALLOWED=NO test 2>&1 | tail -20
```

通过标准：`** TEST SUCCEEDED **`。模拟器名字以 `xcrun simctl list devices available` 为准。`ios/Scripts/verify.sh` 依次跑 8.1、8.2、8.3，任何一步失败就以非零退出。

iOS 单元测试至少覆盖：

| 测试文件 | 内容 |
|---|---|
| `MessageTokenTests` | 6 种占位；URL 前标题有空格；无法识别的方括号 |
| `TranscriptMergeTests` | 往返一致；交错合并有序；完全重复去重；多行正文 |
| `DeliveryPlannerTests` | archive 复制原件且字节一致；单批 TXT 字节与 ZIP 内原文一致；多批图片重名改名且 TXT 引用同步；`omitted` 列出视频和文件；`maxAttachments = 2` 时正确分组且 TXT 在第一组 |
| `RelaySummaryTests` | 用夹具 ZIP 算出的条数、发言人、媒体数、标题；非微信 ZIP 时 `parseFailed == true` |
| `TargetProfileTests` | 覆盖值生效；隐藏和排序 |

### 8.4 模拟器端到端（需要 iOS 平台；不需要账号）

模拟器上没有微信，用「文件」App 代替：

```bash
xcrun simctl boot "iPhone 17" && open -a Simulator
```

```bash
xcrun simctl addmedia booted /Users/wn/Downloads/聊天记录_20261024_001520.zip
```

如果 `addmedia` 不接受 ZIP，就把文件拖进模拟器窗口（会存进「文件」App），或者用 `xcrun simctl get_app_container booted com.apple.DocumentsApp` 不可行时改用拖拽。

| 编号 | 步骤 | 预期 |
|---|---|---|
| S1 | 「文件」App 长按 ZIP → 共享 → 微信流 | 收据卡显示「已存入中转站」，1.2 秒后自动关闭 |
| S2 | 打开微信流 | 中转站出现一张卡片：「蜗牛的聊天」、`6 条消息 · 1 个附件`、时间 `10月24日 00:14` |
| S3 | 同时选 3 个文件分享 | 一批，三个文件都在 `Ready/<id>/items/` 下 |
| S4 | 分享一个 0 字节文件 | 收据卡显示失败；Ready 下没有新增批次；主 App 列表顶部出现一次失败横幅 |
| S5 | 打开卡片 | 详情 6 条消息，第 3 条内联显示图片，表情占位按原文显示 |
| S6 | 投递 → ChatGPT → 发送 | 分享面板里有一个 ZIP 和提示词文本；剪贴板里是提示词（`xcrun simctl pbpaste booted`） |
| S7 | 投递 → 豆包 → 发送 → 选「存储到文件」 | 文件夹里有 `聊天记录.txt` 和 1 张 jpg；TXT 与 ZIP 里的原文字节一致（`cmp`） |
| S8 | 多选 2 张卡片 → 合并投递 → 豆包 → 存储到文件 | 一份合并 TXT，消息按时间排序 |
| S9 | 设置 → 选择文件夹（「我的 iPhone」下新建一个）→ 投递 → 文件夹 | 该文件夹 `微信流/` 下有 `蜗牛的聊天.md` 和 `附件/` |
| S10 | 删除卡片 → 撤销 | 卡片恢复；不撤销时 5 秒后 `Ready/<id>` 消失 |
| S11 | 杀掉 App 后从「文件」再分享一次，再打开 App | 新卡片出现（`scenePhase` 重新加载生效） |

查看 App Group 容器内容：

```bash
xcrun simctl get_app_container booted com.xiangming.wechatbridge.ios group.com.xiangming.wechatbridge
```

### 8.5 真机验收（需要付费团队签名；由用户操作，Agent 记录结果）

| 编号 | 内容 | 记录什么 |
|---|---|---|
| R1 | 微信 → 转到其他应用 → 微信流，单人和多人群各一次；多人群样本要含链接、视频号、视频、文件 | 收据卡、卡片、详情截图；把新样本的 `聊天记录.txt` 占位写法补进 §3 的表 |
| R2 | 投递到豆包、千问：TXT + 3 张图 + 提示词 | 对方收到了几个附件，文本是否进了输入框；据此填 `maxAttachments` |
| R3 | 投递到 ChatGPT、WorkBuddy：ZIP + 提示词 | 对方是否读到了 ZIP 内的聊天内容 |
| R4 | Claude、DeepSeek、WeSight 是否接受 ZIP | 更新 6.6 的表 |
| R5 | Obsidian：库放在 iCloud 和「我的 iPhone」各一次 | Obsidian 打开后能否立刻看到新笔记 |
| R6 | 30 MB 以上的 ZIP 从微信分享 | 扩展不崩溃（内存）；耗时 |
| R7 | 分享扩展发本地通知（可选，见 §11 的 Q3） | 能否发出、点开能否定位到批次 |

结果写进 `docs/design/ios-验收记录.zh-CN.md`，格式：编号、日期、设备和系统版本、App 版本、结果（通过 / 不通过 / 待真机）、证据（截图放 `docs/design/assets/ios-验收/`，**先打码聊天内容**）。

---

## 9. 任务清单（按依赖顺序）

每个任务的“完成”= 代码 + 测试 + 列出的验证全部通过 + 一个 `[AI]` 前缀的提交。工期见[档期文档](ios-schedule.zh-CN.md)。

| ID | 任务 | 依赖 | 产出 | 验证 |
|---|---|---|---|---|
| T0.1 | 安装 iOS 26.5 平台（用户同意后） | — | 可用的模拟器 | `xcrun simctl list runtimes` 有 26.5 |
| T1.1 | `ios/` 骨架：project.yml、两个 target、entitlements、Info.plist、空白 SwiftUI 首页、`.gitignore` | — | 能生成工程 | `xcodegen generate` 成功；8.3 能构建（平台未装时只做 generate） |
| T1.2 | `AttachmentImporter` 移进 Core 并 public | — | Core 变更 | 8.1 + 8.2 |
| T1.3 | `ios/Scripts/verify.sh` | T1.1 | 一键验证 | 脚本退出码 0 |
| T1.4 | `ios/Scripts/make-fixtures.py` 生成合成夹具（多人、链接、视频号、图片、小 mp4、pdf）到 `ios/Tests/Fixtures/`；条目名不带 UTF-8 标志位（若 Python 无法关掉标志位，就带标志位，同时保留复制过来的 Mac 夹具做对照） | — | 夹具 | 用 Core 的 `WeChatNativeArchive.transcript` 能解析，条数符合脚本里的预期 |
| T2.1 | 分享扩展 + 收据卡 | T1.1 T1.2 | 扩展 | 8.4 S1 S3 S4 |
| T2.2 | `InboxObserver` | T1.1 | 自动刷新 | S11；App 在前台时分享后列表 1 秒内刷新 |
| T3.1 | `RelayStore`、`RelaySummary`、缓存 | T2.1 | 数据层 | `RelaySummaryTests` |
| T3.2 | 中转站列表、筛选、多选删除、撤销 | T3.1 | 首页 | S2 S10 |
| T3.3 | 详情、`MessageToken`、图片查看 | T3.1 | 详情页 | `MessageTokenTests`；S5 |
| T4.1 | `KnowledgeDelivery`、`FolderDelivery` 移进 Core 并 public | — | Core 变更 | 8.1 + 8.2 |
| T5.1 | `TargetProfile` 和设置覆盖 | T1.1 | 能力表 | `TargetProfileTests` |
| T5.2 | `DeliveryPlanner`、`TranscriptMerge` | T5.1 | 规划逻辑 | `DeliveryPlannerTests`、`TranscriptMergeTests` |
| T5.3 | 投递面板、`ActivitySheet`、剪贴板、投递记录 | T5.2 T3.2 | 投递 | S6 S7 S8 |
| T6.1 | `FolderBookmark`、Obsidian / 文件夹投递 | T4.1 T5.3 | 知识库投递 | S9 |
| T7.1 | 场景列表、编辑、导入导出、群绑定、自动识别 | T5.3 | 场景 | 用合成多人夹具：第一次绑定场景，第二次导入同群（换一批消息，发言人重合 ≥ 70%）时自动预选同一场景（单元测试 + 手测） |
| T8.1 | 合并收集 | T5.3 | 收集 | 3 批加入收集后投递，合并 TXT 按时间排序；杀 App 重开后收集还在 |
| T8.2 | 首次引导 | T6.1 | 引导 | 首装出现；跳过后不再出现；设置里能重开 |
| T8.3 | 隐私清单、图标、本地化查漏、空状态和错误文案 | 全部 | 打磨 | Xcode 构建无 Warning 级别的隐私清单问题；所有界面无英文残留 |
| T9.1 | 真机验收 R1–R6，按结果修正能力表和占位规则 | T8.x + 付费签名 | 验收记录 | §8.5 |
| T9.2 | 交付检查（§10） | T9.1 | 交付 | §10 全部打勾 |

---

## 10. 交付定义（全部满足才算 v1 完成）

- [ ] 8.1 Mac 回归全过，测试数 ≥ 186。
- [ ] 8.2 类型检查通过。
- [ ] 8.3 iOS 单元测试全过，§8.3 表里的 5 个测试文件都存在且覆盖了列出的用例。
- [ ] 8.4 S1–S11 全部通过，结果写进验收记录。
- [ ] 8.5 R1–R5 已由用户在真机上完成并记录；R6、R7 有结论（通过、不通过或明确不做）。
- [ ] 能力表与真机结果一致，未测的目标在表里标明“未测”。
- [ ] 设计文档 [ios-relay-v1.zh-CN.md](ios-relay-v1.zh-CN.md) 的 §8 开发顺序与 §9 待测项已按结果更新。
- [ ] 所有提交标题以 `[AI]` 开头；工作区干净；未经用户同意没有 push。
- [ ] 仓库里没有用户真实聊天内容（真机样本、未打码的截图）。

---

## 11. 待决问题（需要用户回答，不要自己猜）

| # | 问题 | 影响 | 没有答案时的默认做法 |
|---|---|---|---|
| Q1 | Team `DLKMC3ZRZQ` 属于哪个账号？能不能用它做 iOS 真机签名和后续上架？ | T9.1 无法开始 | 先只做模拟器，T9.1 标阻塞 |
| Q2 | iOS 显示名用「微信流」是否有商标风险？上架中国区需要 App 备案 | 上架，不影响 v1 开发 | 开发期先用「微信流」 |
| Q3 | 分享扩展是否要发本地通知（“已存入，点此投递”）？需要主 App 先申请通知权限 | 引导多一步 | v1 不做，R7 只做技术验证 |
| Q4 | 是否在 v1 就把 Core 抽到 `shared/WeChatBridgeCore`？ | Mac 构建脚本要跟着改 | 不抽，iOS 引用 `../macos` 包 |
| Q5 | 记录默认保留 7 天是否合适？ | 存储占用 | 7 天，与 Mac 一致 |

---

## 12. 不在 v1

云端账号和积分、云端执行技能、手机作为 Mac `wcb` 的审批端、iCloud 同步、Shortcuts / App Intents、小组件、灵动岛、画中画、剪贴板监听、自定义键盘、Handoff、邮件、英文界面、iPad 布局。

---

## 附录 A：Core API 速查（以源码为准，签名有出入时先 `grep` 核对）

| API | 签名 | 备注 |
|---|---|---|
| `Inbox.resolve` | `static func resolve(appGroupIdentifier: String = AppGroup.identifier, storageMode: AppGroup.StorageMode = .current, ...) throws -> Inbox` | 目录：`staging` `ready` `diagnostics` `failures` |
| `Inbox.pruneStaging` | `func pruneStaging(olderThan: TimeInterval = 1800, now: Date = Date(), ...) -> Int` | |
| `BatchStaging.create` | `static func create(in: Inbox, batchID: UUID = UUID(), ...) throws -> BatchStaging` | |
| `BatchStaging.commit` | `func commit(manifest: BatchManifest, diagnostics: ImportDiagnostics?, intent: BatchIntent?, in: Inbox, ...) throws -> URL` | 原子 rename 到 Ready |
| `BatchManifest.init` | `(batchID:createdAt:items:action:)` | `schemaVersion = 1` |
| `InboxReader` | `init(inbox:fileManager:removal: .trash/.delete)`；`loadBatches() -> [ReadyBatch]`；`discard(batchID:)`；`pruneHistory(olderThan:now:excluding:) -> Int`；`consumeFailures() -> [ShareFailure]`；`recordOutcome(_ outcome: BatchOutcome, targetName: String? = nil, for batchID: UUID) throws`；`recordContext(chatName:sceneID:sceneName:for:) throws` | iOS 用 `.delete`；不用 `trashRecoverably` |
| `ReadyBatch` | `id directory createdAt action items outcome targetName chatName sceneID sceneName isFirstSeen byteCount` | |
| `ReadyItem` | `id batchID displayName url byteCount contentType createdAt action` | |
| `InboxSignal.post()` | iOS 走 Darwin 通知 `com.xiangming.wechatbridge.inbox.didChange` | 主 App 监听见 6.2 |
| `ShareFailure.record(_:in:)` | | 主 App 用 `consumeFailures()` 读一次 |
| `DiagnosticsLog.record(_:in:)` | 保留最近 20 条 | |
| `WeChatNativeArchive.transcript` | `static func transcript(_ data: Data, checkCancellation: () throws -> Void = {}) throws -> Transcript?` | `Transcript { path, body, records: [WeChatTranscriptRecord]?, start, end }` |
| `WeChatNativeArchive.extract` | `static func extract(_ data: Data, to: URL, ...) throws -> [String]` | **目标目录必须已存在** |
| `WeChatNativeArchive.messageCount` | `static func messageCount(_ data: Data, ...) throws -> Int?` | |
| `WeChatTranscriptRecord.parse` | `static func parse(_ body: String, timeZone: TimeZone = .current) throws -> [Self]` | `sender date text`；日期格式 `yyyy年M月d日 HH:mm` |
| `WeChatBatchInsightsReader.read` | `static func read(urls: [URL], ...) throws -> WeChatBatchInsights` | `senders: Set<String>`、`end: Date?` |
| `ObsidianNote.title` | `(chatName: String?, transcript: Transcript?, archiveName: String) -> String` | 样本得到「蜗牛的聊天」 |
| `ObsidianNote.render` / `merge` | 见源码 | `KnowledgeDelivery` 内部使用 |
| `SceneSettings` | `makeDefault()`、`installOfficialScenes()`、`enabledScenes`、`scene(id:)`、`add/remove/replace` | |
| `SceneResolver.resolve` | `(groupName: String?, settings: SceneSettings, memories: [String: GroupMemory], senders: Set<String> = [], allowDefault: Bool = true) -> SceneResolution` | `.scene`、`.scenes`、`.source` |
| `GroupFingerprint.match` | `(senders: Set<String>, memories: [String: GroupMemory]) -> String?` | 最少 2 个发言人，阈值 0.7 |
| `GroupMemory.advancing` | `(_ existing:, displayName:, sceneID:, senders:, end:, at:) -> GroupMemory` | 投递成功后调用 |
| `ScenePrompt.render` | `(scene:, previousSummaryAt:, currentEnd:, skills: nil, locale:, timeZone:) -> String?` | iOS 传 `skills: nil` |
| `BatchCollectionLedger` | `current`、`protectedBatchIDs`、`append`、`beginDelivery`、`finishDelivery`、`recoverInterruptedDeliveries`、`load(from:)`、`save(to:)` | |
| `DisplayName.sanitize` / `subfolderPath` | | |
| `ByteFormat`、`L10n` | | |

**iOS 不使用**：`ShareAction` 的除 `.collect` 以外的 case、`ForwardTarget`、`CustomForwardDecision`、`PointerPlacement`、`AppLink`、`AgentID`、`Skills`、`FilePasteboard` 的写剪贴板方法。
