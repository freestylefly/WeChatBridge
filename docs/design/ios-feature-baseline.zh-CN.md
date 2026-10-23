# iOS 版功能点确认（以 macOS 版为基准）

> 基准：`feat_ai_wechatbridge_batch-collection_port` 合并 `origin/main`（`64b2d23`）后的 macOS 代码，外加工作区未提交的云端账号、积分、wcb 等改动
> 审查范围：`WeChatBridge/`（macos、windows、shared、docs、site）以及 HUB 下的 `WeChatBridge-Server`、`WeChatBridge-Web`
> 日期：2026-10-23

## 0. 结论

1. **仓库里目前没有任何 iOS 代码或服务。** 全仓库搜索 `iOS / iPhone / UIKit / iphoneos / StoreKit`，只在产品文档和路演提示词中出现过“不提供 iOS 版本”。`macos/Package.swift` 只声明了 `.macOS(.v14)`。
2. **可以直接复用的部分不少：**
   - `WeChatBridgeCore` 约 5800 行，基本只依赖 Foundation、CryptoKit、zlib。移植到 iOS 只需改 4 个文件（详见 [中转站第一版设计](ios-relay-v1.zh-CN.md) §7）。批次、收集、场景、技能模型、Obsidian 笔记生成、微信原生 ZIP 解析、云端 Commerce 契约，都能在 iOS 上直接编译。
   - `WeChatBridge-Server`（Go，含积分、买断、按量授权、执行和 OpenAPI 契约）本身与平台无关，iOS 可以作为它的新客户端。
3. **Mac 的核心交互在 iOS 上做不了：** 自动激活目标 App 并 `⌘V`、辅助功能、屏幕录制识别群名、全局快捷键、写入其他 Agent 的技能目录、本地 `wcb` CLI。iOS 版必须换一套“交付”方式（系统分享面板、URL Scheme、文件夹书签、云端执行）。
4. **入口已确认可行（2026-10-24 真机截图）：** iOS 微信转发聊天记录时，选「转到其他应用 → 选择手机中的应用」，会弹出系统分享面板，内容是 `聊天记录_20261024_001520.zip`（ZIP 归档，约 170 KB）。
   - **ZIP 结构与 Mac 版一致（2026-10-23 已验证）：** 包含 `聊天记录.txt` 和 `聊天记录内的图片、视频和文件/` 两部分。条目名是 UTF-8，但没有设置 UTF-8 标志位；压缩方式为 deflate。用现有的 `WeChatNativeArchive`、`WeChatBatchInsightsReader`、`ObsidianNote` 跑这份样本，6 条消息、发言人、时间、媒体解压、笔记标题「蜗牛的聊天」和 Markdown 都正确，不需要改代码。样本只有 1 位发言人，并且只有图片，还需要补测多人群聊，以及含视频、文件的导出。
   - **豆包 iOS 不接受 ZIP（用户实测）：** 豆包虽然出现在分享面板里，但不收压缩包。所以“原样转发 ZIP”在 iOS 上行不通，微信流必须**先解包，再转成 Agent 能接收的形式**：聊天文本（TXT/Markdown，或直接作为分享文本），加上解出来的图片和文件。这件事正好成为 iOS 版的核心价值。每个 Agent 能接收哪些类型，需要逐个实测。
   - **各 Agent 接收 ZIP 的实测结果（持续补充）：**

     | Agent（iOS） | 是否接收 ZIP | 备注 |
     |---|---|---|
     | ChatGPT | ✅ | 以文件附件形式出现在输入框上方，点开可以查看。发送后模型能否读到 ZIP 里的内容，还需要实测 |
     | WorkBuddy | ✅ | 从微信推广位进入，显示为「转发内容」卡片，见下一条 |
     | 豆包 | ❌ | 不接受压缩包；✅ 接受 TXT 和图片 |
     | 千问 | ❌ | 提示格式不支持；✅ 接受 TXT 和图片 |
     | Claude、DeepSeek、WeSight 等 | ❓ | 待测 |

     结论：问题不是“iOS 不能转发 ZIP”，而是“部分 Agent 不接受 ZIP”。对于接受 ZIP 的 Agent，微信流原样转发，再附上场景提示词即可；不接受的（如豆包），才需要先转换成文本、.md 或图片再发送。
   - **参照物：WorkBuddy iOS（用户实测）。** 从微信「转到其他应用」的推广位发给 WorkBuddy 后，输入框上方出现一张「转发内容」卡片，显示前几条消息摘要、「6 条消息 · 1 个附件」，点开是按发言人和时间排列的聊天详情（图片内联显示）。上方还有快捷动作：保存到资料库、总结并创建腾讯文档、召唤聊天总结专家。这基本就是微信流 iOS 版接收端该有的样子，快捷动作对应我们的“场景”。❓ 待确认：WorkBuddy 是自己解压了 ZIP，还是通过腾讯内部通道拿到了结构化数据？验证方法：在「文件」App 里把同一个 ZIP 分享给 WorkBuddy，看是否出现同样的卡片。
