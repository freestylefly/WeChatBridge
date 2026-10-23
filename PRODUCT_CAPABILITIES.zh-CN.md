# 微信流（WeChatBridge）产品能力文档

> 基于 main 分支当前代码整理
> 平台：macOS 14 及以上
> 文档日期：2026-09-20

## 1. 产品定义

微信流是一个完全本地运行的 macOS 微信聊天记录转发与归档工具。它把微信“转发到其他应用”产生的 ZIP、TXT 和媒体文件，送到 Codex、Claude、豆包、千问办公、WorkBuddy、WeSight、Obsidian、其他应用或剪贴板。

核心价值：

1. 补上微信转发菜单里缺失的目标应用入口。
2. 保留微信原生导出的完整文件和目录结构，不读取数据库、不解密、不注入。
3. 用场景提示词把“选择目标、附加整理要求、粘贴”压缩成一个连续操作。
4. 默认不联网、不上传聊天内容。

明确不做：

- 不读取微信数据库。
- 不解密微信数据。
- 不注入微信进程。
- 不自动发送微信消息。
- 不提供快捷微信转发、快捷朋友圈转发或 HTML 聊天预览。
- 不提供云端聊天记录同步。

## 2. 核心能力总览

| 能力域 | 能力 | 用户结果 | 当前状态 |
|---|---|---|---|
| 分享入口 | 在微信转发菜单增加十二个入口 | 无需打开微信流主窗口即可发起场景 | 稳定 |
| 分批收集 | 连续接收多批原始 ZIP，最后统一选择目标和场景 | 浮条首尾参考、记录页恢复、冻结交付和失败重试 | 开发版已实现，微信宿主分享链路待实测 |
| 文件接入 | 接收微信导出的 ZIP 和文件型附件 | 原始文件和文件名进入本地批次 | 稳定 |
| 目标转发 | 发给 Codex、Claude、豆包、千问办公、WorkBuddy、WeSight 或自定义应用 | 激活目标 App 并自动粘贴 | 稳定，依赖辅助功能权限 |
| Obsidian | 生成 Markdown 笔记并保存原始归档 | 聊天记录进入本地知识库 | 稳定 |
| 剪贴板 | 文件写入剪贴板 | 用户自行在目标位置粘贴 | 稳定 |
| 记录 | 历史批次、状态、重新发送和清理 | 可以确认文件是否送达 | 稳定 |
| 场景 | 一个群保留多个候选场景，每次转发选择一个 | 只附加当前场景的提示词 | 稳定 |
| 技能 | 安装官方 SKILL.md 到支持的 Agent，手动应用导出 ZIP | 让 Agent 使用明确的能力包，而不是只靠提示词 | 技能包待提供 |
| Dock 驻留 | 默认在 Dock 中显示应用图标 | 方便从 Dock 打开设置 | 稳定 |
| 软件更新 | Sparkle 签名更新 | 安全检查和安装新版本 | 已接入，自动检查待公开 appcast 后启用 |
| 本地化 | 简体中文、English | 界面和扩展名称双语显示 | 稳定 |
| 分发 | Developer ID 签名、公证、DMG、Homebrew | 用户可直接安装 | 稳定 |

## 3. 微信分享入口

微信流通过签名后的 macOS Share Extension 出现在微信“转发到其他应用”菜单中，当前固定提供十二条入口。

### 3.1 发给 Codex

- 解析 ChatGPT/Codex 应用。
- 激活目标应用并等待窗口进入前台。
- 将场景提示词和微信导出的文件依次写入剪贴板并自动粘贴。
- 目标未安装或未取得辅助功能权限时，文件仍留在剪贴板。

### 3.2 发给 Claude

- 流程与 Codex 一致。
- Claude 未安装或未取得辅助功能权限时，文件仍留在剪贴板。

### 3.3 发给豆包

- 流程与 Codex 一致。
- 豆包读取 ZIP 时优先粘贴本机文件路径，避免再走上传选择器。

### 3.4 发给 WeSight

- 激活 WeSight 并把当前批次粘贴到输入区域。
- WeSight 未安装或未取得辅助功能权限时，文件仍留在剪贴板。

