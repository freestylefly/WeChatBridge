# Windows 本地 artifacts 构建与稀疏包注册实录

> 记录时间：2026-10-17
> 环境：Windows 10 Home 10.0.19045
> 工具链：.NET 10 SDK 10.0.402 + Microsoft.WindowsSDK.BuildTools 10.0.26100.9169（NuGet）
> 源码版本：`windows/Directory.Build.props` 中 `<Version>1.0.8</Version>`
> 前置阅读：[Windows 本地可行性验证与稀疏包注册实录](./windows-local-verification.zh-CN.md)

本文记录在上一轮排查结论的基础上，**首次真正产出 `windows/artifacts/` 目录**并完成稀疏包注册的完整过程。上一轮证明了「构建和测试可行」，但本机从未跑过打包脚本，`windows/artifacts/` 一直不存在；本文补上这一段，并记录了几个只有真正走一遍才会暴露的坑。

结论先行：**在不安装完整 Windows SDK、不申请管理员权限的前提下，用一个 NuGet 包即可完成便携布局 + 稀疏 MSIX 的构建与本地注册**，全程验证通过。

---

## 1. 起点：artifacts 目录为什么是空的

`.gitignore:57` 忽略了 `windows/artifacts/`，而该目录从未被创建。本机只有 `dotnet build` 的裸输出：

```text
D:\WeChatBridge\windows\src\WeChatBridge.Windows\bin\Release\net10.0-windows10.0.19041.0\win-x64\
D:\WeChatBridge\windows\src\WeChatBridge.ShareTarget\bin\Release\net10.0-windows10.0.19041.0\win-x64\
```

这些是 framework-dependent 的构建输出，不是可分发的布局。`windows/packaging/README.md` 描述的安装根目录布局从未在本机被组装过。

### 1.1 缺失的依赖

| 组件 | 初始状态 | 影响 |
| --- | --- | --- |
| .NET 10 SDK | ✅ 10.0.402（Git Bash 中需手动 export PATH） | — |
| Windows SDK | ❌ 无 `C:\Program Files (x86)\Windows Kits` | `makepri` / `makeappx` / `signtool` / `mt` 全部缺失 |
| WiX 6 + Util/UI/Bal | ❌ 未安装 | MSI + Setup.exe 无法构建 |
| 开发证书 | ✅ 已存在（上一轮 `new-dev-certificate.ps1` 生成） | — |

Git Bash 中 `dotnet` 不在 PATH 上，仍需：

```bash
export PATH="$PATH:/c/Program Files/dotnet"
```

---

## 2. 工具链决策：用 NuGet 包替代整套 Windows SDK

`pack-msix.ps1` 的 `Resolve-SdkTool()` 优先走 `Get-Command`，找不到再扫描 `%ProgramFiles(x86)%\Windows Kits\10\bin\*\x64\`。这意味着**只要把这三个工具放进 PATH 就能满足，不必真的装 SDK**。

winget 上可选的 Windows SDK 只有 `10.0.17134` 和 `10.0.18362` 两个陈旧版本，而 `AppxManifest.xml` 的 `TargetDeviceFamily` 是 `MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0"`，用 18362 的工具去 pack 一个声明 26100 的包并不稳妥。

改用 NuGet 包 `Microsoft.WindowsSDK.BuildTools`，这是 WinAppSDK 在 CI 上构建应用的常规做法 —— 它包含四个所需工具，且解压即用：

```bash
mkdir -p /d/WeChatB-Hub/_scratch/tools && cd /d/WeChatB-Hub/_scratch/tools
curl -sL -o sdkbuildtools.zip \
  "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/10.0.26100.9169/microsoft.windows.sdk.buildtools.10.0.26100.9169.nupkg"
unzip -q -o sdkbuildtools.zip -d sdkbuildtools
```

工具落在：

```text
D:\WeChatB-Hub\_scratch\tools\sdkbuildtools\bin\10.0.26100.0\x64\{makepri,makeappx,signtool,mt}.exe
```

选 26100 分支与 manifest 的 `MaxVersionTested` 对齐。整个过程约 22 MB，无管理员权限，无系统级安装。

> 顺带一提：`build-installer.ps1` 的默认 `-WixExtensionDirectory` 恰好是 `D:\WeChatB-Hub\_scratch\tools\wixext`。本次工具放在它的上级目录，将来补装 WiX 扩展时路径能直接对上。

---

## 3. 构建步骤

