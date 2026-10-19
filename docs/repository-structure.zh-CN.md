# 仓库结构

按用途分区。面向两类读者：**使用者**想知道自己装的东西对应哪段代码；**维护者**想找一个功能落在哪个文件。

> 路径按本仓库检出位置 `D:\WeChatBridge\` 写成绝对路径。换一台机器只有根目录不同，其余层级一致。
> `bin/`、`obj/`、`.git/` 已省略——它们都是可再生的中间产物。

## 1. 仓库根

```
D:\WeChatBridge\
├── README.md / README_EN.md          对外入口：能力介绍 + 下载安装
├── USAGE.zh-CN.md / USAGE_EN.md      使用说明（功能层面，不含安装）
├── PRODUCT_CAPABILITIES.zh-CN.md    产品能力矩阵
├── SECURITY.md                       安全策略
├── LICENSE                           MIT
├── THIRD-PARTY-NOTICES.md            第三方声明
└── _watch.ps1                        本地调试脚本（未入库）
```

## 2. 双端共用

```
D:\WeChatBridge\shared\              两端共享资产与契约
├── AppLogos\                        12 个 Agent 的 logo，两端 UI 共用
├── Design\                          品牌源图与图标生成基准
├── Screenshots\                     macOS 截图，Windows 视觉移植的对照基准
├── Skills\                          技能目录与技能包 → 编译进两端产物
└── README.md                        Inbox 共享契约 + 文件级端口对照表
```

`shared/` 是唯一不允许出现平台特定代码的目录——两端都从这里取。

## 3. macOS 端

```
D:\WeChatBridge\macos\               完整 SwiftPM 包
├── Package.swift                    模块与依赖声明（Sparkle 在此）
├── Package.resolved                 锁定版本
├── mise.toml                        工具链版本
├── Sources\
│   ├── WeChatBridgeApp\              主应用、设置、转发编排、权限管理
│   ├── WeChatBridgeCore\            批次、场景、归档、剪贴板核心逻辑
│   └── WeChatBridgeShare\           Share Extension ×12
├── Resources\                       图标、Plist、entitlements、DMG 素材
├── Scripts\                         14 个脚本
│   ├── make-app.sh                  装配 .app（主程序 + 12 个扩展）
│   ├── make-dmg.sh / make-icon.swift
│   ├── release.sh / check-release-config.sh   签名、Bundle ID、App Group 校验
│   ├── install-dev-build.sh / dev-preview.sh / stop-dev-app.swift
│   └── share-slots.sh / update-tap.sh / make-signing-identity.sh
│       / check-localizations.swift
└── Tests\                           Swift Testing / XCTest
```

产物：`macos/dist/微信流.app`，走 Developer ID 签名 + 公证 + Sparkle 自动更新。

## 4. Windows 端 — 源码

```
D:\WeChatBridge\windows\
├── Directory.Build.props             版本等 MSBuild 属性
├── Directory.Packages.props          NuGet 集中版本
├── WeChatBridge.Windows.sln
├── app.manifest                     主程序 Win32 manifest
├── share-target.manifest            助手的 msix 身份（publisher 由构建脚本注入）
│
├── src\
│   ├── WeChatBridge.Windows.Core\   无 UI 依赖，被两个 exe 共用
│   │   ├── AppLayout.cs             ★ 便携模式判据 + 全部用户数据根的派生
│   │   ├── InboxModels.cs           InboxPaths / manifest 契约 / InboxLogger
│   │   ├── ForwardTarget.cs         ConfigStore + 自定义目标存储
│   │   ├── ShareAction.cs           12 个入口枚举（枚举掉 Hub 即十二个）
│   │   ├── PrefetchHint.cs          预取信号
│   │   ├── Archive\                 聊天归档
│   │   ├── Collections\             批量收集与导入进度
│   │   ├── Delivery\                投递、目标解析、入口可用性判定
│   │   ├── Identity\ Interop\
│   │   ├── Localization\ en.json    ★ L10n 表：key 即中文原文，值即英文
│   │   ├── Obsidian\                知识库写入与设置
│   │   ├── Scenes\                  场景、群记忆、下一跳
│   │   └── Skills\                  技能库与备份
│   │
│   ├── WeChatBridge.Windows\        WPF 主程序
│   │   ├── App.xaml.cs              启动、单实例、Inbox 监听、托盘
│   │   ├── MainViewModel.cs         UI 状态中枢（+ .Collections / .LocalImport）
│   │   ├── AppSettings.cs           设置与持久化
│   │   ├── LaunchAtLogin.cs         开机自启（Run 键）
│   │   ├── Panes\                   6 个页面：入口/记录/通用/场景/技能/关于
│   │   ├── Services\
│   │   │   ├── ShareRegistrationService.cs  ★ 便携版按需注册分享入口
│   │   │   ├── ShareRegistration…（在 Onboarding\，见下）
│   │   │   ├── ObsidianProtocol.cs  ★ 自注册 obsidian:// 协议
│   │   │   ├── StaleLocationReconciler.cs  ★ U 盘换位后清理悬空路径
│   │   │   ├── SkillService.cs      技能目录发现与同步
│   │   │   └── WorkBuddyArchiveWatcher.cs  WorkBuddy 导出目录监听
│   │   ├── Onboarding\
│   │   │   ├── OnboardingWindow.xaml.cs    首次运行向导
│   │   │   ├── ShareRegistrationProbe.cs   ★ 四态探测（含"探测失败"）
│   │   │   └── ShareRegistrationMarker.cs  ★ 记录注册时的 U 盘路径
│   │   ├── Components\              可复用 UI 片段
│   │   ├── Themes\                  AppTheme.xaml
│   │   ├── Localization\             WPF 侧本地化
│   │   └── Assets\                  托盘图标等
│   │
│   └── WeChatBridge.ShareTarget\     分享助手
│       ├── Program.cs               keeper/存根选举、命名管道、--registration-check
│       └── SharePipe.cs / ShareIdentityCheck.cs
│       （部分 .cs 从主项目 Link 编译，故助手进程也会碰 AppLayout）
│
└── tests\
    ├── WeChatBridge.Windows.Tests\   309 项单元测试
    │   ├── AppLayoutTests.cs         ★ Resolve 的四个用例 + 派生根归属
    │   ├── LayoutWiringTests.cs      ★ 扫描源码断言接线（见 §7）
    │   ├── TempInbox.cs              临时目录 fixture，"绝不碰真实 %LOCALAPPDATA%"
    │   └── ...（共 26 个测试文件）
    └── WeChatBridge.Windows.AppTests\  11 项 WPF / 进程级集成测试
