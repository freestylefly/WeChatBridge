# 微信流 iOS 中转站 · 第一版设计

> 日期：2026-10-23
> 前置文档：[iOS 版功能点确认](ios-feature-baseline.zh-CN.md)（真机实测结论与各 Agent 接收情况）
> 落地文档：[开发技术文档](ios-dev-spec.zh-CN.md)（任务、接口、验证）· [档期文档](ios-schedule.zh-CN.md)（里程碑与工期）
> 范围：第一版（v1）只做 **收进来 → 存着看 → 投出去**。云端技能、积分、Mac 联动不在本版。

## 1. 一句话

微信里转发聊天记录 → 选「转到其他应用 → 选择手机中的应用 → 微信流」→ 记录进入**中转站** → 用户随时挑一批（或合并几批），选好场景，投给 ChatGPT、豆包、千问、Obsidian 或任意 App。对方不收 ZIP 时，微信流自动改发 `聊天记录.txt` 加图片。

## 2. 已确认的前提（真机实测）

| 事实 | 来源 |
|---|---|
| iOS 微信转发聊天记录，可以通过系统分享面板导出 `聊天记录_<日期>_<时间>.zip` | 用户截图，2026-10-24 |
| ZIP 内部结构与 Mac 版一致：`聊天记录.txt` 加 `聊天记录内的图片、视频和文件/` | 用 Mac Core 解析样本，2026-10-23 |
| 现有 `WeChatNativeArchive`、`WeChatBatchInsightsReader`、`ObsidianNote` 不改代码就能正确解析这份样本 | 同上 |
| ChatGPT、WorkBuddy 接受 ZIP；豆包、千问不接受 ZIP，但接受 TXT 和图片 | 用户实测 |
| `WeChatBridgeCore` 改 4 个文件后，能以 `-application-extension` 通过 iOS 17 模拟器 SDK 的类型检查；Mac 的 186 个测试全部通过 | `feat_ai_ios_relay` 分支，`swiftc -typecheck` 加 `swift test`，2026-10-23 |

仍需实测的点列在 §9。

## 3. 用户流程

### 3.1 收：从微信进中转站

```
微信 · 聊天记录 ─▶ 转发 ─▶ 转到其他应用 ─▶ 选择手机中的应用 ─▶ 系统分享面板 ─▶ 微信流
                                                                                   │
                                            分享扩展（WeChatBridgeShare）            ▼
                     ① 复制附件到 Staging（BatchStaging，原名、原字节）
                     ② 一次性提交到 Ready（原子操作，不会出现半批）
                     ③ 解析：消息数、发言人、首尾时间、附件数（WeChatNativeArchive）
                     ④ 群识别：发言人集合与已知群做指纹匹配（GroupFingerprint）
                     ⑤ 显示收据卡片：「已存入中转站 · 6 条消息 · 1 个附件」
                        [完成]　[加入收集中的「XX群」]（有进行中的收集时显示）
```

- 扩展**只负责收和存，不负责投**。原因：iOS 扩展的内存上限很低；扩展里再弹系统分享面板，其他 App 的分享扩展通常不会出现；扩展也不能正式地拉起自家主 App。
- 收完可以发一条本地通知「已存入中转站，点此投递」，点击后打开主 App，并定位到这一批（扩展能否发通知需实测，见 §9）。
- 用户如果在分享面板里把微信流设成“个人收藏”，下次就会出现在第一排。

### 3.2 存：中转站里看和整理

- 每一批显示为一张卡片，形式参照 WorkBuddy 的「转发内容」卡片：群名或参与者、前两条消息摘要、`N 条消息 · N 个附件`、时间范围、状态（未投递 / 已投递到 X / 投递失败）。
- 点进卡片是聊天详情：按发言人和时间排列，图片内联显示。数据全部来自 `聊天记录.txt` 和解压出来的媒体。
- 可以**多选几批合并**成一个收集（复用 `BatchCollection`），合并后作为一份投出去。

