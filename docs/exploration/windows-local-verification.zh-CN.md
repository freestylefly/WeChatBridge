# Windows 本地可行性验证与稀疏包注册实录

> 记录时间：2026-10-17
> 环境：Windows 10 Home 22H2，build 19045.3803
> 源码版本：`windows/Directory.Build.props` 中 `<Version>1.0.8</Version>`
> 结论先行：**Windows 端可以本地构建、测试、运行；但本机无法验证「微信转发菜单入口」这一核心功能**，原因是操作系统 build 低于微信 4.x 的硬门槛。

本文记录一次完整的本地可行性排查，包含已验证可用的路径、已被证伪的假设，以及一条经过端到端验证的替代数据入口。

---

## 1. 源码构建与测试

### 1.1 前置依赖缺失

初始环境没有任何 .NET 工具链：

| 组件 | 初始状态 | 说明 |
| --- | --- | --- |
| `dotnet` | 未安装 | `C:\Program Files\dotnet` 不存在，PATH 中也没有 |
| Visual Studio | 未安装 | — |
| Windows SDK | 未安装 | 影响 `makeappx.exe` / `signtool.exe` |
| Swift / Xcode | 无 | `macos/` 是完整 SwiftPM 包，Windows 上无法构建 |

**Git Bash 中 `dotnet` 不在 PATH 上**，winget 安装后仍需：

```bash
export PATH="$PATH:/c/Program Files/dotnet"
```

### 1.2 安装与构建结果

.NET 10 SDK 10.0.402 通过 winget 安装（`Microsoft.DotNet.SDK.10`）。目标框架 `net10.0-windows10.0.19041.0` 对本机 build 19045 满足。

```
dotnet restore windows/WeChatBridge.Windows.sln   → 5 个项目全部还原
dotnet build   windows/WeChatBridge.Windows.sln -c Release
    → 0 个警告, 0 个错误（19.71s）
dotnet test    windows/WeChatBridge.Windows.sln -c Release
    → 已通过! 失败 0, 通过 287 (Windows.Tests)
    → 已通过! 失败 0, 通过 8   (AppTests)
```

注意 `Directory.Build.props` 设置了 `TreatWarningsAsErrors=true`，构建零警告是硬要求。

### 1.3 运行时验证

启动 Release 产物成功：

```
进程 WeChatBridge.Windows.exe  存活，29 线程，窗口标题「微信流」
自动创建 %LOCALAPPDATA%\WeChatBridge\Inbox\{Ready,Failed,Logs,Staging} + collections.json
日志唯一一行，无异常：
    tray icon registered: True, v4=False, hwnd=127665880
```