5. **产品形态（已确认）：** 做成类似 Yoink 的“暂存中转站”。收进来的每一批都先放进中转站，用户想投到哪个 Agent 就投到哪个；对方不收 ZIP 时，自动改发 `聊天记录.txt` 加图片。具体流程：微信 → 微信流（分享扩展接收并暂存）→ 加上场景提示词 → 通过系统分享面板发给豆包、千问等 Agent。差异化在于能读懂聊天记录（群名、发言人、时间、场景、合并多批、Obsidian），而不只是一个通用暂存架。

图例：✅ 可直接做或复用　🟡 需改造　❌ iOS 做不了或无意义　❓ 需实测

## 1. 功能逐项对照

### 1.1 入口与接收

| Mac 功能 | Mac 实现 | 仓库内可复用 | iOS 可行性 | 说明 |
|---|---|---|---|---|
| 微信转发菜单里的 12 个入口 | `Scripts/share-slots.sh`，12 个 Share Extension | `ShareAction` 枚举 | 🟡❓ | iOS 系统分享面板里，多个扩展会占多个图标，体验差。建议参考 Windows 的做法：只做 1 个入口，在扩展内选择 12 个动作 |
| 接收 ZIP / 文件型附件（最多 32 项，恢复原名） | `WeChatBridgeShare/AttachmentImporter.swift` | `Representation`、`DisplayName` | ✅❓ | `NSItemProvider` 在 iOS 上同样可用；关键是 iOS 微信实际会给出什么类型的数据 |
| 原子批次 Staging → Ready、App Group 共享 | `BatchStaging`、`Inbox`、`AppGroup` | 整体可复用 | ✅ | iOS 的 App Group 容器机制相同 |
| 一次性 intent、90 秒过期 | `InboxSignal`、`BatchState` | 可复用 | 🟡 | iOS 扩展不能在后台拉起主 App。需要改成“扩展内直接完成交付”或“打开主 App 继续”，90 秒过期规则需要重新定义 |
| 微信原生 ZIP / TXT 解析（发言人、时间） | `WeChatNativeArchive`、`WeChatForward`、`WeChatBatchInsights` | 可复用 | ✅❓ | 前提是 iOS 微信导出的格式和 Mac 一致 |

### 1.2 交付到目标

| Mac 功能 | Mac 实现 | iOS 可行性 | iOS 替代方案 |
|---|---|---|---|
| 发给 Codex / Claude / 豆包 / 千问办公 / WorkBuddy / WeSight / DeepSeek Harness，激活并自动粘贴 | `AutoPaste`、`ActionRunner`、`DoubaoAttachment`（AX） | ❌ 自动粘贴 | 🟡 弹出系统分享面板（`UIActivityViewController`）发给对方 App 的分享扩展；或者把提示词复制到剪贴板，再通过 URL Scheme 打开对方 App，由用户手动粘贴。每个 Agent 的 iOS App 是否存在、是否接收 ZIP，都需要逐个实测 |
| 先贴场景提示词，再贴文件（`PastePlan`） | `PastePlan` | 🟡 | 提示词作为分享文本、文件作为附件，一起交给分享面板；对方是否同时接收文本和文件，因 App 而异 |
| 沉淀到 Obsidian（复制 ZIP 到附件目录，生成 `<聊天名>的聊天.md`） | `KnowledgeDelivery`、`ObsidianNote` | ✅ | 用 `UIDocumentPicker` 选择库目录，保存安全作用域书签后写入（库在 iCloud 或“我的 iPhone”里都可以）；笔记生成逻辑直接复用 |
| 保存到文件夹 | `FolderDelivery` | ✅ | 同上，用文件夹书签 |
| 复制到剪贴板 | `FilePasteboard`（AppKit） | 🟡 | iOS 的 `UIPasteboard` 可以放文件数据，但大多数 App 不接受粘贴文件。建议降级为“复制提示词 + 分享文件” |
| 发送到自定义 App（按 bundle id，终端只贴路径） | `ForwardTarget`、`ForwardTargets` | ❌ | iOS 无法枚举已安装的 App。系统分享面板本身就是“自定义目标”；“只贴路径”在 iOS 上没有意义 |
| 目标选择面板、最近使用优先 | `TargetPickerPanel` | ✅ | 改成扩展内或主 App 内的 SwiftUI 列表 |