### 3.3 投：选目标、选场景、发出去

```
中转站卡片 ─▶ [投递] ─▶ 投递面板
                         ├─ 选目标：ChatGPT / 豆包 / 千问 / WorkBuddy / Claude / Obsidian / 文件夹 / 其他 App
                         ├─ 选场景：按群绑定的候选场景优先，也可以不带场景
                         ├─ 预览：将发出的内容（ZIP，或者 TXT 加 N 张图）和提示词
                         └─ [发送]
                               ├─ App 类目标 → 提示词写入剪贴板 → 弹出系统分享面板 → 用户点目标 App
                               └─ Obsidian / 文件夹 → 直接写入用户授权的目录 → Toast「已保存」
```

关于 iOS 的限制，需要写进引导里：**第三方 App 不能直接把内容送进某个指定 App**，只能准备好内容，再弹出系统分享面板，由用户点一下目标。所以“选目标”这一步决定的是**发什么格式**，面板里最后点哪个 App 仍由用户决定。为了减少出错，面板弹出前会提示一句“请选择 豆包”。

提示词怎么带过去：

1. 先写入剪贴板，同时作为分享面板的文本项一起传过去（有的 App 会把它直接填进输入框）。
2. 如果对方丢掉了文本项，用户在对方输入框里长按粘贴即可。投递面板会提示「提示词已复制」。
3. 不往 `聊天记录.txt` 里插入提示词，保持原文不被改动（与 Mac 版的原则一致）。

## 4. 投递格式：目标能力表

每个目标对应一条能力描述，投递时由它决定发什么。

```swift
struct TargetProfile {
    let id: String              // "chatgpt" "doubao" "qwen" "workbuddy" "claude" "obsidian" "folder" "other"
    let acceptsZip: Bool
    let acceptsText: Bool       // .txt / .md
    let acceptsImages: Bool
    let maxAttachments: Int?    // 实测后填
}
```

| 目标 | 发送内容 | 依据 |
|---|---|---|
| ChatGPT、WorkBuddy | 原始 ZIP（合并时为多个 ZIP） | 实测接受 ZIP |
| 豆包、千问 | `聊天记录.txt` 加解压出的图片（合并时把多份 TXT 按时间拼成一份） | 实测不接受 ZIP，接受 TXT 和图片 |
| Claude、DeepSeek 等 | 默认按“不接受 ZIP”处理，实测后再改 | 待测 |
| 其他 App | 原始 ZIP | 通用兜底 |
| Obsidian | 写入 `<聊天名>的聊天.md` 加原始 ZIP | 复用 `ObsidianNote` |
| 文件夹 | 原始 ZIP 加解压内容 | 同 Mac 的文件夹投递 |

视频和文件附件：第一版对“不接受 ZIP”的目标只发图片，视频和文件在预览里列出“未随附 N 个文件”，并提供“改发原始 ZIP”的按钮。

## 5. 页面

| # | 页面 | 内容 | 对应的 Mac 功能 |
|---|---|---|---|
| 1 | 中转站（首页） | 卡片列表；筛选：全部 / 未投递 / 收集中；多选合并；左滑删除 | 记录页、分批收集 |
| 2 | 记录详情 | 聊天气泡式详情、图片预览；底部按钮「投递」「合并到…」 | 新增（参照 WorkBuddy） |
| 3 | 投递面板（底部弹层） | 目标网格、场景选择、发送内容预览、发送 | 目标选择面板、场景选择面板 |
| 4 | 场景 | 官方场景 / 我的场景；编辑；JSON 导入导出；群绑定 | 场景设置 |
| 5 | 设置 | 目标管理（显示 / 隐藏、排序）、Obsidian 库目录、默认文件夹、记录保留时间、语言、关于 | 通用、入口、记录保留 |
| 6 | 首次引导（3 步） | ① 在微信里找到微信流（配动图）② 把微信流加到分享面板个人收藏 ③ 可选：选择 Obsidian 库 | 三步设置向导 |
| E | 分享扩展收据 | 一张小卡片：已存入、条数、附件数、[完成]、[加入收集] | Share Extension |