```

## 5. Windows 端 — 分发（两条路线）

结构上刻意分开：**安装版**走 sparse MSIX + WiX，**便携版**是自包含目录。两者共用 `windows/scripts/`。

```
D:\WeChatBridge\windows\packaging\    【安装版】
├── README.md                         构建与签名证书说明
├── SparsePackage\                    只带包身份，程序在包外
│   ├── AppxManifest.xml              Identity / Share Target / AllowExternalContent
│   └── Assets\                       23 个 PNG（manifest 实际只引用 2 个文件）
├── msi\
│   ├── Package.wxs                   per-user MSI → %LOCALAPPDATA%\WeChatBridge\App
│   ├── Bundle.wxs                    Burn 引擎（可选拉取 .NET 运行时）
│   ├── CertificateTrust.wxs          隐藏 + 永久的证书信任前置包
│   ├── Installer.zh-CN.wxl           中文安装界面文案
│   ├── InstallerTheme.xml
│   ├── Register-ShareTarget.ps1      安装版注册脚本
│   ├── Unregister-ShareTarget.ps1
│   └── build-bundle.ps1
└── signpath-artifact-configuration.xml

D:\WeChatBridge\windows\portable\    【便携版 / U 盘】
├── README.md                         开发者文档：布局、构建、设计取舍
├── build-portable.ps1                构建 → artifacts\portable + Portable.zip
├── Register-ShareTarget.ps1          msi\ 那份的分支，4 处适配
├── Unregister-ShareTarget.ps1
├── FirstRun.ps1                      下载安装 Obsidian + 写协议 + 启动
├── FirstRun.bat                      纯 ASCII 外壳
└── 使用说明.txt                       随包文档，面向 U 盘用户
```

两条路线**共用**的构建脚本：

```
D:\WeChatBridge\windows\scripts\
├── build-installer.ps1               安装版：自包含发布 → 校验 → 签名 → MSI → Setup.exe
├── new-dev-certificate.ps1           自签根 + 叶证书
├── pack-msix.ps1                     sparse MSIX 打包（两条路线都调用）
├── Test-SharePackage.ps1             发布前校验（需要 mt.exe）
├── register-dev.ps1 / unregister-dev.ps1    开发期注册到本机
└── Collect-ShareDiagnostics.ps1      一键故障诊断包
```

## 6. 文档与发布

```
D:\WeChatBridge\docs\
├── repository-structure.zh-CN.md     本文
├── design\                           设计稿
│   ├── batch-collection.md / batch-collection-windows-v2.md
│   ├── windows-onboarding-v2.md
│   ├── windows-share-packaging-fix.md
│   └── assets\                       流程图与线框图
└── exploration\                      探索实录（含踩坑记录，构建便携版必读）
    ├── windows-local-verification.zh-CN.md    Windows 本地可行性验证
    ├── windows-local-artifacts-build.zh-CN.md 免 SDK 构建便携布局 + 稀疏 MSIX
    ├── windows-inbox-ready-source.zh-CN.md   Inbox 数据来源
    └── wecom-forward.zh-CN.md