### 1.3 分批收集

| Mac 功能 | iOS 可行性 | 说明 |
|---|---|---|
| 连续接收多批 ZIP，统一选择目标和场景交付 | ✅ | `BatchCollection`、`BatchManifest` 可直接复用 |
| 浮条（首尾消息参考） | 🟡 | iOS 没有悬浮窗。可以改成扩展内的“已收集 N 批”提示，加主 App 内的收集页，或者用灵动岛 / Live Activity |
| 记录页恢复、冻结交付、失败重试 | ✅ | 逻辑复用，界面重写 |

### 1.4 场景

| Mac 功能 | iOS 可行性 | 说明 |
|---|---|---|
| 场景模型（关键词、附加指令、输出规范、所需技能、兼容 Agent） | ✅ | `Scene.swift` 复用 |
| 官方场景、我的场景、JSON 导入导出 | ✅ | 文件导入走 `UIDocumentPicker` |
| 一个群绑定多个候选场景 | 🟡 | Mac 的群名来自屏幕录制 OCR（`WeChatTitleReader`），iOS 不能截取其他 App 的画面。不过 Core 里已经有 `GroupFingerprint.match(senders:memories:)`，可以按发言人集合识别已知群（至少 2 个发言人），iOS 直接用它；识别不出来时让用户手动选群 |
| 按群名自动匹配场景 | 🟡 | 依赖上一条的群名来源 |
| `⌃⌥1–9` 快捷选场景（保留 60 秒） | ❌ | 改成在分享扩展里直接点选场景 |
| 未绑定群时弹出单选场景面板 | ✅ | 在分享扩展里做 |

### 1.5 技能与云端

| Mac 功能 | Mac 实现 | iOS 可行性 | 说明 |
|---|---|---|---|
| 把 SKILL.md 安装到 `~/.codex/skills`、`~/.qwenworkcn/skills`、`~/.workbuddy/skills` | `SkillLibrary`、`AgentID` | ❌ | iOS 沙盒无法写入其他 App 的目录 |
| 技能 ZIP 导出，由用户导入豆包 / Claude | `SkillArchive` | 🟡 | 可以通过分享面板导出 ZIP；对方 iOS App 能否导入技能，需要实测 |
| 内置技能：公众号文章提取 / 下载、视频号视频下载、视频信息读取 | `shared/Skills/*`（含 Python、Node 脚本） | ❌ 本地执行 | 这些技能都依赖本机 Agent 运行脚本，iOS 上跑不了，只能改为云端执行（见下一行） |
| 云端账号、积分、买断、按量授权与审批 | `CloudAccount`、`CloudPane`、`Commerce`、`SkillMonetization`；服务端 `WeChatBridge-Server` | ✅ | Commerce 契约（`contracts/openapi.json`、`examples.json`）可直接复用；iOS 上的 App 内审批 UI 需要重写。注意：iOS 上售卖数字内容/积分受 App Store 规则约束，通常要求走 IAP（见 §3） |
| `wcb` CLI（Agent 发起报价、授权、执行） | `Sources/wcb` | ❌ | iOS 没有本地 Agent 和 CLI。iOS 只能做“审批端”：Mac 上的 Agent 发起请求，手机上批准 |
| 技能安装状态、冲突替换、手动确认 | `SkillsPane` | ❌ / 🟡 | 只剩云端技能的购买和管理部分有意义 |

### 1.6 记录、设置与系统集成

| Mac 功能 | iOS 可行性 | 说明 |
|---|---|---|
| 记录（状态、重发、复制、在 Finder 中显示、废纸篓、清空、保留时长） | ✅🟡 | “在 Finder 中显示”改为“在文件 App 中显示 / 分享”，其余逻辑复用 `HistoryLabel`、`BatchState` |
| 启用 / 关闭每个分享入口（`pluginkit`） | ❌ | iOS 由用户在分享面板里自行编辑；如果改成单入口，可以改为在 App 内开关“动作” |
| 登录时启动、Dock 图标、菜单栏图标 | ❌ | 不适用 |
| 辅助功能 / 屏幕录制权限页 | ❌ | 不适用；iOS 需要的是文件夹访问和通知权限 |
| 三步首次引导 | 🟡 | 内容需重写：教用户在微信里分享到微信流、选择 Obsidian 文件夹 |
| 交付通知 / Toast | ✅ | `UNUserNotificationCenter` |
| 中英文本地化 | ✅ | `Localizable.strings` 大部分可复用，但 Mac 专属文案需要清理 |
| 诊断日志、导入诊断 | ✅ | `DiagnosticsLog`、`ImportDiagnostics` 复用 |