## 6. 复用的 Core 模块

`WeChatBridgeCore` 已经以 `-application-extension` 编译，扩展里不能用的 API 编译器会直接报错，所以它天然能放进 iOS 分享扩展。

| 模块 | iOS 用途 | 处理方式 |
|---|---|---|
| `AppGroup` | 主 App 与扩展共享的目录 | 改：`StorageMode.sharedDirectory` 的回退路径只在 macOS 上编译（`#if os(macOS)`），iOS 固定使用 App Group 容器 |
| `Inbox`、`BatchStaging`、`BatchManifest`、`InboxReader`、`InboxSignal`、`InboxError` | 扩展写入 Staging → Ready，主 App 读取 Ready | 直接复用 |
| `BatchState`、`HistoryLabel` | 卡片状态与命名 | 直接复用 |
| `BatchCollection` | 多批合并 | 直接复用 |
| `Representation`、`DisplayName`、`ImportDiagnostics`、`ShareFailure` | 从 `NSItemProvider` 取文件、清理文件名、记录脱敏诊断 | 直接复用 |
| `WeChatNativeArchive`、`WeChatForward`、`WeChatBatchInsights` | 解析 ZIP、聊天记录、发言人和时间 | 直接复用（已用 iOS 样本验证） |
| `Scene`（`SceneResolver`、`GroupFingerprint`、`GroupMemory`） | 场景、群绑定、**按发言人集合识别群** | 直接复用。iOS 没有标题栏 OCR，群识别改为依赖发言人指纹（至少 2 个发言人），识别不出来时让用户手动选 |
| `ObsidianNote` | 生成 Markdown 笔记 | 直接复用 |
| `ByteFormat`、`L10n`、`DiagnosticsLog` | 大小格式化、本地化、日志 | 直接复用 |
| `PastePlan` | 描述“先提示词、后文件”的投递内容 | 复用其数据结构，iOS 上把它翻译成分享面板的条目 |
| `FilePasteboard` | macOS 剪贴板 | 写剪贴板的方法只在 macOS 上编译；`shellLine` 和 `shellQuoted` 保留，因为 `PastePlan` 会用到。iOS 另写 `UIPasteboard` 版本 |
| `ShareAction`、`ForwardTarget`、`CustomForwardDecision`、`PointerPlacement`、`AppLink` | macOS 的分享入口、自定义 App、窗口定位 | 不用。iOS 改用 §4 的 `TargetProfile` |
| `AgentID`、`Skills` | 技能安装 | v1 不用（代码可以编译，但不调用）。云端积分的 `Commerce`、`SkillMonetization` 还在 `feat_ai_wechatbridge_batch-collection_port` 的未提交改动里，main 上还没有 |

**需要新写的 iOS 代码（只放在 iOS 端）：**

- `TargetProfile` 和投递规划：按目标决定发 ZIP，还是发 TXT 加图片
- `TranscriptMerge`：多份 `聊天记录.txt` 按时间合并（按“不接受 ZIP”路线投递收集时使用）
- `RelayShareSheet`：把投递内容包装成 `UIActivityViewController` 的条目
- `FolderBookmark`：Obsidian 和文件夹的安全作用域书签（对应 Mac 的 `KnowledgeDelivery`、`FolderDelivery`，这两个在 Mac 的 App 层，需要移植其中的写入逻辑）
- 全部 SwiftUI 页面

## 7. 工程结构

建议把 Core 从 `macos/` 提到 `shared/` 下，作为独立的 Swift Package，供 Mac 和 iOS 共用。只有路径变化，Mac 的构建脚本需要跟着改引用。