### 3.5 发给千问办公

- 激活千问办公并自动粘贴。
- 技能可安装到 `~/.qwenworkcn/skills/<skill-id>/`。

### 3.6 发给 WorkBuddy

- 激活 WorkBuddy 并自动粘贴。
- 技能可安装到 `~/.workbuddy/skills/<skill-id>/`。

### 3.7 沉淀到 Obsidian

- 将原始 ZIP 复制到知识库的附件目录。
- 从原生 TXT 中解析聊天文本和发送人。
- 按 OCR 识别到的聊天名生成 `<聊天名>的聊天.md`；识别失败时回退 TXT 参与者，内容包含场景、时间和原文链接。
- 没有配置知识库时提示用户前往“入口”设置。

### 3.8 复制到剪贴板

- 只把文件写入剪贴板。
- 不激活其他应用，也不依赖辅助功能权限。

### 3.9 发送到自定义

- 用户可以添加任意已安装的 macOS 应用。
- 应用按 bundle identifier 保存，不依赖安装路径。
- 一个目标时直接转发；多个目标时显示目标选择面板。
- 最近使用的目标优先排在前面。
- 终端类应用可配置为“只粘贴文件路径”。

## 4. 文件与批处理能力

### 4.1 输入

- 扩展声明支持最多 32 个文件型分享项。
- 主动应用于微信“合并转发”产生的 ZIP。
- 优先保留最具体、信息损失最小的文件表示。
- 支持 ZIP、其他归档、普通数据和文件 URL。
- 恢复微信分享文件的原名，避免文件名被系统统一成“Zip 归档”。

### 4.2 原子批次

- 分享先写入 `Staging`。
- 所有附件复制完成后再一次性提交到 `Ready`。
- 中断或失败不会暴露半批次。
- 扩展和应用通过 App Group 共享批次目录。

### 4.3 一次性投递

- 转发请求带有一次性 intent。
- intent 执行后被消费，应用重启不会重复粘贴。
- 请求超过 90 秒未执行时标记为过期，避免突然粘贴到用户当前窗口。

### 4.4 失败兜底

- 文件在扩展退出前已尽量写入剪贴板。
- 目标未安装、权限缺失或未成功进入前台时，记录失败原因。
- 用户可以根据记录重新发送或复制。

## 5. 记录

每条记录显示批次名称、目标入口、创建时间、文件大小和当前状态。

记录状态包括：

- `已送达`：已激活目标 App 并执行粘贴，或已写入 Obsidian。
- `已复制`：已经写入剪贴板。
- `未送达`：目标或系统操作失败。
- `未执行`：请求超过有效期或用户取消。

记录支持再次转发、复制、在 Finder 中显示、移到废纸篓和一次清空。

## 6. 场景、技能与快捷键

- 场景包含名称、关键词、附加指令、输出规范、所需技能和兼容 Agent。
- 官方场景不可直接编辑，可以复制为“我的场景”；自定义场景支持编辑、JSON 导入导出和版本检查。
- 一个群聊可以保留多个已启用候选场景；每次转发只加载其中一个提示词。
- 场景只在兼容当前目标 Agent 时生效，不兼容的场景自动跳过。
- 未绑定群聊会弹出单选场景面板，也可以不带提示词直接转发；选择结果会记到当前群。
- `⌃⌥1–9` 选择下一次转发使用的场景，选择保留 60 秒。
- 转发时先粘贴场景提示词，再粘贴文件；缺少技能时继续转发，并在提示词中要求 Agent 说明未完成或未验证部分。
- 技能页显示每个官方技能安装到各 Agent 的状态，支持直接安装、更新、冲突替换、移除和手动确认。
- Codex 安装到 `~/.codex/skills/`，千问办公安装到 `~/.qwenworkcn/skills/`，WorkBuddy 安装到 `~/.workbuddy/skills/`；豆包和 Claude 导出 ZIP 后由用户导入。

## 7. 设置与个性化

设置包括：