D:\WeChatBridge\design\macos-ui\      macOS 视觉设计源文件

D:\WeChatBridge\windows\docs\         Windows 开发者文档 7 篇
    README.zh-CN.md                   本地构建与注册（UTF-8 BOM 约定在此）
    MACOS_PARITY.zh-CN.md / MACOS_PARITY_AUDIT.zh-CN.md
    BATCH_COLLECTION_DESIGN_AUDIT.zh-CN.md / COLLECTION_UI_REDESIGN.zh-CN.md
    SHARE_TARGET_ENUMERATION.zh-CN.md / verification-checklist.md

D:\WeChatBridge\site\                 18 篇版本说明 → GitHub Pages（pages.yml 发布）
D:\WeChatBridge\.github\workflows\    ci.yml / windows-sign.yml / pages.yml
```

## 7. 几个不看代码看不出来的约定

**唯一的数据根定义点。** 用户数据只有三个根，其余全部由它们派生：

| 根 | 定义处 | 安装版 | 便携版 |
| --- | --- | --- | --- |
| Inbox | `Core\InboxModels.cs` → `InboxPaths()` | `%LOCALAPPDATA%\WeChatBridge\Inbox` | `<便携根>\Data\Inbox` |
| Config | `Core\ForwardTarget.cs` → `ConfigStore.DefaultDirectory` | `%LOCALAPPDATA%\WeChatBridge\Config` | `<便携根>\Data\Config` |
| Skills | `Core\Skills\SkillStore.cs` | `%LOCALAPPDATA%\WeChatBridge\Skills` | `<便携根>\Data\Skills` |

三者都经由 `Core\AppLayout.cs` 派生。新增落盘点必须走它，否则便携版会把一部分状态写到 U 盘、一部分写到本机——这在拔盘之前完全看不出来。`LayoutWiringTests.NoSourceFileResolvesLocalAppDataOutsideTheAllowlist` 就是守这一条的。

**本地化的方向是单向的。** `L10n.Text("中文")` 的 key 就是中文字符串本身，英文只存在于 `Core\Localization\en.json`。加新文案时改代码里的中文 + 在 en.json 补一条，XAML 侧用 `{loc:Loc Key='中文'}`。

**中文字符串所在的 .ps1 必须是 UTF-8 with BOM。** PS 5.1 会把无 BOM 的文件按系统 ANSI 代码页读（中文 Windows 上是 GBK），静默毁掉字符串而不报错。`windows\docs\README.zh-CN.md` 写了这个要求，`LayoutWiringTests` 会守住 `FirstRun.ps1`。反过来 `.bat` 必须纯 ASCII——cmd 同样按 ANSI 代码页读。

**构建工具不必装完整 Windows SDK。** `makepri`、`makeappx`、`signtool`、`mt.exe` 从 `Microsoft.Windows.SDK.BuildTools` NuGet 包取到 `PATH` 即可。Git Bash 下还需 `MSYS_NO_PATHCONV=1` 与 `MSYS2_ARG_CONV_EXCL='*'`，否则 `signtool /fd` 会被当成路径转换。

**Git Bash 的 PATH 不含 dotnet。** 需手动 `export PATH="$PATH:/c/Program Files/dotnet"`。

## 8. 构建产物（不入库）

`.gitignore` 第 57 行忽略整个 `windows/artifacts/`。

```
D:\WeChatBridge\windows\artifacts\
├── dist\                             安装版：MSI + Setup.exe + SHA256SUMS.txt
├── release\                          本地发布布局
├── WeChatBridge-<版本>-Windows-Portable.zip    便携版，约 161 MB
└── portable\                         便携版工作区
    ├── FirstRun.bat / FirstRun.ps1 / 使用说明.txt / LICENSE.txt / SHA256SUMS.txt
    └── 微信流\                       ← 便携根
        ├── WeChatBridge.Windows.exe / portable.flag / resources.pri
        ├── WeChatBridge.ShareTarget.msix
        ├── share-target\ Assets\ Resources\ certs\
        ├── Register-ShareTarget.ps1 / Unregister-ShareTarget.ps1
        ├── Vault\.obsidian\ + 31.微信会话\
        └── Data\                     ← 运行时生成
            ├── Inbox\   Staging\ Ready\ Failed\ Logs\
            ├── Config\  settings.json / scenes / 入口开关…
            ├── Skills\
            └── SkillBackups\
```

★ 标记的是本次便携版工作新增或改动的文件。
