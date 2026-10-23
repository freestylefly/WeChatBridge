# 分享目标在微信「转发到其他应用」中不显示——根因与修复记录

> 版本：1.0.7 / 2026-10-04 复盘
> 关联文件：`packaging/SparsePackage/AppxManifest.xml`、`scripts/Test-SharePackage.ps1`、
> `packaging/msi/Register-ShareTarget.ps1`

## 症状

打包出来的安装包（0.1.16 → 1.0.7 多个版本）安装后一切看似正常——

- `Get-AppxPackage` 能查到 `ChatBridge.Windows.ShareTarget_1.0.7.0_neutral__k2gw8zhv15c4m`，Status=Ok
- `share-target\WeChatBridge.ShareTarget.exe --registration-check` 返回正确 AUMID
- `Microsoft-Windows-AppXDeploymentServer/Operational` 日志显示注册成功，无错误

但微信（Weixin 4.x）的「转发到其他应用」菜单里始终没有「聊天桥」。

## 根因

**清单声明过窄，在微信宿主枚举 ShareTarget 阶段就被过滤掉，根本轮不到展示名字。**

当时的 `AppxManifest.xml` 写的是：

```xml
<uap:ShareTarget Description="聊天桥">
  <uap:SupportedFileTypes><uap:FileType>.zip</uap:FileType></uap:SupportedFileTypes>
  <uap:DataFormat>StorageItems</uap:DataFormat>
</uap:ShareTarget>
```

决定性证据来自同机腾讯自家 `WorkBuddyShareTarget` 包的清单注释（原文照录）：

> An overly narrow SupportedFileTypes (e.g. only ".zip") + a single DataFormat is
> filtered out by the Weixin host during ShareTarget enumeration even though the
> system Share Sheet still shows it.

即：**窄文件类型 + 单一 DataFormat 会被 Weixin 宿主在枚举时剔除**；系统自己的分享面板能看到，但微信看不到。腾讯两个能正常显示的包（`WeixinShare`、`WorkBuddyShareTarget`）形态一致：

```xml
<uap:SupportedFileTypes><uap:SupportsAnyFileType /></uap:SupportedFileTypes>
<uap:DataFormat>StorageItems</uap:DataFormat>
<uap:DataFormat>Text</uap:DataFormat>
<uap:DataFormat>URI</uap:DataFormat>
<uap:DataFormat>Bitmap</uap:DataFormat>
```

宽声明只是"枚举可见性"层面的——helper 运行时仍按自己的规则校验真实负载，多声明格式不会让不支持的分享真正进来。

## 这个问题是怎么发生的

**不是迁移遗漏，是迁移之后的排障改错了方向。**

`git diff` 显示 `AppxManifest.xml` 相对 HEAD 是未提交的工作区改动。上游原版一直是
`SupportsAnyFileType` + `AppListEntry="none"`，且在 `verification-checklist.md`
中有 2026-09-25 的实测记录：微信 4.1.15.13 下「微⁠信流」入口可正常显示。

排障过程中形成了两个错误结论，并被写进 `Test-SharePackage.ps1` 固化为断言：

| 错误结论 | 实际后果 |
| --- | --- |
| "WeChat on Windows 11 23H2 excludes hidden share applications" | 删掉了 `AppListEntry="none"` |
| "Explicit ZIP matching is required by WeChat" | 把 `SupportsAnyFileType` 收窄成 `.zip` |

收窄方向恰好相反——越窄越被过滤。之后再改名为「聊天桥」也无效，因为枚举阶段
就不通过了。这个错误配置又被测试脚本强制要求，导致后续每个版本都"稳定地错"。

教训：**排障期的假设一旦写进自动化断言，就从猜测变成了强制执行的错误。**

## 修复内容

`AppxManifest.xml`：

- `SupportedFileTypes` 恢复为 `<uap:SupportsAnyFileType />`
- `DataFormat` 补齐为 `StorageItems` / `Text` / `URI` / `Bitmap` 四项
- `VisualElements` 恢复 `AppListEntry="none"`（与腾讯两个可用包一致）

`Test-SharePackage.ps1`：断言反转——现在强制要求 `SupportsAnyFileType` 且四种
DataFormat 齐全，防止再次收窄。

## 热修复验证方法（不重打安装包）

```powershell
# 1. 重打 msix（manifest 已修正）
windows\scripts\pack-msix.ps1 -Publisher 'CN=WeChatBridge Windows Dev' -Version '1.0.7.0' `
  -OutputPath "$env:TEMP\WeChatBridge.ShareTarget.new.msix" `
  -ExternalContentDirectory "$env:LOCALAPPDATA\WeChatBridge\App"

# 2. 同一张开发证书重签
signtool sign /fd SHA256 /sha1 <证书指纹> "$env:TEMP\WeChatBridge.ShareTarget.new.msix"

# 3. 替换已安装的 msix 并重新注册
Copy-Item "$env:TEMP\WeChatBridge.ShareTarget.new.msix" "$env:LOCALAPPDATA\WeChatBridge\App\WeChatBridge.ShareTarget.msix" -Force
Remove-AppxPackage -Package 'ChatBridge.Windows.ShareTarget_1.0.7.0_neutral__k2gw8zhv15c4m'
Add-AppxPackage -Path "$env:LOCALAPPDATA\WeChatBridge\App\WeChatBridge.ShareTarget.msix" `
  -ExternalLocation "$env:LOCALAPPDATA\WeChatBridge\App"
```

**关键：结束全部 `Weixin` / `WeChatAppEx` 进程再打开微信。** 微信的分享菜单由
`WeChatAppEx` 宿主渲染并缓存枚举结果（见 `verification-checklist.md`），不重启进程
即使注册正确也不会刷新。

## 排查时容易误判的点

1. `Get-AppxPackage` 显示 Ok ≠ 分享目标对微信可见。包注册成功只说明 AppX 层接受了
   清单，微信宿主有自己的枚举过滤。
2. `Classes\Extensions\ContractId\Windows.ShareTarget` 注册表项缺失不是判据——
   腾讯正常工作的包同样没有这些键，契约枚举走的是 AppModel 状态存储。
3. 微信菜单缓存独立于我方注册动作，改完清单必须杀进程验证，否则会误以为改动无效
   而继续错改配置（这次的 `.zip` 收窄很可能就是这样产生的）。