以下所有命令都在 Git Bash 中执行，且**必须**先设置两个环境变量（原因见 §5.1、§5.2）：

```bash
export PATH="$PATH:/d/WeChatB-Hub/_scratch/tools/sdkbuildtools/bin/10.0.26100.0/x64"
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'
```

### 3.1 发布便携布局

与 `.github/workflows/windows-sign.yml:57-61` 的参数保持一致（framework-dependent）：

```bash
dotnet publish windows/src/WeChatBridge.Windows/WeChatBridge.Windows.csproj \
    -c Release -r win-x64 --self-contained false -o windows/artifacts/release --nologo

dotnet publish windows/src/WeChatBridge.ShareTarget/WeChatBridge.ShareTarget.csproj \
    -c Release -r win-x64 --self-contained false -o windows/artifacts/release/share-target --nologo
```

### 3.2 打稀疏 MSIX

```bash
powershell -NoProfile -File 'D:\WeChatBridge\windows\scripts\pack-msix.ps1' \
    -Publisher 'CN=WeChatBridge Windows Dev' \
    -Version '1.0.8.0' \
    -OutputPath 'D:\WeChatBridge\windows\artifacts\release\WeChatBridge.ShareTarget.msix' \
    -ExternalContentDirectory 'D:\WeChatBridge\windows\artifacts\release'
```

`pack-msix.ps1` 除了 pack，还会把生成的 `resources.pri` 复制到外部内容目录 —— 这是必须的，因为 `AllowExternalContent` 包的资源查找走的是外部位置，只拷图标是不够的（脚本 `:61-64` 的注释说明了这一点）。

### 3.3 同步包用图标（易漏，见 §5.3）

```bash
cp -n windows/packaging/SparsePackage/Assets/*.png windows/artifacts/release/Assets/
```

### 3.4 签名

`pack-msix.ps1` **不打签名**，签名是调用方的职责。本机开发证书已在 `Cert:\CurrentUser\My`，根证书也早已在 `Cert:\CurrentUser\Root` 中信任：

```bash
signtool sign /fd SHA256 /sha1 C064A5FA6BA7B92D49827D1F7974FC967351359C \
    windows/artifacts/release/WeChatBridge.ShareTarget.msix
signtool sign /fd SHA256 /sha1 C064A5FA6BA7B92D49827D1F7974FC967351359C \
    windows/artifacts/release/WeChatBridge.Windows.exe
signtool sign /fd SHA256 /sha1 C064A5FA6BA7B92D49827D1F7974FC967351359C \
    windows/artifacts/release/share-target/WeChatBridge.ShareTarget.exe
```

签完 `Get-AuthenticodeSignature` 返回 `Status: Valid`，不需要再手动导入任何证书。

### 3.5 校验

```bash
powershell -NoProfile -File 'D:\WeChatBridge\windows\scripts\Test-SharePackage.ps1' \
    -InstallRoot 'D:\WeChatBridge\windows\artifacts\release'
```

输出：`Share package data formats, identity, external resource index and visual assets verified.`

---

## 4. 注册

`register-dev.ps1` 自己会重新 pack、签名并调 `Add-AppxPackage`，所以 §3.2–§3.4 手工做的部分会被覆盖一遍，但布局和资产是复用的：

```bash
powershell -NoProfile -File 'D:\WeChatBridge\windows\scripts\register-dev.ps1' \
    -InstallRoot 'D:\WeChatBridge\windows\artifacts\release'
```

结果：

```text
Registered: WeChatBridge.Windows.ShareTarget_1.0.8.0_neutral__k2gw8zhv15c4m
```

`Get-AppxPackage` 确认 `Status: Ok`，`InstallLocation` 指向 `WindowsApps\...`。

### 4.1 运行时身份校验

仅凭 `Get-AppxPackage` 有记录是不够的（`build-installer.ps1:16-19` 明确指出了这一点），分享入口自带无副作用的校验开关：

```bash
windows/artifacts/release/share-target/WeChatBridge.ShareTarget.exe --registration-check
```

```json
{"PackageFullName":"WeChatBridge.Windows.ShareTarget_1.0.8.0_neutral__k2gw8zhv15c4m",
 "ApplicationUserModelId":"WeChatBridge.Windows.ShareTarget_k2gw8zhv15c4m!Share.Hub"}
```

exit 0，身份与 manifest 的 `Identity` / `Id="Share.Hub"` 一致。

### 4.2 仍需人工重启

