<div align="center">
  <img src="shared/Screenshots/app-icon-rounded.png" width="144" alt="WeChatBridge 图标" />
  <h1>WeChatBridge（微信流）</h1>
  <p><strong>从微信转发菜单，把聊天记录送进 AI Agent 与本地知识库。</strong></p>
  <p>原生、轻量、完全本地的 macOS 微信聊天记录转发与归档工具。</p>

  <p>
    <a href="https://github.com/freestylefly/WeChatBridge/actions/workflows/ci.yml"><img src="https://img.shields.io/github/actions/workflow/status/freestylefly/WeChatBridge/ci.yml?branch=main&style=flat-square&label=build" alt="构建状态" /></a>
    <a href="#下载安装"><img src="https://img.shields.io/badge/download-蓝奏云-blue?style=flat-square" alt="下载最新版本" /></a>
    <img src="https://img.shields.io/badge/macOS-14%2B-black?style=flat-square&logo=apple" alt="macOS 14+" />
    <img src="https://img.shields.io/badge/Swift-6.0-F05138?style=flat-square&logo=swift&logoColor=white" alt="Swift 6.0" />
    <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue?style=flat-square" alt="MIT License" /></a>
  </p>

  <p>简体中文 · <a href="README_EN.md">English</a></p>
  <p><a href="https://render.qmuse.pub/p/muse/2413870555736078">官网 · render.qmuse.pub</a></p>
</div>

