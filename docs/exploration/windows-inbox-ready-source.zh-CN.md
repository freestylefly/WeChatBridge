# Windows 端 Inbox「Ready」目录压缩包来源溯源

- 环境：Windows · 微信流 Windows（WPF / .NET 10，`windows/` 解决方案）· 本机常驻实例
- 日期：2026-10-17
- 对象：`%LOCALAPPDATA%\WeChatBridge\Inbox\Ready` 目录下的聊天记录压缩包（ZIP）
- 问题：这些 ZIP 从哪来、经哪条代码链路进入 Ready、之后由谁处理

## 1. 结论（TL;DR）

| 诉求 | 结论 |
| --- | --- |
| Ready 里的 ZIP 直接来源 | `%USERPROFILE%\.workbuddy\app\tmp\chat-history`（WorkBuddy 本机聊天记录导出目录） |
| 进入 Ready 的入口 | **WorkBuddy 自动导入**（`WorkBuddyArchiveWatcher` → `LocalChatImporter`），**不是**微信「合并转发 → 分享到其他应用」的 Share Target 通道 |
| 进入后如何处理 | `AutoArchiveWorkBuddyToObsidian` 默认开启且已配 vault 路径，导入后**立即自动投递 Obsidian** 并记 `delivered / Obsidian`；发给 AI Agent 保留手动 |

一句话：Ready 是「WorkBuddy 导出目录监视器」的落库目录，微信分享菜单那条链路当天零提交。

## 2. 完整链路（代码级）

```
微信「合并转发」导出
  └─ 被分享/导入 WorkBuddy 时落盘：
     %USERPROFILE%\.workbuddy\app\tmp\chat-history\        ← 直接来源
       ├─ <时间戳>-<hex>-聊天记录_<时间戳>.zip
       └─ wechat-chat-history-<epoch毫秒>-<hex>\           ← 同分钟的导出会话目录
  └─ WeChatBridgeArchiveWatcher（FileSystemWatcher，监视上述目录）
      ├─ MainViewModel.ImportWorkBuddyArchiveAsync(file)
      │    └─ LocalChatImporter.ImportFilesAsync(paths, [file])   （archiveToObsidian: true）
      │         ├─ InboxWriter.StageAsync  → 复制进 Staging\<GUID>\files\（附 manifest/state）
      │         └─ InboxWriter.CommitAsync → 批次目录移入 Ready\<同一GUID>\
      └─ AutoArchiveWorkBuddyToObsidian = true
           └─ KnowledgeDelivery.Deliver(...) → Obsidian vault
                └─ Record(delivered, targetName: "Obsidian")
```

关键代码位置：

| 环节 | 文件 |
| --- | --- |
| 监视目录定义 | `windows/src/WeChatBridge.Windows/Services/WorkBuddyArchiveWatcher.cs`（`DefaultDirectory`） |
| 监视器装配（主程序启动时） | `windows/src/WeChatBridge.Windows/App.xaml.cs`（`ConfigureWorkBuddyWatcher`） |
| 开关默认值 | `windows/src/WeChatBridge.Windows/AppSettings.cs`（`WorkBuddyAutoImport = true`、`AutoArchiveWorkBuddyToObsidian = true`） |
| 导入编排（含自动归档） | `windows/src/WeChatBridge.Windows/MainViewModel.LocalImport.cs` |
| 暂存 + 提交（Staging → Ready） | `windows/src/WeChatBridge.Windows.Core/LocalChatImporter.cs`、`InboxModels.cs`（`InboxWriter`） |
| 另一条入口（ Share Target，当日未使用） | `windows/src/WeChatBridge.ShareTarget/Program.cs`（`ProcessShareAsync`，提交时写「分享批次已提交」日志） |

## 3. 机制细节（与「发现即复制」的三点差异）

1. **只收启动后新生成的文件，不补收历史**。监视器构造时把目录内已存在的 ZIP 全部记入 `_known` 基线，启动前遗留的文件永远不会被自动导入（`WorkBuddyArchiveWatcher.cs` 基线逻辑）。
2. **等写完整再复制，且不做有效性假设**。文件事件触发后按 750ms 轮询等待大小/写入时间连续稳定（上限约 30 秒），再以独占方式打开确认 WorkBuddy 已松手；导入前还会打开 ZIP 校验条目可读。
3. **两段式落地，原文件保留**。先复制进 `Staging\<GUID 批次目录>\files\`（同址生成 `manifest.json` / `state.json`），再由 `CommitAsync` 把整个批次目录移入 `Ready`。chat-history 里的原 ZIP 不动（复制语义，非移动）。

补充前提：监视器长在主 WPF 应用进程内，**主程序不运行就没有自动导入**；开关为设置项 `WorkBuddyAutoImport`（默认 true）。自动导入只负责「进记录」，除 Obsidian 自动归档外不自动发送到任何 AI Agent。

## 4. 本机实证（2026-10-17）

Ready 内 4 个批次与 chat-history 源文件逐一对应（文件名 + 字节数精确匹配）：

| Ready 批次（GUID 前 8 位） | 批次创建 | ZIP 文件名 | 字节数 | 投递状态 |
| --- | --- | --- | --- | --- |
| `4290a3d0` | 08:27:53 | 20261017-082602-c38906-聊天记录_20261017_082556.zip | 2,069,976 | delivered / Obsidian |
| `8ae806bd` | 17:31:38 | 20261017-173136-d29408-聊天记录_20261017_173131.zip | 1,227 | delivered / Obsidian |
| `4f8ec601` | 17:32:49 | 20261017-173247-3d8c28-聊天记录_20261017_173244.zip | 2,165,635 | delivered / Obsidian |
| `9eaf16c0` | 18:57:33 | 20261017-185732-79b4b0-聊天记录_20261017_185729.zip | 631,986 | delivered / Obsidian |

- 后三个批次的「创建 → 投递」间隔仅 1～2 秒，与导入后立即自动归档的行为吻合。
- 当天 `windows.log` 中「分享批次已提交」记录数为 **0**，Share Target 通道未提交任何批次。
- chat-history 内与 ZIP 同分钟生成的 `wechat-chat-history-<epoch>-<hex>` 会话目录，即 WorkBuddy 接收微信合并转发记录时的导出痕迹。

## 5. 遗留疑点

当天 chat-history 共产生 7 个导出 ZIP，仅 4 个进入 Ready：

- 08:25:08、16:22:39 两个 ZIP 生成时无带监视器的实例在运行，被后续启动按基线跳过——符合第 3 节第 1 条设计。
- 16:42:47 的 ZIP（888 字节）生成时理论上处于监视窗口内却未入库，原因待查（监视器丢事件或导入静默失败均无日志）。
- 最早批次 `4290a3d0`（08:27:53）早于当天首条「WorkBuddy 自动导入已启用」日志（08:50:21），疑为当时经本地导入窗口手动导入，或监视器已存在但日志行为后补。