### 1.7 分发与更新

| Mac 功能 | iOS 可行性 | 说明 |
|---|---|---|
| Developer ID 签名、公证、DMG、Homebrew、Universal 2 | ❌ | 改为 App Store / TestFlight |
| Sparkle 更新、蓝奏云检查更新 | ❌ | App Store 自动更新；蓝奏云的检查更新在 iOS 上不应出现（审核风险） |
| 官网 `WeChatBridge-Web`、`site/appcast.xml` | 🟡 | 官网写的是“macOS 14+”，需要补充 iOS 下载入口 |

## 2. 仓库现有资产对 iOS 的价值

| 资产 | 位置 | 对 iOS 的价值 |
|---|---|---|
| 核心库 | `macos/Sources/WeChatBridgeCore`（约 5800 行） | 高。建议在 `Package.swift` 里加上 `.iOS(.v17)`，把 `FilePasteboard` 用 `#if canImport(AppKit)` 隔离，再让 iOS App 依赖它。同时可以考虑把它从 `macos/` 挪到 `shared/` |
| 核心测试 | `macos/Tests/WeChatBridgeCoreTests` | 高。iOS 模拟器上跑同一套测试，就能证明复用没有出问题 |
| 技能目录、场景、Logo、截图 | `shared/` | 中。目录元数据可复用，脚本执行不行 |
| 云端服务 | `WeChatBridge-Server`（Go + PostgreSQL） | 高。但它目前只监听 loopback，是本机开发形态，还没有可供手机访问的 HTTPS 部署；而且这个目录的 `main` 分支还没有任何提交 |
| Windows 版的“单入口 + 应用内 12 个动作”设计 | `windows/docs/MACOS_PARITY.zh-CN.md` | 中。与 iOS 分享面板的限制非常接近，可以直接借鉴交互 |
| 产品能力文档 | `PRODUCT_CAPABILITIES.zh-CN.md` | 注意：文档日期是 2026-09-20，还没写入 DeepSeek Harness、文件夹、分批收集、云端积分这些新功能，并且明确写了“不提供 iOS”，需要同步更新 |

## 3. 需要你确认的功能点

**先做真机实测，再定范围：**

1. ✅ iOS 微信可以通过「选择手机中的应用」把聊天记录以 ZIP 形式分享出去（`聊天记录_<日期>_<时间>.zip`）。内部结构与 Mac 版相同，现有解析器可以直接使用。❓ 补测：多人群聊，以及含视频、文件的导出。
2. ❌ 豆包 iOS 不接受 ZIP。❓ 其余 Agent（千问、Claude、ChatGPT、WorkBuddy 等）分别接受：纯文本、.txt/.md 文件、图片、多个附件中的哪些？
3. ❓ Codex（ChatGPT）、Claude、千问、WorkBuddy、WeSight、DeepSeek 的 iOS App 是否存在？是否有分享扩展？能否同时接收文本提示词和附件？
4. ❓ Obsidian iOS 的库放在 iCloud 和放在“我的 iPhone”里时，写入后 Obsidian 能否立即识别。

**请勾选第一版要做的范围：**

- [ ] 单个分享入口，扩展内选择动作（建议），还是多个分享扩展
- [ ] 交付到 Agent：通过系统分享面板转交
- [ ] 沉淀到 Obsidian
- [ ] 保存到文件夹
- [ ] 分批收集
- [ ] 场景（含群绑定；群名来源选：TXT 推断 / 手动选择）
- [ ] 记录与重发
- [ ] 云端账号与积分（如果包含：积分购买是否走 IAP？）
- [ ] 手机作为 Mac `wcb` 请求的审批端
- [ ] 云端执行技能（文章下载、视频下载等，需要服务端实现真实能力）

**需要定的方向：**

- 核心库是否抽到 `shared/` 作为 Mac、iOS 共用的 Swift Package
- 最低支持的 iOS 版本（建议 17，可以用 Observation 和较新的 SwiftUI）
- App Store 审核：名称和描述里使用“微信”、导出聊天记录场景是否存在审核与商标风险，需要提前评估