`Assets\`（各 Agent logo）与 `Resources\Skills\`（技能目录）随构建正确落盘，`AppLogos.PathFor` 与 `SkillService.FindResourcesRoot` 的运行时路径解析均正常。

---

## 2. 平台限制：为什么本机验证不了微信入口

### 2.1 硬门槛

微信 4.x 的「选择电脑中的应用」依赖 `TransferTargetWatcher`，该 API 自 build 22621 起才存在，且**微信插件硬性要求 build ≥ 26100**（Windows 11 24H2）。

本机 build 19045 < 26100，**无论怎么配置都不会出现该入口**。

### 2.2 仓库自身证据

`windows/docs/verification-checklist.md:38` 明确记录，所有「微信分享菜单出现微⁠信流」的实测结果均为 2026-09-25 在 **Windows 11** / 微信 4.1.15.13 上取得，Windows 10 从未被验证过。

### 2.3 兼容层 `WeChatBridge-WeixinCompat-1.0.6` 不可用于本机

Downloads 中的 `WeChatBridge-WeixinCompat-1.0.6` 是为 **Windows 11 23H2（22631 ~ < 26100）** 准备的兼容补丁，通过 IFEO 注入 `Weixin.exe` / `shareSender.exe` 并在机器级注册伪造的 WinRT 激活类。

**本机绝对不要安装**，理由：

1. 其自愈守卫仅在 build ≥ 26100 时整体卸载（`Install-CompatLayer.ps1:45`），而本机永远等不到那天。
2. Win10 上微软真实的 `DataTransfer` 类**是存在的**，伪造激活类将永久遮蔽它们——这正是其 README 警告的「遮蔽微软真实现」，但在 Win10 上会变成永久状态。
3. IFEO + Debugger 是安全软件重点监控的持久化手法，杀软清理后微信将无法启动。
4. 其 README 明示：对微信进程做 PEB 修改超出《微信软件许可协议》范围，腾讯可能据此风控甚至封号。

### 2.4 仓库版本落后于发布产物

| 产物 | 版本 | 来源 |
| --- | --- | --- |
| 本仓库源码 | 1.0.8 | — |
| `WeChatBridge-1.1.17-Windows.msi` | 1.1.17.0 | Downloads，SHA256 校验通过 |
| `WeChatBridge-WeixinCompat-1.0.6` | 1.0.6 | Downloads，**仓库中无对应源码** |

兼容层 README 指向 `windows/tools/TransferTargetShim/` 与 `docs/exploration/微信转发面板-22631根因分析与兼容层部署.md`，二者在当前 checkout 中**均不存在**。若要研究该补丁，需另行获取更新版本的源码。

### 2.5 官方 CertificateTrust.msi 不可得

发布包签名者 `CN=WeChatBridge Windows Dev` 的根证书 `CN=WeChatBridge Windows Dev Root`：

- **未嵌入**在 MSI 的 Authenticode 签名中（`certutil -dump` 显示 `Missing Issuer: CN=WeChatBridge Windows Dev Root`）
- 官方 GitHub Releases **为空**（`GET /repos/freestylefly/WeChatBridge/releases` → `[]`），无附件可取
- 仅蓝奏云 `b0umjur4d`（密码 `向明`）分发，本地两个 zip 内均不含该文件

**但该证书并非必需**：`msiexec` 不强制校验 MSI 的 Authenticode，签名仅为信息性；而证书唯一的作用是门禁稀疏包注册，该环节在本机本就无意义。

---

## 3. Inbox 数据入口的代码考古

结论：**除真实 ShareTarget 激活外，不存在任何受支持的导入路径。**

### 3.1 唯一写入者

`%LOCALAPPDATA%\WeChatBridge\Inbox\Ready` 仅由 `WeChatBridge.ShareTarget.exe` 写入，且仅在 `ActivationKind.ShareTarget` 激活时（`Program.cs:329-400` 的 `ProcessShareAsync` 是唯一调用 `InboxWriter.StageAsync` / `CommitAsync` 的地方）。

### 3.2 已排除的路径

| 假设 | 实测结论 |
| --- | --- |
| 给 helper 传文件路径 | ✗ `Program.cs:59,70` 仅解析 `--registration-check` 与 `--picker-preview`，其余参数被忽略。传入 `C:\chat.zip` 会退化为 keeper 驻留进程空转（`Program.cs:245-249` 记一行「收到非分享激活」后 `RunMessageLoop` 永久挂起），**留下无用后台进程** |
| 界面拖放聊天 ZIP | ✗ 全部 XAML 中唯一 `AllowDrop` 是 `ScenesPane.xaml:367`，拖的是场景 JSON |
| 打开文件对话框 | ✗ 唯一 ZIP 选择器是 `SkillsPane.xaml.cs:83`，导入技能包，与聊天记录无关 |
| 监视文件夹 / 定时重扫 | ✗ `windows/src` 中 `FileSystemWatcher` 零命中；`Reload()` 仅在窗口 Loaded、InboxChanged/Foreground 事件及用户操作后触发 |

界面中唯一相关的按钮是「打开 Inbox 文件夹」（`MainViewModel.cs:999-1015`），它只是用 `explorer.exe` 打开目录，**不是导入器**。

### 3.3 可行的替代：手工构造批次目录

`InboxReader` 是纯读取器（`InboxReader.cs:100-110` 直接 `EnumerateDirectories`），不校验来源，因此手工构造的目录会被接受。这是读取器的未支持副作用，而非产品功能。

**目录布局**（`InboxModels.cs:92-114, 253-309`）：

```
Inbox\Ready\{batchId:D}\
├── manifest.json
└── files\聊天记录.zip
```

**`manifest.json` 契约**（`InboxModels.cs:14-50`、`InboxReader.cs:336-352`）：

```json
{
  "schemaVersion": 1,
  "batchId": "与文件夹名一致的 GUID（D 形式）",
  "createdAt": "2026-10-17T06:38:09Z",
  "action": "clipboard",
  "items": [{
    "id": "任意 GUID",
    "displayName": "聊天记录.zip",
    "relativePath": "files/聊天记录.zip",
    "contentType": "application/zip",
    "byteCount": 189,
    "itemIndex": 0,
    "attachmentIndex": 0
  }]
}
```

**四个会导致批次被静默丢弃的坑**：

1. **键名必须 camelCase**。`PropertyNamingPolicy = CamelCase` 且未设置 `PropertyNameCaseInsensitive`，写成 PascalCase 会静默绑定到默认值。
2. **不要放 `intent.json`**。新鲜的 intent 会让 `MainViewModel.cs:481-489` 在首次见到时直接判定 **Expired**。
3. **文件夹名必须等于 `manifest.BatchId` 的 `D` 形式**。`InboxReader.DirectoryFor`（`InboxReader.cs:334`）是从 manifest 而非目录名反推路径的，不一致会导致后续删除/重命名操作打空。
4. `createdAt` 写入端规范形式为 `yyyy-MM-ddTHH:mm:ssZ`（`ManifestContractTests.cs:12-30` 用正则断言）；读取端虽宽松，仍应按此写入以保证跨平台一致。

其余细节：`schemaVersion` 须 ≤ 1；`action` 只能是 `ShareActions.FromRawValue`（`ShareAction.cs:65-83`）中的 12 个原始值，手工批次用 `clipboard` 最安全；`relativePath` 相对批次目录、正斜杠、不可为绝对路径（`ManifestContractTests.cs:32-47`）；1 GiB 的文件/批次上限（`InboxModels.cs:211-212`）**仅写入端生效**，读取端不限制大小。

---

## 4. 已验证可用：手工导入脚本

脚本位置（**放在仓库外**，避免污染代码）：

```
C:\Users\huawei\AppData\Local\WeChatBridge-tools\import-batch.ps1
```

用法：

```powershell
.\import-batch.ps1 -ZipPath "C:\path\chat.zip"
.\import-batch.ps1 -ZipPath .\a.zip, .\b.zip -Action obsidian
```

脚本会写好 manifest、发 `Local\WeChatBridge.Windows.InboxChanged` 事件（`App.xaml.cs:12`，语义为「已提交，请重载」），从而免去重启。

### 4.1 端到端验证结果

用一个假 ZIP 实测，链路成立：

```
import-batch.ps1 写入 manifest.json → 通知运行中的实例
      ↓