- 启用或关闭每条共享入口。
- 设置登录时自动启动。
- 默认在 Dock 中显示应用图标。
- 设置记录保留时间。
- 管理自定义转发应用和“只粘贴文件路径”。
- 管理场景、群聊多场景绑定、官方技能安装和手动确认。
- 配置 Obsidian 知识库目录和子文件夹。
- 查看并开启辅助功能和屏幕录制权限。
- 重新运行三步设置向导。

## 8. 权限与隐私边界

### 8.1 微信数据

- 数据只来自微信自身导出的分享文件。
- 不读取微信数据库。
- 不解密、不注入、不修改微信。
- 分享内容默认只保存在本机 App Group 容器。

### 8.2 网络

- 除软件更新外不联网。
- 十二个 Share Extension 均在沙盒中运行。
- 扩展没有网络权限。

### 8.3 系统权限

辅助功能权限用于激活目标应用、场景选择和模拟 `⌘V`。

屏幕录制权限只用于截取微信标题栏并识别群名；截图只在内存中处理，不落盘。

未授权时不会丢失文件，文件仍在剪贴板，用户可以手动粘贴。

### 8.4 沙盒边界

- Share Extension 必须沙盒运行。
- 宿主 App 不启用 App Sandbox，因为需要用 `pluginkit` 管理共享入口。
- 宿主 App 和扩展通过签名后的 App Group 通信。

## 9. 交付与更新能力

- 支持 Universal 2，即 `arm64` 和 `x86_64`。
- 使用 Developer ID Application 签名。
- App 和 DMG 支持 Apple 公证与 stapling。
- 提供 DMG、Homebrew Cask、SHA-256 校验文件。
- 支持 Sparkle 更新源。
- MIT 许可证。

## 10. 已知限制

1. 仅支持 macOS 14 及以上。
2. 微信分享菜单入口数量在构建时固定，不能为每个已安装应用动态生成。
3. 任意应用扩展通过“发送到自定义”解决。
4. 自动粘贴依赖辅助功能权限、目标 App 可激活性和窗口焦点。
5. 场景识别群名依赖屏幕录制权限和微信标题栏可读性。
6. Codex/Claude 转发进入目标应用的输入区域，不等于官方 API 建场景。
7. 不提供 Windows、iOS、Android 或云端版本。
8. 不提供多设备同步、团队共享空间或服务端搜索。

## 11. 关键实现位置

| 能力 | 主要实现 |
|---|---|
| 十二个分享入口 | `macos/Scripts/share-slots.sh` |
| Share Extension 接入 | `macos/Sources/WeChatBridgeShare/ShareViewController.swift` |
| 附件复制与命名 | `macos/Sources/WeChatBridgeShare/AttachmentImporter.swift` |
| 目标应用定义 | `macos/Sources/WeChatBridgeCore/ShareAction.swift` |
| 自定义目标 | `macos/Sources/WeChatBridgeCore/ForwardTarget.swift` |
| 激活和自动粘贴 | `macos/Sources/WeChatBridgeApp/AutoPaste.swift` |
| 转发执行与兜底 | `macos/Sources/WeChatBridgeApp/ActionRunner.swift` |
| 场景匹配与快捷键 | `macos/Sources/WeChatBridgeCore/Scene.swift`、`macos/Sources/WeChatBridgeApp/SceneShortcutController.swift` |
| 技能模型与安装 | `macos/Sources/WeChatBridgeCore/AgentID.swift`、`macos/Sources/WeChatBridgeApp/SkillLibrary.swift` |
| 技能页面 | `macos/Sources/WeChatBridgeApp/Views/Settings/SkillsPane.swift` |
| Obsidian 投递 | `macos/Sources/WeChatBridgeApp/KnowledgeDelivery.swift`、`macos/Sources/WeChatBridgeCore/ObsidianNote.swift` |
| 批次状态 | `macos/Sources/WeChatBridgeCore/BatchState.swift` |
| 共享目录 | `macos/Sources/WeChatBridgeCore/Inbox.swift` |
| App 打包和签名 | `macos/Scripts/make-app.sh` |
| 发布和公证 | `macos/Scripts/release.sh` |