> [!NOTE]
> 安装包统一通过蓝奏云发布：[Windows 版](https://wwbsr.lanzout.com/b0umjur4d)与 [macOS 版](https://wwbsr.lanzout.com/b0umjur8h)的提取密码均为 `向明`，详见[下载安装](#下载安装)。

## 为什么做微信流

macOS 微信 4.1.13 起，多选聊天记录后可以“合并转发”给第三方应用。微信会生成一份包含 TXT、图片和视频的 ZIP，天然适合交给 AI Agent 处理，也适合沉淀进本地知识库。

系统的“转发到其他应用”列表只展示带有 Share Extension 的 App。微信流补齐这层入口，让一次转发直接抵达 Codex、Claude、豆包、千问办公、WorkBuddy、WeSight、DeepSeek Harness、Obsidian、剪贴板或你指定的其他应用。

```mermaid
flowchart LR
    A[微信多选聊天记录] --> B[合并转发]
    B --> C[WeChatBridge Share Extension]
    C --> D[AI Agent]
    C --> E[Obsidian]
    C --> F[剪贴板]
    C --> G[自定义应用]
```

## 核心能力

| 能力 | 使用体验 |
| --- | --- |
| 十二个原生入口 | 在微信转发菜单直接选择目标，无需打开微信流主窗口 |
| AI Agent 转发 | 激活目标 App，附加场景指令并自动粘贴聊天归档 |
| Obsidian 沉淀 | 生成 Markdown 笔记，保存原始 ZIP，并按聊天名组织内容 |
| 自定义目标 | 添加任意 macOS 应用，终端类应用可只接收文件路径 |
| 场景与技能 | 为不同群聊保留场景提示词，并管理兼容 Agent 的 `SKILL.md` |
| 本地记录 | 查看批次状态、重新发送、复制、定位文件和清理历史 |
| 失败兜底 | 目标未安装或权限不足时，文件仍会保留在剪贴板 |
| 双语界面 | 完整支持简体中文与 English |

### 内置转发入口

| 入口 | 行为 |
| --- | --- |
| 发给 Codex | 激活 ChatGPT/Codex 并粘贴聊天归档 |
| 发给 Claude | 激活 Claude 并粘贴聊天归档 |
| 发给豆包 | 激活豆包并粘贴聊天归档 |
| 发给千问办公 | 激活千问办公并粘贴聊天归档 |
| 发给 WorkBuddy | 激活 WorkBuddy 并粘贴聊天归档 |
| 发给 WeSight | 激活 WeSight 并粘贴聊天归档 |
| 发给 DeepSeek Harness | 激活 DeepSeek Harness 并粘贴聊天归档 |
| 沉淀到 Obsidian | 创建 Markdown 笔记并保存原始附件，通知可打开笔记 |
| 沉淀到文件夹 | 写入选定目录中的 Markdown 与附件，通知可在访达中定位 |
| 分批收集到微信流 | 连续分享多批原始 ZIP，收齐后一次选择目标和场景 |
| 复制到剪贴板 | 保留文件，交给用户手动粘贴 |
| 发送到自定义 | 转发到用户维护的应用列表 |

需要整理超过 100 条时，每批在微信手动选取并分享到「分批收集到微信流」。浮条显示已收批次和上一批首尾参考；收齐后点击「完成并发送」。原始图片、视频保留在 ZIP 中，未完成的收集可在记录页继续，失败可重试。微信的单次多选上限保持不变。

## 实际效果

在微信「转发到其他应用」里直接选择目标，不需要先打开微信流主窗口：

<div align="center">
  <img src="shared/Screenshots/usage-wechat-share-menu.png" width="380" alt="微信转发菜单中的微信流入口" />
</div>

<table>
  <tr>
    <th align="center">入口开关</th>
    <th align="center">场景化</th>
  </tr>
  <tr>
    <td align="center"><img src="shared/Screenshots/usage-entries.png" width="420" alt="入口设置页" /></td>
    <td align="center"><img src="shared/Screenshots/usage-scenes.png" width="420" alt="场景管理页" /></td>
  </tr>
  <tr>
    <td align="center"><sub>十二个入口随时开关，未安装的应用直接标注</sub></td>
    <td align="center"><sub>场景保存提示词与适用 Agent，转发时挑一个</sub></td>
  </tr>
  <tr>
    <th align="center">技能中心</th>
    <th align="center">沉淀到 Obsidian</th>
  </tr>
  <tr>
    <td align="center"><img src="shared/Screenshots/usage-skills.png" width="420" alt="技能中心" /></td>
    <td align="center"><img src="shared/Screenshots/usage-obsidian-attachments.png" width="420" alt="Obsidian 中的聊天记录与附件" /></td>
  </tr>
  <tr>
    <td align="center"><sub>给 Agent 装上能力包，群里的链接和视频都能读</sub></td>
    <td align="center"><sub>笔记保留原始归档，附件按微信样式渲染</sub></td>
  </tr>
</table>

完整操作步骤、权限说明和常见问题见 [使用指南](USAGE.zh-CN.md)。

## 隐私设计

微信流的处理边界保持清晰：

- 聊天内容只来自微信主动导出的文件。
- 不读取微信数据库，不解密、不注入、不修改微信进程。
- 聊天归档、场景和记录保存在本机。
- Share Extension 在 macOS 沙盒中运行且没有网络权限。
- 屏幕录制权限只用于识别微信标题栏中的聊天名，图像仅在内存中处理。
- 辅助功能权限只用于激活目标应用和执行粘贴。
- 更新包统一从蓝奏云获取，当前源码配置尚未启用自动检查。

更完整的能力边界见 [产品能力文档](PRODUCT_CAPABILITIES.zh-CN.md)。

## 系统要求

- macOS 14 Sonoma 或更高版本
- Xcode 16 或兼容 Swift 6 的 Command Line Tools
- 使用自动粘贴时，需要在系统设置中授予辅助功能权限
- 使用聊天名识别时，需要授予屏幕录制权限

## 下载安装

全部安装包统一在蓝奏云发布，请按下表选择对应平台，提取密码均为 `向明`：

| 平台 | 下载地址 | 提取密码 |
| --- | --- | --- |
| Windows | [wwbsr.lanzout.com/b0umjur4d](https://wwbsr.lanzout.com/b0umjur4d) | `向明` |
| macOS | [wwbsr.lanzout.com/b0umjur8h](https://wwbsr.lanzout.com/b0umjur8h) | `向明` |

macOS 版下载后打开 DMG，将“微信流”拖入“应用程序”，安装包同时支持 Apple Silicon 和 Intel Mac；Windows 版解压后按目录内说明运行。

## 从源码构建

```bash
git clone https://github.com/freestylefly/WeChatBridge.git
cd WeChatBridge/macos
swift test
CONFIG=release Scripts/make-app.sh
```

构建产物位于 `macos/dist/微信流.app`。安装到当前用户的“应用程序”目录并注册分享扩展：

```bash
Scripts/install-dev-build.sh
```

随后打开“微信流 → 设置 → 入口”，启用需要的分享入口。也可以前往“系统设置 → 通用 → 登录项与扩展 → 共享”管理它们。

> [!TIP]
> 本地构建会优先使用钥匙串中的 Apple Development 或 Developer ID 签名。没有可用证书时会使用 ad-hoc 签名，macOS 可能要求重新授予辅助功能权限。

## 项目结构

```text
macos/                  # macOS 端 — 完整 SwiftPM 包
├── Sources/
│   ├── WeChatBridgeApp/      # 主应用、设置、转发编排与权限管理
│   ├── WeChatBridgeCore/     # 批次、场景、归档和剪贴板核心逻辑
│   └── WeChatBridgeShare/    # macOS Share Extension
├── Resources/          # 图标、Plist、entitlements、本地化与 DMG 素材
├── Scripts/            # 构建、安装、签名和发布脚本
└── Tests/              # Swift Testing / XCTest 测试

windows/                # Windows 端 — .NET WPF 解决方案
├── src/WeChatBridge.Windows/        # 主程序（WPF）
├── src/WeChatBridge.Windows.Core/   # Inbox 契约、投递、场景、技能（无 UI 依赖）
├── src/WeChatBridge.ShareTarget/    # Windows Share Target 助手
├── packaging/SparsePackage/         # 稀疏包 MSIX（Share Target 注册）
├── scripts/            # 打包与开发注册脚本
└── tests/              # xUnit 测试

shared/                 # 两端共用的资产与契约
├── AppLogos/           # 各 Agent 的 logo（两端 UI 都用）
├── Design/             # 品牌源图与图标生成基准
├── Screenshots/        # macOS 截图（Windows 视觉移植的对照基准）
├── Skills/             # 技能目录与技能包
└── README.md           # Inbox 共享契约 + 文件级端口对照表

site/                   # Sparkle 更新源与版本说明
```

项目使用 Swift Package Manager 管理 macOS 源码和 Sparkle 依赖。`macos/Scripts/make-app.sh` 会把主程序与十二个 Share Extension 组装成完整的 `.app`。Windows 侧见 `windows/AGENTS.md` 与 `windows/docs/README.zh-CN.md`。

## 开发与验证

```bash
# macOS（在 macos/ 目录下运行）
cd macos
swift test                                    # 运行测试
swift Scripts/check-localizations.swift       # 校验中英文资源
Scripts/check-release-config.sh               # 签名 / Bundle ID / App Group 检查
Scripts/dev-preview.sh                        # 构建、安装并打开开发版本

# Windows
dotnet test windows/WeChatBridge.Windows.sln -c Release
```

## 参与贡献

欢迎提交 Issue 和 Pull Request：

1. 先查看 [Issues](https://github.com/freestylefly/WeChatBridge/issues) 中是否已有相关讨论。
2. Fork 仓库并从 `main` 创建功能分支。
3. 保持改动聚焦，为行为变化补充测试。
4. 提交前运行 `swift test` 和本地化检查。
5. 在 Pull Request 中说明动机、验证方式与界面变化。

涉及安全问题时，请通过 GitHub 的 [Security Advisories](https://github.com/freestylefly/WeChatBridge/security/advisories/new) 私下报告，避免在公开 Issue 中附上敏感细节。

## 路线图

- [x] 微信原生合并转发接入
- [x] Codex、Claude、豆包、千问办公、WorkBuddy 与 WeSight
- [x] Obsidian 本地归档
- [x] 场景提示词与技能管理
- [x] Universal 2 构建、签名与公证流程
- [x] 首个公开 DMG Release
- [ ] Homebrew Cask
- [ ] 更多社区场景和技能包

## 交流群

欢迎加入 WeChatBridge 交流群，与开发者和社区用户交流使用经验、反馈问题与分享工作流。

<div align="center">
  <img src="shared/Screenshots/wechat-community-group.png" width="280" alt="WeChatBridge 微信交流群二维码" />
  <br />
  <sub>使用微信扫码加入交流群</sub>
</div>

## 关于我们

关注项目动态、使用交流与后续更新，可通过微信搜索“向明”或“苍何”联系我们。

<table>
  <tr>
    <th align="center">向明</th>
    <th align="center">苍何</th>
  </tr>
  <tr>
    <td align="center"><img src="shared/Screenshots/wechat-xiangming.jpg" width="420" alt="公众号「向明」二维码" /></td>
    <td align="center"><img src="shared/Screenshots/wechat-canghe.jpg" width="420" alt="苍何微信二维码" /></td>
  </tr>
</table>

## 许可证

WeChatBridge 使用 [MIT License](LICENSE) 发布。

## 致谢

- [Dukou（渡口）](https://github.com/qzz0518/Dukou)：微信流基于该项目二次开发。
- [Sparkle](https://github.com/sparkle-project/Sparkle) 提供安全的 macOS 应用更新能力。
- 感谢所有参与测试、提出建议和贡献代码的朋友。

<div align="center">
  <sub>如果微信流对你有帮助，欢迎点亮 ⭐️，让更多需要本地工作流的人看到它。</sub>
</div>
