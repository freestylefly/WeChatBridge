# Windows / macOS 功能对齐

基准：2026-10-03，上游 `freestylefly/WeChatBridge` 的 `main`，提交 `bf1f26d`。
开发代码保存在私有仓库 `xiangmingAI/WeChatBridge` 的 `feat/windows-port`。

完整源码审计结论：**尚未全部同步**。下表列出已有模块；具体遗漏、行为差异及复现结果见 [全面功能审计](MACOS_PARITY_AUDIT.zh-CN.md)。

## 上游分支核对

| 分支 | 已进入 main 的内容 |
|---|---|
| `codex/fix-onboarding-content-height` | 首次引导高度修正（#11，等价补丁） |
| `feat_ai_function_about_license-website` | 关于页、许可证、网站（#3、#4，等价补丁） |
| `feat_ai_function_readme_digital-life-snail` | README 说明（#10，等价补丁） |
| `feat_ai_wechatbridge_batch-collection_port` | 分批收集（#20，已合并祖先） |
| `feat_ai_wechatbridge_skill-tab_scene-port` | 技能库与场景编辑（压缩提交 `7acd27a` 与分支文件树一致） |

这些功能代码已随上游 main 合并进入私有开发分支。等价补丁及压缩合并不再重复引入旧分支代码。

## 功能矩阵

| 功能 | Windows 对应实现与行为 |
|---|---|
| 12 个分享操作 | 原有 Agent 加 DeepSeek Harness、文件夹、分批收集；统一选择器尊重入口启停 |
| 分批收集 | 持久收集账本；逐批群名；首尾消息参考；默认名称须显式勾选；恢复、可撤销删除、重试 |
| 统一交付 | 冻结成员后聚合原始 ZIP，用同一粘贴计划；独立场景选择及混合群行为仍需补齐 |
| 数据保留 | 原始 ZIP 不合并、不重写；未完成收集不随历史过期清理；交付中断标记可重试 |
| 文件夹与 Obsidian | 对每个会话增量合并 Markdown，保存原始 ZIP 与媒体，支持多级子文件夹 |
| 安静归档 | 默认不打开 Obsidian；托盘通知可点击打开笔记或文件；可开启保存后打开 |
| 技能库与场景 | 内置技能刷新、导入与引用、场景编辑/导入/导出、群绑定、提示词预览和 Ctrl+Alt+1–9 |
| 双语界面 | 中文 / English / 跟随系统，重启生效；部分统计、错误和自动提示词框架仍未本地化 |
| 历史和重发 | 展示单批与收集结果；改变目标时同步更新真实投递动作；所有转发共用剪贴板队列 |
| 分享与兜底 | 原子 Inbox、单实例/常驻 helper、文件剪贴板、转发失败保留原件 |

## 平台差异

- Windows 微信菜单保留一个 `微⁠信流` 入口，应用内提供 12 个操作；macOS 使用多分享扩展。保留 U+2060，避免微信过滤。
- Windows 用 Win32 / UIA 激活和粘贴、托盘通知及 MSIX；macOS 用 AX、菜单栏通知及 Sparkle。Windows 无对应的辅助功能/录屏授权页。
- 私有开发阶段不建立公开 Windows 更新源、不改变仓库可见性。自动更新和发布渠道须按 Windows 私有发布策略配置。
- 默认的磁盘子文件夹名称保持“微信流”，切换界面语言不改已有归档路径；用户内容、场景提示词及技能包保持原文。
- 只处理用户主动分享的 ZIP，不读取微信数据库、不扫描临时目录、不上传。

## 验证

运行 `dotnet build windows/WeChatBridge.Windows.sln -c Release` 和
`dotnet test windows/WeChatBridge.Windows.sln -c Release`。
测试覆盖原始文件顺序、重启恢复、默认名称、删除撤销、保留保护、JSON 契约、多级归档、统一投递及界面资源加载。
设置 `WECHATBRIDGE_UI_PREVIEW` 为输出目录，可由应用测试生成中英文界面 PNG。

仍需安装环境验证：微信原生分享菜单、目标应用真实附件接收、DeepSeek Harness 实际安装路径与焦点、系统托盘通知点击、签名安装/更新。自动测试中的投递环境使用替身，不能证明这些外部应用的实际接收行为。
