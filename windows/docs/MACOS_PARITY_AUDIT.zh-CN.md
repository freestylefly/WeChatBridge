# macOS → Windows 全面功能审计

审计日期：2026-10-03。
上游：重新 fetch 后的 `origin/main = bf1f26d85c441121b65e7502325987aa3e0680da`。
Windows：私有 `feat/windows-port`，业务代码提交 `ba464b1219bca2898dad96be2ea5b17b25169c12`。

**结论：尚未完全同步。** 主要模块已有对应实现，但仍有功能遗漏、行为差异和一个场景包契约不兼容问题。之前的 264 项测试通过，不能替代下面的跨平台核对。

本次核对以实际源码、调用链和测试为准。根目录产品能力文档有历史描述，例如旧版“向各 Agent 安装技能”；当前 macOS 实际使用应用自有技能库和路径引用，因此没有把旧文档中的已退役功能列为 Windows 遗漏。

## 核对范围与结果

“已对应”表示存在实际实现和调用链；外部应用接收效果仍见最后的实机验证范围。“部分”表示有实现但用户行为或细节不同。

| 模块 | 功能 | Windows 结果 | 核对位置 / 差异编号 |
|---|---|---|---|
| 分享 | 12 个操作及入口启停 | 已对应，统一选择器提供操作 | `ShareAction.cs`、`ShareEntryCatalog.cs`；菜单结构属平台差异 |
| 分享 | ZIP 文件接收、原名与顺序 | 已对应 | `ShareTarget/Program.cs`、`InboxModels.cs` |
| 分享 | TXT、媒体及普通文件型附件 | 缺失 | G01 |
| 分享 | Staging → Ready 原子提交 | 已对应 | `InboxModels.cs` |
| 分享 | 批次及文件大小限制 | 已对应 | `InboxLimits`、`BatchStaging.swift` |
| 分享 | 一次性 intent 与正常转发过期处理 | 已对应 | `InboxReader.cs`、`MainViewModel.EnqueueForward` |
| 分享 | 剪贴板兜底、错误上报 | 已对应 | `TryWriteClipboard`、`ShareFailure`、`ConsumeFailures` |
| 分享 | 收集期间不改动剪贴板 | 未对应 | G11 |
| 转发 | Codex、Claude、豆包、千问、WorkBuddy、WeSight、DeepSeek Harness | 已有入口及目标解析 | `WindowsForwardTarget.cs`；真实接收待验证 |
| 转发 | 自定义应用、最近使用顺序、只粘贴路径 | 已对应 | `ForwardTarget.cs`、`CustomTargetService.cs` |
| 转发 | 激活、窗口恢复、提示词后粘贴文件 | 已对应 | `DeliveryEngine.cs`、`Win32.cs` |
| 转发 | 精确定位 Electron 输入框 | 部分 | Windows 聚焦原生 Edit / Chromium 子窗口；Mac 查找 AX 文本输入元素 |
| 转发 | 多批交付共用串行剪贴板队列 | 已对应 | `MainViewModel.Collections.cs` |
| 转发 | 收集任务排队后的超时保护 | 缺失 | G04 |
| 收集 | 账本、去重、冻结及后来批次进入新组 | 已对应 | `BatchCollection.cs`、`CollectionService.cs` |
| 收集 | 重启恢复、失败重试、保留保护 | 已对应 | `RecoverInterruptedDeliveries`、`ProtectedBatchIDs` |
| 收集 | 每批群名、显式默认名称 | 已对应主体行为 | `SetChatName`；Mac 限制名称 200 字，Windows 未实现此上限 |
| 收集 | 首尾消息、条数预览 | 部分 | G12：缺少复制关键词及整组统计 |
| 收集 | 本次显式选择场景，支持混合群 | 未对应 | G03 |
| 收集 | 保存原始文件到临时选择的文件夹 | 缺失 | G02 |
| 收集 | 整组删除、移到系统回收站 | 缺失 | G05 |
| 收集 | 逐批删除和撤销 | 部分 | Windows 自有恢复目录及持久撤销；Mac 系统废纸篓 |
| 收集 | 记住上次目标与场景 | 缺失 | G12 |
| 收集 | 关闭收集面板后继续追加同一组 | 不同 | G12：Windows 关闭会暂停当前组 |
| 收集 | 交付结果和场景写回全部成员 | 部分 | G06：结果写回全部成员，场景只写回第一批 |
| 归档 | Markdown、原始 ZIP、媒体提取 | 已对应 | `KnowledgeDelivery.cs`、`ObsidianNote.cs` |
| 归档 | 同会话增量合并、跨会话分别保存 | 已对应 | 归档与集合集成测试 |
| 归档 | 多级子文件夹 | 已对应 | `DisplayName.SubfolderPath` |
| 归档 | 首次使用文件夹入口的默认路径 | 不同 | G10 |
| 归档 | 安静保存、可点击通知 | 部分 | G14：托盘气泡替代持久系统通知 |
| 场景 | 官方模板刷新、只读及复制为自定义 | 已对应 | `Scene.cs`、`SceneService.cs` |
| 场景 | 编辑、启停、排序、默认、删除 | 已对应 | `ScenesPane`、`SceneService` |
| 场景 | JSON 导入导出、版本覆盖规则、拖入 | 已有实现，但跨平台导出不兼容 | G07 |
| 场景 | 群聊多场景绑定及取消、搜索 | 已对应 | `ScenesPane`、`GroupMemoryStore` |
| 场景 | 关键词、发送人指纹、上次场景 | 已对应 | `SceneResolver`、`SceneCoordinator` |
| 场景 | 续聊水位、成功后推进 | 已对应 | `SceneCoordinator.Advance` |
| 场景 | 快捷键及 60 秒下一次选择 | 已对应 | `SceneShortcutController`、`NextForwardSceneStore` |
| 场景 | Agent 兼容过滤、技能引用及预览 | 已对应主体行为 | `RenderPrompt`、`SkillRenderContext` |
| 技能 | 应用自有库、官方同步、冲突保护 | 已对应 | `SkillStore`、`SkillService` |
| 技能 | ZIP 导入、移除、搜索、关联场景 | 已对应 | `SkillsPane`、`SkillService` |
| 技能 | 一键复制 `{{skill:id}}` | 缺失 | G13 |
| 识别 | 从微信可访问性标签读取群名 | 已对应 | Windows UIA；Mac AX |
| 识别 | 标题标签不可用时的 OCR 回退 | 缺失 | G09 |
| 历史 | 批次结果、搜索、展开、复制、重发、清理 | 已对应 | `HistoryPane`、`HistoryLabels`、`InboxReader` |
| 历史 | 收集历史管理 | 部分 | 查看 / 恢复已有；G05、G12 |
| 偏好 | 登录启动、保留期限、首次引导重跑 | 已对应 | `LaunchAtLogin`、`AppSettings`、`Onboarding` |
| 偏好 | 双语及系统语言 | 部分 | G08 |
| 关于 | 版本、项目、官网、反馈 | 已对应 | `AboutPane` |
| 关于 | 许可证及第三方声明查看页 | 缺失 | G15 |
| 更新 | 自动检查、手动检查、安装更新 | 尚未对应 | P01：私有阶段发布策略未落地 |
| 驻留 | 菜单栏 / 托盘、隐藏窗口、退出 | 已有平台对应实现 | `TrayIconService` |
| 权限 | AX / 屏幕录制授权页 | macOS 专属 | Windows 不需要对应系统授权；OCR 功能缺失另计 G09 |
| 分发 | macOS Universal / DMG / Homebrew | macOS 专属 | Windows x64 / MSIX / SignPath；安装更新待实测 |