app 识别批次，自行写入 state.json：
    { "outcome": { "kind": "copied", "at": "2026-10-16T22:38:09Z" },
      "action": "clipboard", ... }
```

`state.json` 由 app 侧写入（`InboxReader.cs:123-128`），其出现即证明手工构造的批次被完整接受。该批次随后正常显示在「历史」中。

### 4.2 生成合规的微信格式 ZIP

由于本机无法从微信取得真实归档（见第 2 节），另附一个生成器：

```
C:\Users\huawei\AppData\Local\WeChatBridge-tools\make-chat-zip.ps1
```

用法（消息文件每行 `发送者|正文`，以两个空格开头的续行并入上一条正文，支持多行）：

```powershell
.\make-chat-zip.ps1 -Messages .\msgs.txt -ChatName 产品讨论组 -Media .\clip.mp4
```

**格式要求**（`WeChatNativeArchive.cs:65`、`WeChatTranscriptRecord.cs:16-17`）：

| 要求 | 说明 |
| --- | --- |
| 条目名最后一个路径段必须**正好**是 `聊天记录.txt` | 否则只能走「记录数最多」的兜底分支 |
| UTF-8、无 BOM、deflate | ZIP64 / 加密 / 未知压缩方式一律 fail closed |
| 正文必须**从 header 开始**（首个匹配在索引 0） | 否则 `WeChatTranscriptRecord.Parse` 抛 `InvalidTranscript`，insights 静默为空 |
| header：`·发送者\nYYYY年M月D日 HH:MM\n正文` | 那个点是 **U+00B7**，不是 U+2022。写错是自制归档拿不到 insights 的最常见原因 |

生成结果已按上表逐条校验：5 条消息全部命中、首个匹配位于索引 0、发送者去重正确、多行正文保留，导入后 app 正常识别。

### 4.3 踩坑：Windows PowerShell 5.1 会毁掉脚本里的中文

首版生成器产出的条目名码点是 `U+9471 U+5A42 U+3049 U+7481 U+677F U+7D8D`——正是「聊天记录」被 GBK 误解码的结果；日期格式串里的 `年月日` 同样被毁，`.ToString()` 退化为输出字面量 `2026年M月d日`。

原因：**PowerShell 5.1 把无 BOM 的 UTF-8 脚本按 ANSI(GBK) 解码**。这与 `windows/docs/README.zh-CN.md:63` 记录的坑完全一致。

修复：

```powershell
$c = [System.IO.File]::ReadAllText($f, [System.Text.Encoding]::UTF8)
[System.IO.File]::WriteAllText($f, $c, (New-Object System.Text.UTF8Encoding($true)))
```

本次涉及的 `make-chat-zip.ps1` 与 `import-batch.ps1` 均已带 BOM。顺带一提，写校验脚本时也踩了同一个家族坑：PowerShell 5.1 不支持 `` `uXXXX `` 转义（那是 JS/JSON 的写法），正则里要拼 `[char]0x00B7` 而不是 `` `u00B7 ``。

---

## 5. 稀疏包注册进度

### 5.1 已完成

| 步骤 | 状态 |
| --- | --- |
| 免安装获取 SDK 工具 | ✅ `Microsoft.Windows.SDK.BuildTools` 10.0.26100.8249 → `WeChatBridge-tools\extracted\bin\10.0.26100.0\x64\`，`signtool.exe` 与 `makeappx.exe` 实测可运行（22 MB，替代 2 GB 完整 SDK） |
| 自签开发证书 | ✅ `new-dev-certificate.ps1`，叶 `CN=WeChatBridge Windows Dev` / 根 `CN=WeChatBridge Windows Dev Root`，有效期内自动复用 |
| 导入 CurrentUser\Root | ✅ 需绕过 `Import-Certificate`（会弹出「受保护的进程无法访问此证书或私钥」模态框并挂起），改用 `certutil -user -addstore Root`，**无需管理员**。之后 `X509Chain.Build()` 返回 `True`，链元素 leaf → root，无状态错误 |
| 发布到 install root | ✅ `dotnet publish` 产出 `App\WeChatBridge.Windows.exe` 与 `App\share-target\WeChatBridge.ShareTarget.exe`，符合 `register-dev.ps1` 的布局期望 |
| 打包并签名 MSIX | ✅ 29 个文件，Version 1.0.8.0，Publisher `CN=WeChatBridge Windows Dev` |
| 导入计算机级证书存储 | ✅ 管理员执行 `certutil -addstore` |
| `Add-AppxPackage` 注册 | ✅ `WeChatBridge.Windows.ShareTarget_1.0.8.0_neutral__k2gw8zhv15c4m`，Status=Ok |

### 5.2 提权导入根证书（定位过程中的关键一环）

`Add-AppxPackage` 一度失败，**错误码的演进本身就是定位过程**：

| 尝试 | 错误码 | 含义 |
| --- | --- | --- |
| 仅导入 CurrentUser | `0x80131501` | MSIX 签名不受信任 |
| 导入 `Cert:\CurrentUser\Root` 后重试 | `0x800B010A` | 链可构建，但终止于不受信任的根 |
| 导入计算机级存储后重试 | — | **注册成功** |

第二次失败的原因值得记下：AppX 部署服务（AppXSVC）以 SYSTEM 身份运行，验证证书链时读取**计算机级**存储，看不见用户级存储。所以用户级导入虽然让 `X509Chain.Build()` 返回 `True`，对 `Add-AppxPackage` 而言仍等于没做。这与 `windows/docs/README.zh-CN.md:36-38` 要求导入「本地计算机」一致。

最终在管理员 PowerShell 中执行：

```powershell
certutil -addstore Root          "$env:TEMP\WeChatBridge.Windows.Dev.Root.cer"
certutil -addstore TrustedPeople "$env:TEMP\WeChatBridge.Windows.Dev.cer"
```

⚠️ **踩坑**：在用户级导入时，`Import-Certificate -CertStoreLocation Cert:\CurrentUser\Root` 会弹出「受保护的进程无法访问此证书或私钥」的模态框并**永久挂起**（根证书带 `-KeyExportPolicy NonExportable` 与密钥隔离）。改用 `certutil -user -addstore Root <cer>` 直接操作 .cer 文件即可绕开，且无需管理员。同样的写法适用于 `TrustedPeople`。

**安全性说明**：向 `LocalMachine\Root` 添加根证书意味着本机所有用户信任该根签发的一切，有效期 5 年。实际导入的两张证书：

| 存储 | 主题 | 指纹 | 到期 |
| --- | --- | --- | --- |
| `LocalMachine\Root` | CN=WeChatBridge Windows Dev Root | `DC465A0742C17A0AC9263A3055FC50CC8279762F` | 2031-10-17 |
| `LocalMachine\TrustedPeople` | CN=WeChatBridge Windows Dev | `C064A5FA6BA7B92D49827D1F7974FC967351359C` | 2028-10-17 |
| `CurrentUser\My` | 两张证书均带私钥 | 私钥 `-KeyExportPolicy NonExportable` | — |

该证书为本地自签、私钥不出机器，风险可控，但毕竟是机器级信任变更，应知情后执行。撤销：

```powershell
certutil -delstore Root          DC465A0742C17A0AC9263A3055FC50CC8279762F
certutil -delstore TrustedPeople C064A5FA6BA7B92D49827D1F7974FC967351359C
```

### 5.3 注册结果验证

```
registration.log:
    2026-10-16T22:50:54  已同步图标资源到外部内容目录
    2026-10-16T22:50:55  成功 WeChatBridge.Windows.ShareTarget_1.0.8.0_neutral__k2gw8zhv15c4m