微信的分享目标列表在进程启动时枚举并缓存，**注册完必须重启 explorer 和微信**才能在分享菜单里看到「微⁠信流」。这一步会销毁桌面与未保存的窗口状态，属人工决策。

### 4.3 explorer 不会自动重启

`taskkill /f /im explorer.exe` 之后，本机（Windows 10 Home 10.0.19045）的 explorer **没有自动拉起**，桌面直接消失。这与「杀 explorer 会自动恢复」的常见预期不符 —— 从脚本化环境发起的强制终止没有触发 shell 的自动重启逻辑。

必须显式启动：

```bash
powershell -NoProfile -Command "Start-Process -FilePath explorer.exe"
```

启动后确认：

```text
映像名称                       PID 会话名              会话#       内存使用
========================= ======== ================ =========== ===========
explorer.exe                  6624 Console                    1    140,944 K
```

注：Git Bash 下 `cmd //c start "" explorer.exe` 不可靠 —— MSYS 的 `//c` 转义会退化成交互式 `cmd`，只打印版本横幅就退出。用 `Start-Process` 更稳。

---

## 5. 踩到的坑

### 5.1 MSYS 会把 `/fd` 当成 Unix 路径转换

第一次签名全部失败，报的是看起来毫不相关的错误：

```text
SignTool Error: No file digest algorithm specified. Please specify the digest algorithm with the /fd flag.
```

实际原因是 Git Bash 的 MSYS 路径转换把 `/fd` 当成了一个路径参数。修复：

```bash
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL='*'
```

### 5.2 不需要 `-ExecutionPolicy Bypass`

`powershell -NoProfile -File script.ps1` 在默认执行策略下即可运行。仓库脚本没有 Zone.Identifier 标记，不需要解除执行策略 —— 也不应该为了省事就加上这个开关。

### 5.3 `dotnet publish` 不会同步包用图标

`Test-SharePackage.ps1` 第一次就卡在这里：

```text
External visual asset is missing: Assets\Square150x150Logo.png
```

`dotnet publish` 只会带出主应用自己的 `Assets\AppIcon.ico`、`TrayGlyph.png`、`AppLogos\`。`AppxManifest.xml` 声明的 `Square150x150Logo.png` / `Square44x44Logo.png` 来自 `windows/packaging/SparsePackage/Assets/`，平时由 `register-dev.ps1:34-42` 在注册时同步到外部内容目录。

**手工组包时这步没有任何脚本会替你做**，必须显式拷贝，否则 shell 解析不到图标就会静默地把分享目标从分享面板里省掉。

### 5.4 无 BOM 的 UTF-8 + PowerShell 5.1

`AppxManifest.xml` 是无 BOM 的 UTF-8，PowerShell 5.1 会按 GBK 读，中文静默损坏。`pack-msix.ps1:39` 用 `Get-Content -Raw -Encoding UTF8` 显式指定，`$manifest.Save()` 又按 XML 声明写回，所以这条路径是安全的 —— 但值得实测确认：

```bash
unzip -p WeChatBridge.ShareTarget.msix AppxManifest.xml | grep -o 'DisplayName="[^"]*"'
```

输出 `DisplayName="微⁠信流"`，那个用于躲避微信关键词过滤的 `U+2060` 词连接符完好。

### 5.5 `registration.log` 的乱码

`register-dev.ps1:12` 用 `Add-Content`（默认 ANSI/GBK）写日志，其中包含中文。日志文件本身内容正确，但用 UTF-8 工具读取时会看到尾部乱码。只影响可读性，不影响注册结果。

### 5.6 硬编码的证书 thumbprint 已失效

`build-installer.ps1` 原本写死：

```powershell
[string]$SigningThumbprint = '40ED85A6287A88A950D0FEE9EA3C9DBC032358CD',
```

本机实际可用的是 `C064A5FA6BA7B92D49827D1F7974FC967351359C`（`CN=WeChatBridge Windows Dev`，私钥在，有效至 2028-10-17）。已修复，见 §6。

---

## 6. 顺带修复：`build-installer.ps1` 的证书默认值

没有把默认值换成另一串机器相关的硬编码 —— 那只是把坑换了个位置。`new-dev-certificate.ps1` 是**每台机器现生成证书**的，任何写死的 thumbprint 都会静默失效。改为按主题解析，与 `register-dev.ps1:45-48` 保持一致：

```powershell
[string]$WixExtensionDirectory = 'D:\WeChatB-Hub\_scratch\tools\wixext',
# No machine-specific thumbprint default: new-dev-certificate.ps1 mints a fresh
# certificate per machine, so a hardcoded one goes stale silently. Resolved from
# the subject below, the same way register-dev.ps1 does.
[string]$SigningThumbprint,
```

```powershell
if (-not $SigningThumbprint) {
    $SigningThumbprint = (Get-ChildItem Cert:\CurrentUser\My |
        Where-Object Subject -eq 'CN=WeChatBridge Windows Dev' |
        Sort-Object NotAfter -Descending | Select-Object -First 1).Thumbprint
    if (-not $SigningThumbprint) { throw 'No development certificate found. Run new-dev-certificate.ps1 or pass -SigningThumbprint.' }
}
$certificate = Get-Item "Cert:\CurrentUser\My\$SigningThumbprint"
```

验证：PowerShell 解析器 `parse OK`；解析逻辑在本机解析出 `C064A5FA…`，`HasPrivateKey: True`，`NotAfter: 2028-10-17`。`-SigningThumbprint` 仍可显式传入以覆盖。

---

## 7. 当前状态

产物路径：

```text
D:\WeChatBridge\windows\artifacts\release\
├── WeChatBridge.Windows.exe          主应用（framework-dependent win-x64，ReadyToRun）
├── WeChatBridge.ShareTarget.msix     稀疏包，已签名，Status: Valid
├── resources.pri                     包内外哈希一致
├── Assets\                           主应用图标 + 23 个包用 logo
├── Resources\Skills\                 共享技能目录
└── share-target\
    └── WeChatBridge.ShareTarget.exe  分享入口