## 需要补齐的功能及行为

### G01：分享输入仍只接受 ZIP

macOS `AttachmentImporter` 接收文件表示、文件 URL 与普通文件附件。Windows `InboxModels.cs:374` 明确拒绝 `.zip` 以外的文件，仍提示“PoC 只接收 ZIP”。
临时验证分享 `sample.txt` 得到该异常。微信主链路的 ZIP 能用，但通用分享接入能力未同步。

### G02：收集缺少“只保存原始文件”模式

macOS `CollectionCoordinator.swift:179` 让用户本次选择文件夹，并调用 `FolderDelivery.save` 保留原件与防重名复制。
Windows 收集中的 Folder 走 `KnowledgeDelivery.Deliver`，需要事先配置路径并生成 Markdown。
Windows Core 已有 `FolderDelivery.Save`，但收集 UI 没有接入。两种模式用途不同，需要分别提供。

### G03：收集缺少显式场景选择，混合群可无提示词交付

macOS 收集面板独立选择本次目标及兼容场景，并把 scene 直接交给 `ActionRunner.deliverCollection`。
Windows `CollectionWindow.Deliver_Click` 只提供目标菜单；随后调用普通 `PerformForwardCore`。
当多个批次群名不同，聚合 `ChatName` 为 null，普通场景解析可能没有绑定、指纹或默认回退，因此不弹选择器。
临时验证两批来自“甲群 / 乙群”、存在启用场景、无指纹记录时，选择器调用次数 **0**，粘贴的提示词数量 **0**。
应优先补齐独立场景选择及“不带场景”的显式选项。