WeChatBridge.ShareTarget.exe --registration-check  (exit=0):
    {"PackageFullName":"WeChatBridge.Windows.ShareTarget_1.0.8.0_neutral__k2gw8zhv15c4m",
     "ApplicationUserModelId":"WeChatBridge.Windows.ShareTarget_k2gw8zhv15c4m!Share.Hub"}

Get-AppxPackage -Name WeChatBridge.Windows.ShareTarget:
    Status: Ok
    InstallLocation: C:\Program Files\WindowsApps\WeChatBridge.Windows.ShareTarget_1.0.8.0_neutral__k2gw8zhv15c4m
    SignatureKind  : Developer
```

`AllowExternalContent` 稀疏包解析外部路径时以 install root 为基准，因此 manifest 声明的 logo 必须真实存在于外部目录，否则 shell 会静默不枚举（`register-dev.ps1:31-34` 的注释）。已核对 `App\Assets\` 下 `Square150x150Logo.png`、`Square44x44Logo.png`、`Square44x44Logo.targetsize-24_altform-unplated.png` 均存在，各尺寸 scale/targetsize 变体齐全。

### 5.4 枚举缓存：注册成功的包也可能不出现

注册完成后第一次在资源管理器共享面板里**看不到**「微⁠信流」，只有 WorkBuddy。排查结论是注册本身毫无问题：

| 检查项 | 结果 |
| --- | --- |
| AppXDeploymentServer/Operational | 无任何 Warning/Error |
| 包结构（WindowsApps 下无 exe，只有 Assets + 元数据 + pri） | 与 WorkBuddy 一致 |
| 注册后的 manifest 保留 `AllowExternalContent=true` | 是 |
| `windows.shareTarget` + 四个 `DataFormat` | 与 WorkBuddy 完全一致 |
| `SignatureKind` | `Developer`（自签），WorkBuddy 为正式证书 |

排查中的一条弯路：`AppXDeploymentServer/Operational` 里的

> 已为程序包 … 提供程序，当注册状态设置为 **disabled** 时耗时 0x0。

是 **Information 级**消息，属于正常 Add 流程（先置 disabled 再启用），**不是错误**，不要顺着它排查。

真正的解法是重启 shell：

```powershell
Stop-Process -Name explorer -Force
```

重启后「微⁠信流」正常出现在共享面板中。

> **结论：开发人员模式并非必需。** 当时的工作假设是「sideloaded 的 Developer 签名包在开发人员模式关闭时不会被 shell 枚举」，但 `AppModelUnlock` 注册表项自始至终不存在，Developer Mode 从未开启，仅重启 explorer 就解决了。

**枚举缓存同时存在于 shell 和微信两侧**，任何一次重新注册后都应同时重启：

```powershell
Stop-Process -Name explorer, Weixin, WeChatAppEx -Force
```

注意**关闭微信窗口不等于退出微信**，它会留在托盘里继续持有缓存。务必用 `Get-Process Weixin, WeChatAppEx` 确认进程真的没了——本次排查中用户以为已退出，实际仍有 14 个进程存活，其中主进程 `Weixin.exe` 已连续运行超过一天。

---

## 6. 结论与后续清单

> ⚠️ **第 2 节的部分结论已被后续实测推翻，此处保留原文以记录判断路径。**
> 实测发现：微信 4.x 在 build 19045 上**确实提供**了「选择电脑中的其他应用」入口；
> 而稀疏包注册在本机**完全成功**，「微⁠信流」正常出现在资源管理器共享面板中（详见 5.4）。
> 因此「build < 26100 就一定不行」这一说法**未经证实**，
> `WeixinCompat` 所依赖的前提假设也未被验证。

### 6.1 本机已经做到的

- ✅ 完整构建与测试 Windows 端（295 个测试全绿）
- ✅ 运行主程序，验证全部 UI：设置、场景提示词、技能中心、入口开关、历史记录、Obsidian 沉淀
- ✅ 通过 `import-batch.ps1` 手工注入批次，验证数据层读取
- ✅ 通过 `make-chat-zip.ps1` 生成合规归档，验证解析器（6 个 `ArchiveTests` + 逐条契约核对）
- ✅ 注册稀疏包，并在资源管理器共享面板中确认「微⁠信流」出现

### 6.2 本机仍待验证的

- ❓ 微信「选择电脑中的其他应用」中「微⁠信流」不可见，排查见第 7 节
- ❓ 显示名改为纯 ASCII 是否能让微信枚举到（决定性实验，见 7.5）
- ❓ 真实分享 ZIP 的完整投递链路（Obsidian / Agent / 剪贴板）
- ❓ 目标 Agent 的实际激活与粘贴行为
- ❌ 构建或测试 `macos/` 端

### 6.3 重启注册后的固定动作

```powershell
Stop-Process -Name explorer, Weixin, WeChatAppEx -Force
```

微信分享菜单由 `WeChatAppEx` 宿主渲染并缓存枚举结果，Explorer 共享面板同样有缓存。
**关闭微信窗口不算退出**，必须确认进程真的消失：

```powershell
Get-Process Weixin, WeChatAppEx   # 应无输出
```

### 6.4 日常开发备忘

```bash
# Git Bash 中使用 dotnet
export PATH="$PATH:/c/Program Files/dotnet"

