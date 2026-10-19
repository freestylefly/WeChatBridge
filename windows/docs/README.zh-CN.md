# 微信流 Windows 版

这是与 macOS 版本共仓库维护的 Windows 实现。

微信分享入口的处理链路为：

```text
微信 Windows → Share Target → ZIP → Inbox → FileDropList → 最小 WPF 主程序
```

也可以从本地导入聊天文件或粘贴文字，再使用主程序中的转发和归档操作，见下文。

## 本地构建

```powershell
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
& $dotnet restore windows\WeChatBridge.Windows.sln
& $dotnet build windows\WeChatBridge.Windows.sln -c Release
& $dotnet test windows\WeChatBridge.Windows.sln -c Release
```

## Windows 10 本地聊天导入

不依赖微信分享菜单，也不要求注册分享包：打开「记录」页，点击「导入聊天文件…」选择 ZIP / TXT，或把文件拖到记录页；也可以点击「粘贴聊天文字…」，确认文字并填写可选的聊天名称。

ZIP 原样保存，TXT（UTF-8 或带 BOM 的 UTF-16）及粘贴文字会转换为包含 `聊天记录.txt` 的 ZIP，继续使用现有转发和归档流程。单个文字文件及粘贴文字最多 16 MiB，归档和批次沿用 1 GiB 上限。手动导入只处理用户选择的文件；自动导入只监视下文指定的 WorkBuddy 聊天包目录。

通用设置默认开启「自动导入 WorkBuddy 聊天包」：仅监视 `%USERPROFILE%\.workbuddy\app\tmp\chat-history` 中新建的 ZIP，等文件写完后复制到 Inbox 并刷新记录；启用监听时已有文件不会重新导入。入口设置中的「WorkBuddy 聊天包导入后自动归档到微信会话」默认开启，使用当前 Obsidian Vault 写入 `31.微信会话` 子文件夹，生成 Markdown 并保存原始 ZIP 与媒体附件。可以分别关闭自动导入或自动归档。自动导入不会自动发送给 AI，也不会删除 WorkBuddy 原文件。

导入后记录显示「待发送」。点击记录中的「发给…」选择目标；复制文件、重新发送及文件夹定位也可使用。导入本身不会写剪贴板、自动转发，重启后仍保持待发送。图片、视频需要由原始 ZIP 提供，纯文字导入不会还原附件。微信备份文件不等同于聊天导出 ZIP。

Windows 10 可以运行主程序并使用此导入方式。分享包注册与微信自身的菜单枚举是两个环节；注册成功不能补齐微信依赖的新版 Windows 接口。

## 本地发布布局

```text
<install-root>\WeChatBridge.Windows.exe
<install-root>\share-target\WeChatBridge.ShareTarget.exe
```

随后运行：

```powershell
windows\scripts\new-dev-certificate.ps1
windows\scripts\register-dev.ps1 -InstallRoot <install-root>
```

注册前需要用管理员权限打开 `certlm.msc`，把脚本输出的叶证书
`WeChatBridge.Windows.Dev.cer` 导入“本地计算机\受信任的人”，并把
`WeChatBridge.Windows.Dev.Root.cer` 导入“本地计算机\受信任的根证书颁发机构”或“受信任的人”，然后重新运行注册脚本。
注册脚本会把失败的 HResult 写入 `<install-root>\registration.log`。

## SignPath Foundation 签名

正式测试包使用仓库根目录的 `.github/workflows/windows-sign.yml`，通过 GitHub Actions 手动触发。
在 GitHub 仓库中配置以下内容：

- Secret：`SIGNPATH_API_TOKEN`
- Variables：`SIGNPATH_ORGANIZATION_ID`、`SIGNPATH_PROJECT_SLUG`、`SIGNPATH_SIGNING_POLICY_SLUG`
- Variable：`SIGNPATH_ARTIFACT_CONFIGURATION_SLUG`，指向仓库中的 `windows/packaging/signpath-artifact-configuration.xml`
- Variable：`SIGNPATH_MSIX_PUBLISHER`，必须与 SignPath 证书 Subject 完全一致

SignPath 项目创建完成后，工作流会先构建和测试，再发布外部 EXE/DLL、生成 MSIX 并打成 ZIP，提交 SignPath 签名请求，最后上传签名后的 bundle。Artifact Configuration 会覆盖外部程序和 MSIX 的签名；`pack-msix.ps1` 会在签名前注入 Publisher 和版本号。

## 已知约束与微信过滤规则（2026-09-25 实测，微信 4.1.15.13）