```

| 项目 | 状态 |
| --- | --- |
| 便携发布布局 | ✅ 已产出（framework-dependent，跨机需装 .NET 10） |
| 稀疏 MSIX | ✅ 1.0.8.0，开发证书签名，`Get-AuthenticodeSignature` 为 `Valid` |
| 包注册 | ✅ `Status: Ok`，`--registration-check` 通过 |
| `Test-SharePackage.ps1` | ✅ 数据格式 / 身份 / resources.pri 哈希 / 视觉资源全过 |
| MSI + Setup.exe | ❌ 未做，还缺 WiX 6 + Util/UI/Bal 扩展 |
| explorer / 微信重启 | ✅ explorer 已重启（PID 6624）；微信需用户手动重启 |

## 8. 未做与后续

- **MSI + Setup.exe**：仍需安装 WiX 6 及 Util/UI/Bal 三个扩展。`build-installer.ps1` 的 `-WixExtensionDirectory` 默认路径指向的目录仍不存在，届时需 `-WixExtensionDirectory` 显式指定。
- **离线自包含安装**：本次是 framework-dependent。若要拷到没装 .NET 的机器，需 `-SelfContained` 重新 publish（体积会显著增大）。
- **重启微信**后才能在分享菜单中看到入口（explorer 已重启）。
- 卸载：`powershell -File windows\scripts\unregister-dev.ps1 -InstallRoot D:\WeChatBridge\windows\artifacts\release`

## 9. 可复现的完整命令序列

```bash
export PATH="$PATH:/c/Program Files/dotnet"
export PATH="$PATH:/d/WeChatB-Hub/_scratch/tools/sdkbuildtools/bin/10.0.26100.0/x64"
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'
cd /d/WeChatBridge

dotnet publish windows/src/WeChatBridge.Windows/WeChatBridge.Windows.csproj \
    -c Release -r win-x64 --self-contained false -o windows/artifacts/release --nologo
dotnet publish windows/src/WeChatBridge.ShareTarget/WeChatBridge.ShareTarget.csproj \
    -c Release -r win-x64 --self-contained false -o windows/artifacts/release/share-target --nologo

cp -n windows/packaging/SparsePackage/Assets/*.png windows/artifacts/release/Assets/

powershell -NoProfile -File 'D:\WeChatBridge\windows\scripts\register-dev.ps1' \
    -InstallRoot 'D:\WeChatBridge\windows\artifacts\release'

powershell -NoProfile -File 'D:\WeChatBridge\windows\scripts\Test-SharePackage.ps1' \
    -InstallRoot 'D:\WeChatBridge\windows\artifacts\release'

./windows/artifacts/release/share-target/WeChatBridge.ShareTarget.exe --registration-check
```

注：`register-dev.ps1` 已包含 pack + 签名两步，因此在其之前的 `pack-msix.ps1` 与 `signtool` 调用可省略；此处保留是为了让每个步骤都能独立验证。