```
WeChatBridge/
├── shared/
│   └── WeChatBridgeCore/              ← 从 macos/Sources/WeChatBridgeCore 移过来
│       ├── Package.swift              platforms: [.macOS(.v14), .iOS(.v17)]
│       ├── Sources/WeChatBridgeCore/
│       └── Tests/WeChatBridgeCoreTests/   Mac 和 iOS 模拟器跑同一套测试
├── macos/
│   └── Package.swift                  依赖 ../shared/WeChatBridgeCore
├── windows/
└── ios/
    ├── WeChatBridge.xcodeproj         （或者用 XcodeGen 的 project.yml 生成，避免 pbxproj 冲突）
    ├── App/                           主 App · SwiftUI
    │   ├── RelayApp.swift
    │   ├── Relay/        中转站列表、卡片、多选合并
    │   ├── Detail/       聊天详情
    │   ├── Deliver/      投递面板、TargetProfile、RelayShareSheet、TranscriptMerge
    │   ├── Scenes/       场景列表与编辑
    │   ├── Settings/     设置、FolderBookmark
    │   └── Onboarding/
    ├── ShareExtension/                分享扩展 · 只收和存
    │   ├── ShareViewController.swift  参照 macos/Sources/WeChatBridgeShare
    │   └── ReceiptView.swift
    ├── Resources/                     Localizable.strings（从 Mac 版筛选可用条目）、Assets
    └── Tests/                         TargetProfile、TranscriptMerge、投递规划的单元测试
```

**目标与签名：**

| 目标 | Bundle ID（示意） | 关键配置 |
|---|---|---|
| 主 App | `<prefix>.wechatbridge.ios` | App Group、文件夹书签 |
| 分享扩展 | `<prefix>.wechatbridge.ios.share` | `NSExtensionActivationRule` 只接受文件（ZIP、`public.data`），最多 32 项；共用同一个 App Group |

两个目标都依赖 `WeChatBridgeCore`。分享扩展不能链接任何依赖 UIKit App 生命周期的代码。

**Core 移植的改动（已在 `feat_ai_ios_relay` 完成，iOS 类型检查和 Mac 测试都通过）：**

1. `Package.swift`：平台加上 `.iOS(.v17)`。
2. `FilePasteboard.swift`：`import AppKit` 和写 `NSPasteboard` 的 4 个方法放进 `#if canImport(AppKit)`；`shellLine` 和 `shellQuoted` 两端共用。
3. `AppGroup.swift`：`sharedRoot` 在 macOS 上保持原逻辑；iOS 上改用本进程的 Application Support 目录，只给测试和预览用。iOS 包必须在 Info.plist 里声明 `DKStorageMode = app-group`，否则主 App 和扩展会各读各的收件箱。
4. `InboxSignal.swift`：iOS 没有 `DistributedNotificationCenter`，改用 Darwin 通知中心，同样不携带任何数据。
5. `PointerPlacement.swift`：补上 `import CoreGraphics`（macOS 的 Foundation 会顺带导入它，iOS 不会）。

## 7.5 参照 Yoink：借鉴、改进、不做

依据：用户录屏（2026-10-24，55 秒），完整走了一遍“微信多选消息 → 合并转发 → 其他应用 → 分享面板找到 Yoink → 存入 → 在 Yoink 里查看”。

**借鉴：**

- **存入几乎无感**：分享后只出现一行「正在将项目添加到 Yoink…」就自动退回微信，不需要额外点击。我们的收据卡片也应该做到这么轻：自动消失，「加入收集」作为可选按钮，不点也能存。
- **大卡片列表、编辑多选、批量删除**：直接沿用这种交互。
- **Siri 快捷指令**（如“从 Yoink 复制最新项目”）：对应到我们，可以是“把最新一批发给豆包”，放到 v2。

**改进（这些是 Yoink 作为通用暂存工具做不到、而我们能做好的）：**