**显示名过滤**：微信在「选择电脑中的应用」中隐藏显示名含连续子串 `微信` / `WeChat` / `Weixin` 的目标；单字、被任意字符（包括零宽字符）打断均不触发。manifest 使用 `微⁠信流`（`微` + U+2060 WORD JOINER + `信流`）绕过，渲染效果与原名一致。该字符是方案关键部分——任何字符串规范化、pretty-print、代码生成链路都可能将其剥掉，改动 manifest 工具链后必须跑 `PackagingAssetsTests.DisplayStringsAvoidContiguousBrandSubstrings` 并实机回归微信菜单。包名、Publisher、可执行路径等身份字段不含过滤词，不受此约束。签名类型不被过滤（WeixinShare 自身也是 Developer 签名）。

**入口架构**：manifest 只注册一个 <Application Id="Share.Hub">（显示名 微⁠信流）。微信分享菜单因此只出现一行；helper 落盘后写 action=hub 的 intent，主程序消费时弹自己的「发送到…」选择器——只列 settings.json 里启用的入口和自定义目标，开关即时生效，不用重打包。之前按入口各注册一个 Application 的方案已废弃：Windows 没有 macOS pluginkit -e ignore 那种保留注册、只切可见性的 API，枚举由 manifest 全权决定。

**激活方式**：helper 必须用 `Windows.ApplicationModel.AppInstance`（OS inbox API）获取激活参数。稀疏包不声明 WindowsAppRuntime 框架依赖，使用 `Microsoft.Windows.AppLifecycle.AppInstance` 会在激活时 `REGDB_E_CLASSNOTREG` 崩溃——不要重新引入 `Microsoft.WindowsAppSDK` 引用。

**稀疏包**：`uap10:AllowExternalContent=true` + `runFullTrust`，exe 与资产位于外部 install root。manifest 声明的 Logo 必须在外部目录真实存在且尺寸匹配，否则 shell 静默不枚举。

**编码**：含中文的 `AppxManifest.xml` 与 `.ps1` 必须保存为 UTF-8 with BOM，读取时显式 `-Encoding UTF8`；Windows PowerShell 5.1 默认按 ANSI(GBK) 解码，会把中文写成乱码固化进包。

**隐私边界**：只处理用户主动分享的文件；不读微信数据库、不注入微信进程、不扫描 Temp、不上传、不处理无关文件。以上是买断形态的边界，当前发布的免费档完全适用。订阅版形态会联网：它同样不上传聊天归档本身，只有用户主动选择交给云端的文件会被上传，上传前会列出具体是哪些。

**调试**：日志与批次在 `%LOCALAPPDATA%\WeChatBridge\Inbox\`（`Logs\windows.log`、`Ready`、`Failed`）；微信侧打包产物在 `%TEMP%\WeixinShare\Placeholders`。分享菜单由 `WeChatAppEx` 宿主渲染且有缓存——改注册后菜单不更新时，结束全部 `Weixin`/`WeChatAppEx` 进程再开微信。区分「注册问题」与「微信过滤」的对照方法：资源管理器右键文件 → 共享，系统分享面板可见即注册成功。

## 接入微信生态（认证与付费）

见 [WECHAT_ECOSYSTEM_INTEGRATION.zh-CN.md](WECHAT_ECOSYSTEM_INTEGRATION.zh-CN.md)：按买断版与订阅版两种形态展开的完整技术方案，含微信扫码登录、微信支付 Native 扫码购买和离线许可证校验。

该方案目前是**服务端已实现、客户端未实施**的状态（截至 2026-10）。当前发布的是买断形态的免费档，完全离线；买断形态现有的隐私承诺全部成立，不需要改动，买断版 Pro 仅在兑换许可证时联网一次。联网、认证与云端能力只属于订阅版，其联网范围见该文档 §7 与 §16。订阅版目前只规划了 Windows，macOS 尚未接入。

该方案的前提是**客户端不直连微信**：`AppID`/`AppSecret` 与商户密钥只存在于服务端，客户端只与自己的后端对话。

> ⚠️ **服务端已开始实现，但客户端有一处必须先修的 wire 契约问题**：
> 客户端 `LicenseEnvelope` 用 `DateTimeOffset` 声明 `iat`/`nbf`/`exp` 且无 converter，
> 而服务端发的是 Unix 秒整数，`LicenseStore.Load()` 会静默返回 `null`——用户看到的是「没有许可证」。
> 修法与证据见 [LICENCE-ENVELOPE-WIRE-CONTRACT.zh-CN.md](LICENCE-ENVELOPE-WIRE-CONTRACT.zh-CN.md)。
> 同目录的 `../shared/License/license-test-vectors.json`（仓库 `shared/`）是服务端产出的跨端测试向量，
> 可直接用作 C# 侧的回归夹具。