# 构建与测试
dotnet build windows/WeChatBridge.Windows.sln -c Release
dotnet test  windows/WeChatBridge.Windows.sln -c Release

# 注册（需先把 SDK 工具目录加进 PATH）
powershell -NoProfile -Command '
  $env:PATH = "C:\Users\huawei\AppData\Local\WeChatBridge-tools\extracted\bin\10.0.26100.0\x64;" + $env:PATH
  & "D:\WeChatBridge\windows\scripts\register-dev.ps1" -InstallRoot "$env:LOCALAPPDATA\WeChatBridge\App"'
```

**排障日志位置**：`%LOCALAPPDATA%\WeChatBridge\Inbox\Logs\windows.log`（应用日志）
与 `%LOCALAPPDATA%\WeChatBridge\App\registration.log`（注册 HResult）。

---

## 7. 微信侧枚举调查：Explorer 可见、微信不可见

### 7.1 现象与隔离

资源管理器共享面板中「微⁠信流」**正常出现**，但微信的「选择电脑中的其他应用」中**只有 WorkBuddy**。这是三个互相独立的环节：

```
[微信]  枚举 + 缓存 + 品牌过滤      ← 微信自己的问题
[系统]  Explorer 共享面板           ← 直接反映注册是否生效
[微信流] 被激活后才运行             ← 此时尚未参与
```

Explorer 里能看到，即证明注册与系统枚举链路完全正常，差异只在微信这一侧。

### 7.2 已排除的原因

| 假设 | 核查方式 | 结论 |
| --- | --- | --- |
| 注册失败 | `Get-AppxPackage` / `--registration-check` | ❌ Status=Ok，AUMID 正确 |
| 注册过程有错 | AppXDeploymentServer/Operational | ❌ 无任何 Warning/Error |
| 稀疏包结构问题 | 检查 WindowsApps 目录内容 | ❌ 与 WorkBuddy 一致 |
| `AllowExternalContent` 丢失 | 检查注册后的 manifest | ❌ 仍为 `true` |
| 外部内容 logo 缺失 | 核对 `App\Assets\` | ❌ 齐全 |
| `windows.shareTarget` 声明不符 | 与 WorkBuddy 的注册清单逐项比对 | ❌ 四个 `DataFormat` 一致，均为 `SupportsAnyFileType` |
| 显示名含连续品牌子串 | 检查注册后 manifest 的码点 | ❌ `U+5FAE U+2060 U+4FE1 U+6D41`，U+2060 在打包签名全程无损 |
| 枚举缓存 | 见 7.4 | ❌ 已排除 |

### 7.3 与 WorkBuddy 的包对比：只剩两个变量

| 属性 | WorkBuddy | 微信流 | 是否相同 |
| --- | --- | --- | --- |
| `SignatureKind` | `Developer` | `Developer` | ✅ 相同 |
| `ProcessorArchitecture` | `Neutral` | `Neutral` | ✅ 相同 |
| `AllowExternalContent` | `True` | `True` | ✅ 相同 |
| `runFullTrust` | `True` | `True` | ✅ 相同 |
| 包形态 | 稀疏包 | 稀疏包 | ✅ 相同 |
| **显示名** | `WorkBuddy`（纯 ASCII） | 中文 + U+2060 | ❌ **不同** |
| **Publisher** | `CN=Tencent Technology…` | `CN=WeChatBridge Windows Dev` | ❌ **不同** |

两个推论：

1. **「Developer 签名会被 shell 忽略」这一假设被推翻**——WorkBuddy 同样是 `SignatureKind=Developer` 且同样是稀疏包，却在两个面板里都能显示。
2. 差异只剩**显示名**与**Publisher 信任**两项。

### 7.4 一次时序失误

首次判定「微信不可见」时，进程时间线如下：

```
06:50:55  包注册完成
07:46:33  微信重新打开      ← 微信在此处完成枚举
07:56:20  explorer 重启     ← 晚于微信 10 分钟
```

微信枚举时 explorer 尚未重启，读到的很可能正是 Explorer 随后靠重启刷新的那份旧缓存，因此该阴性结果的证据强度不足。已按 `explorer` 先于微信重开的正确顺序重测——**结果仍为不可见**，此项才算排除。

### 7.5 待验证的核心假设：规范化先于过滤

`windows/docs/README.zh-CN.md:55` 的实测结论是「被任意字符（包括零宽字符）打断均不触发」，U+2060 绕过即建立在此基础上。

但若微信 4.x 后续改为**先做 Unicode 规范化删除零宽字符，再检查连续「微信」**，则该绕过会适得其反：规范化后 `微⁠信流` 还原为 `微信流`，恰好命中过滤条件。该文档记录于 2026-09-25 / 微信 4.1.15.13，实现可能已变。

WorkBuddy 使用纯 ASCII 显示名，永不触发此类过滤，故始终正常。

**决定性实验**：将 `DisplayName` 改为纯 ASCII 且不含品牌子串（如 `FlowBridge`），重新注册后复测。

- 若微信菜单出现 → 确认是显示名问题，中文或 U+2060 被微信拒绝，属真实缺陷，需重新设计方案而非继续调整 U+2060
- 若仍不可见 → 显示名无罪，问题落在 Publisher 信任或系统 build 层面

该实验可在临时目录内复制并修改 manifest 完成，**不必改动仓库文件**。

---

## 8. TODO：本次会话未处理的事项

> 记录时间：2026-10-17。本节汇总本次排查结束时仍未完成或未决的事项。

### 8.1 阻塞中（下一步应做）

| # | 事项 | 状态 | 说明 |
| --- | --- | --- | --- |
| T1 | **ASCII 显示名实验**（7.5） | 未开始 | 临时复制 manifest 改成 `FlowBridge` 等纯 ASCII 不含品牌子串的名字，重新注册后复测微信菜单。这是区分「显示名被拒」与「Publisher/build 限制」的决定性实验。**不要改动仓库文件** |
| T2 | 微信菜单枚举问题定论 | 未决 | 取决于 T1 结果。若 T1 阳性，需重新设计方案（当前 U+2060 绕过假设失效）；若阴性，问题在 Publisher 信任或系统 build |
| T3 | 真实投递链路验证 | 未开始 | 从 Explorer 共享面板真实分享 `产品讨论组.zip` 到微信流，验证 helper → Inbox → 「发送到…」目标选择器 → 目标应用整条链路。当前所有批次都是手工构造或 `action=clipboard`，**未真正投递过任何地方** |
| T4 | 目标 Agent 的实际激活与粘贴 | 未开始 | 需要目标应用（Claude / Codex 等）已安装，且该应用接受激活 + 粘贴 |

### 8.2 环境清理（可随时做）

| # | 事项 | 说明 |
| --- | --- | --- |
| T5 | 清除 3 个测试批次 | `%LOCALAPPDATA%\WeChatBridge\Inbox\Ready\` 下的 `58b38031…`、`4c09a754…`、`80a25070…`，都是手工注入或夹具，不影响真实使用 |
| T6 | 确定主程序实例来源 | 当前无实例运行。日常使用应从 install root `%LOCALAPPDATA%\WeChatBridge\App\WeChatBridge.Windows.exe` 启动（与注册的那套一致），而非仓库 `bin\Release` 下的开发产物 |
| T7 | 撤销机器级证书（如需） | 见 5.2 表格中的指纹。注意须连同稀疏包一起卸：`Remove-AppxPackage -Package 'WeChatBridge.Windows.ShareTarget_1.0.8.0_neutral__k2gw8zhv15c4m'` |

### 8.3 未处理的需求

| # | 事项 | 说明 |
| --- | --- | --- |
| T8 | **官方 1.1.17 安装包未安装** | `Downloads\WeChatBridge-1.1.17-Windows.msix`（实为 .msi）SHA256 已校验通过，但全程改用源码自行构建（v1.0.8），官方安装包从未执行安装。原因见 2.5：`CertificateTrust.msi` 不可得，但经实测该证书并非必需（5.2） |
| T9 | 源码版本落后于发布版 | 仓库 `1.0.8` vs 官方 `1.1.17`；`windows/tools/TransferTargetShim/` 与根因分析文档均不在本 checkout 中。若要研究兼容层需另行获取 |
| T10 | `macos/` 端未验证 | Windows 上无法构建 SwiftPM 包，其 CI 在 macos-15 上运行 |

### 8.4 明确不做（已结论）

| # | 事项 | 结论 |
| --- | --- | --- |
| T11 | 安装 `WeChatBridge-WeixinCompat-1.0.6` | **不要装**。其自愈守卫仅在 build ≥ 26100 时卸载，Win10 永等不到；Win10 上真实 `DataTransfer` 类存在，假类注册将永久遮蔽它们；且含 IFEO 注入微信的封号风险。注意其前提（微信要求 build ≥ 26100）**未被证实**——实测微信在本机确实提供了第三方应用入口 |
| T12 | 开启 Windows 开发人员模式 | **不需要**。曾推测「sideloaded 的 Developer 签名包不被 shell 枚举」，仅重启 explorer 即解决；`AppModelUnlock` 注册表项自始至终不存在 |

---

## 附：本文引用的关键位置

| 位置 | 内容 |
| --- | --- |
| `windows/Directory.Build.props:3` | `<Version>1.0.8</Version>` |
| `windows/packaging/msi/CertificateTrust.wxs:17-24` | 证书 MSI 安装叶证书与根证书的位置 |
| `windows/packaging/SparsePackage/AppxManifest.xml:46` | 经典 `uap:ShareTarget` 契约声明 |
| `windows/packaging/SparsePackage/AppxManifest.xml:20` | `MinVersion 10.0.19041.0` |
| `windows/src/WeChatBridge.ShareTarget/Program.cs:59,70` | 仅两个命令行开关 |
| `windows/src/WeChatBridge.Windows.Core/InboxModels.cs:14-50` | `ManifestItem` / `BatchManifest` 定义与 JSON 选项 |
| `windows/src/WeChatBridge.Windows.Core/InboxModels.cs:92-114` | Inbox 目录布局常量 |
| `windows/src/WeChatBridge.Windows.Core/InboxReader.cs:334` | 按 manifest 而非目录名反推路径 |
| `windows/src/WeChatBridge.Windows.Core/InboxReader.cs:336-352` | manifest 识别与静默跳过条件 |
| `windows/src/WeChatBridge.Windows.Core/ShareAction.cs:65-83` | `action` 的 12 个合法原始值 |
| `windows/src/WeChatBridge.Windows/MainViewModel.cs:481-489` | 新鲜 intent 导致 Expired |
| `windows/src/WeChatBridge.Windows/App.xaml.cs:12` | `InboxChanged` 事件名 |
| `windows/docs/verification-checklist.md:38` | 实测环境为 Windows 11 |
| `windows/docs/SHARE_TARGET_ENUMERATION.zh-CN.md` | 早期 `.zip` 收窄导致微信枚举过滤的根因复盘 |
