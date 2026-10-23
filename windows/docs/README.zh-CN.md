# 微信流 Windows PoC

这是与 macOS 版本共仓库维护的 Windows 迁移起点。

第一阶段只验证：

```text
微信 Windows → Share Target → ZIP → Inbox → FileDropList → 最小 WPF 主程序
```

当前不包含自动粘贴、OCR、Agent 适配、Obsidian、技能管理或正式发布。

## 本地构建

```powershell
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
& $dotnet restore windows\WeChatBridge.Windows.sln
& $dotnet build windows\WeChatBridge.Windows.sln -c Release
& $dotnet test windows\WeChatBridge.Windows.sln -c Release
```

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

**隐私边界**：只处理用户主动分享的文件；不读微信数据库、不注入微信进程、不扫描 Temp、不上传、不处理无关文件。

**调试**：日志与批次在 `%LOCALAPPDATA%\WeChatBridge\Inbox\`（`Logs\windows.log`、`Ready`、`Failed`）；微信侧打包产物在 `%TEMP%\WeixinShare\Placeholders`。分享菜单由 `WeChatAppEx` 宿主渲染且有缓存——改注册后菜单不更新时，结束全部 `Weixin`/`WeChatAppEx` 进程再开微信。区分「注册问题」与「微信过滤」的对照方法：资源管理器右键文件 → 共享，系统分享面板可见即注册成功。