### G04：收集交付队列缺少超时检查

macOS `ActionRunner.swift:150` 在队列开始执行时检查用户本次交付意图是否仍新鲜，超时保留原件并要求重新选择。
Windows `MainViewModel.Collections.cs:99` 等待前一个任务后直接执行，代码明确取消此路径的 90 秒限制。
保存收集文件本身不应过期；**用户点击交付后排队的粘贴意图**仍应有独立有效期，避免很久之后突然激活应用并粘贴。
本项为源码调用链确认，未进行 90 秒真实等待实验。

### G05：不能删除整组收集，清空历史留下收集卡片

macOS `CollectionHistoryCard` 有整组移到废纸篓；`AppModel.discardCollection` 删除成员后调用 ledger.forget。
Windows 只有逐批 Remove / Undo，没有整组删除或 Forget。`DiscardAll` 删除已交付成员后不移除账本组，卡片仍存在；未完成组受保护，也无法通过清空消除。
需补齐带确认的整组删除和账本清理。

### G06：场景上下文只写回第一批

Windows 聚合对象使用第一批的 ID，普通转发把场景写到该 ID；遍历全体成员只写动作与结果。
macOS `AppModel.recordContext(urls:)` 遍历此次交付的全部批次。
临时验证同群两批、通过快捷键指定 `audit.demo` 后：第一批 SceneID 为 `audit.demo`，第二批为 null。
历史搜索及后续查看不能稳定展示整组真实使用的场景。

### G07：Windows 默认导出的场景包无法回到当前 macOS

Windows 新增 `AgentId.DeepSeekHarness`，默认 `CompatibleAgents = AgentIds.All`。临时验证导出的场景 JSON 含 `deepSeekHarness`。
当前 macOS `AgentID.swift` 只有六个值；`Scene.swift:161` 用 `[AgentID]` 严格解码这个数组。遇到新值会解码失败。
这是契约兼容问题，需要优先修复。可以保留现有 macOS Agent 契约、将 DeepSeek 作为目标处理，或协调两端同时升级解码策略。
此项由导出实测加 Swift 解码源码确认；未在 Windows 上运行 macOS App。

### G08：本地化不完整

界面有语言选择和英文资源，但 `SkillsPane.xaml` 四处统计 `StringFormat`、`HistoryPane.xaml` 的入口格式仍写死中文；ShareTarget 的附件数量文案与若干 Core 错误也未接入 L10n。
更明显的是 `ScenePrompt` 和 `SkillRenderContext` 自动生成的“输出规范 / 技能要求 / 续聊要求”等框架文本仍为中文。
临时验证 Language=en、用户提示词为英文，生成结果仍出现“输出规范”。这不属于用户场景原文应保持不变的情况。

### G09：群名识别缺少 OCR 回退

macOS 先读取 AX 标签，失败后截图标题栏并用 Vision 识别。
Windows 只有 `WeChatUiTitleReader` 和普通窗口标题回退，没有 OCR。微信不暴露可读取标签时，自动命名可靠性低于 Mac。
补充 OCR 时仍须遵守主动分享、标题区域、内存处理的隐私边界。

### G10：首次文件夹归档无默认路径

macOS `Preferences.defaultFolderDeliveryPath` 使用 Downloads，首次即可归档。
Windows `new AppSettings().DeliveryFolderPath` 为 null，必须先进入设置选路径；临时验证已确认。
这是使用流程差异，可以设置 Windows Downloads 对应路径，或明确采用首次交付时选择路径的流程。

### G11：收集分享仍重写剪贴板