| Yoink 的现状 | 微信流的做法 |
|---|---|
| ZIP 卡片的缩略图是一片空白，只有文件名「聊天记录_20261024_010035」，看不出是谁的聊天 | 卡片直接显示群名或参与者、前两条消息、条数、附件数、时间范围 |
| 点开后是一个通用 ZIP 图标「170 KB · 2 份文件 · 预览内容」，要再点一层 | 点开直接进入聊天详情 |
| 预览就是原始 TXT：整面纯文本，`[链接]` 后面跟着一长串 mp.weixin URL | 气泡式详情：`[链接]` 渲染成标题卡片，`[视频号]` 渲染成视频卡片，图片内联，表情占位保持原样 |
| 在分享面板里要往下滑很久才能找到 Yoink | 首次引导第 ② 步，教用户把微信流加到分享面板的「个人收藏」；投递时也提示“请选择 豆包” |
| 投出去时只能原样分享文件 | 按目标自动决定发 ZIP 还是 TXT 加图片，并附上场景提示词 |

**不做：**画中画浮窗、剪贴板监视、自带键盘、iCloud 同步、Handoff、电子邮件支持。这些是 Yoink 作为通用暂存工具的卖点，与“微信聊天记录中转”无关，v1 一律不做。

**录屏里发现的新内容类型**：聊天记录里除了文字、图片、表情，还会有 `[链接] <标题> <mp.weixin.qq.com URL>` 和 `[视频号] <标题> <weixin.qq.com/sph URL>`。详情页需要识别这两类，以后也可以把它们直接接到“公众号文章下载”“视频号视频下载”技能上。补测样本时要包含这两类消息。

## 8. 开发顺序

| 步骤 | 内容 | 完成标准 |
|---|---|---|
| 1 | ✅ Core 加上 iOS 平台并完成上面的改动；抽到 `shared/`（可选，未做） | ✅ Mac 186 个测试全部通过；iOS 模拟器 SDK 类型检查通过（Xcode 尚未安装 iOS 平台，还没做完整构建） |
| 2 | iOS 工程骨架：主 App、分享扩展、App Group | 在真机上，从微信分享 ZIP 后，Ready 目录里出现这一批 |
| 3 | 中转站列表和记录详情 | 卡片上的条数和附件数与 ZIP 内容一致，详情页能看图 |
| 4 | 投递：ZIP 路线（ChatGPT） | 真机上投递到 ChatGPT 后，提示词可以粘贴 |
| 5 | 投递：TXT 加图片路线（豆包、千问） | 真机上豆包和千问都能收到 TXT 和图片 |
| 6 | Obsidian 和文件夹 | Obsidian iOS 打开后能看到新笔记 |
| 7 | 场景和群绑定 | 多人群第二次导入时能自动匹配到上次的场景 |
| 8 | 合并收集、首次引导、本地化 | 三批合并后投递，内容按时间排序 |

## 9. 待实测（会影响设计）

1. 多人群聊、含视频和文件的 iOS 导出，结构是否仍和样本一致。
2. 系统分享面板一次发“TXT + 多张图 + 文本”时，豆包、千问、ChatGPT 各自收到了什么：是全收，还是只收第一个附件，文本有没有被丢掉。
3. 分享扩展里能否发本地通知，点击后能否打开主 App 并定位到这一批。
4. Claude、DeepSeek、WeSight 是否接受 ZIP。
5. ChatGPT 收到 ZIP 后，模型是否真的读到了里面的内容。
6. Obsidian iOS 的库在 iCloud 和“我的 iPhone”两种位置下，写入后能否立即看到。
7. App Store 审核：名称和描述中使用“微信”是否存在商标风险。

## 10. 不在第一版

云端账号和积分、云端执行技能、手机作为 Mac `wcb` 请求的审批端、iCloud 同步到 Mac 版、Shortcuts 和 App Intents、小组件、灵动岛。