macOS `ShareViewController.swift:175` 对 collect 跳过写入文件剪贴板。
Windows `ShareTarget/Program.cs:360` 对所有动作执行 `TryWriteClipboard`，包括 collect。
用户仅收集文件也会覆盖原有剪贴板内容。应在确定为 collect 后跳过普通转发的剪贴板兜底写入。

### G12：收集操作与反馈细节未齐

macOS 有首尾消息“复制关键词”、整组消息数/大小、上一批时间范围，并持久记住上次交付目标及场景。
Windows 只有逐批首尾文本和条数；没有上述复制按钮、整组统计或选择记忆。
Mac 关闭浮条只是隐藏，下一次分享继续加入当前组；Windows `CollectionWindow.Closed` 将当前组改为 Draft，下一次开始新组。
这些是可见的体验差异，需逐项对齐或明确产品选择。

### G13：技能引用不能一键复制

macOS 技能详情显示 `{{skill:id}}` 并提供复制按钮。
Windows `SkillsPane.xaml:121` 仅用不可选择的 TextBlock 显示 ReferenceToken，没有复制按钮或对应事件。
场景编辑器中的“插入技能”已实现，技能页的独立复制操作仍缺失。

### G14：通知能力仅部分对应

macOS 用系统通知保存 note/vault 路径，可在较晚点击时重建动作。
Windows 保存 `_balloonAction` 委托，只有运行中的托盘进程可执行，且隐藏/超时会清空动作；通知点击还会先显示主窗口。
安静保存已有对应行为，持久通知、操作存活期和点击流程不同。需要确认是否接受平台方案，或使用 Windows 持久通知实现。

### G15：关于页缺少许可证与第三方声明

macOS `AboutPane.swift:96` 有查看按钮和 LicenseSheet，加载应用 LICENSE、第三方声明及实际使用组件的许可。
Windows About 只有版本及三个外链，csproj 也未把 LICENSE / 声明作为可查看资源交付。
需补齐 Windows 自身使用组件的声明页，无需复制 Windows 没有使用的 Sparkle 许可。

## 发布策略及平台差异

- **P01 软件更新**：Mac 有 Sparkle 的自动/手动检查功能；Windows 有 MSIX/签名构建流程，应用内没有检查更新逻辑，也未建立私有更新源。打包链路不等于自动更新能力。私有阶段可延期发布，但完整功能一致前仍须解决。
- **系统入口**：Mac 多扩展与 Windows 单 Hub 是平台适配；Windows 菜单显示名中的 U+2060 保留，不应改为会被微信过滤的连续品牌子串。
- **系统授权页**：Windows 不需要 macOS AX/屏幕录制授权页。OCR 回退是否实现是另一项功能问题。
- **驻留与分发**：Dock、菜单栏、DMG、Homebrew 对应 Windows 托盘、任务栏和 MSIX；不是逐项复制 macOS 系统接口。
- **目标接收**：Windows 千问目前走路径文本，Mac 千问一般走文件粘贴；需以实际 Windows 客户端支持情况验收。Windows 对 Electron 聚焦的是子窗口，未实现 Mac 那种精确 AX 文本框搜索。

## 验证记录与边界

1. 重新 fetch 上游，main 仍为 `bf1f26d`；私有业务代码审计基准为 `ba464b1`。
2. `dotnet test windows/WeChatBridge.Windows.sln -c Release --no-restore`：Core **259** + App **5** = **264** 项全部通过。
3. 隔离 Inbox、配置目录和投递替身的临时验证复现：TXT 拒绝、英文框架文案残留、第二批场景丢失、混合群无场景选择、默认路径为空，以及导出的新 Agent 枚举值。
4. 对照 macOS App/Core/Share、设置六页、浮层、通知、技能/场景及现有测试；没有把“存在同名类”直接当作功能完成，检查了 UI → 服务 → Core 的调用链。
5. 没有安装或运行 macOS App，不能在本机执行 AppKit / Vision / AX 实机链路。
6. 本次未操作用户真实微信、剪贴板、Inbox、启动项或目标应用；未重新安装 Windows 包。微信菜单、各 Agent 接收、系统通知点击、签名安装/更新仍需实机验收。

优先补齐 G07 契约兼容、G03 场景选择与 G04 超时保护，再处理输入类型、收集保存/删除、元数据及剩余界面功能。本次为审计，不修改业务实现。
