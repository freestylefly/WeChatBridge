# Windows Share Target PoC 验证清单

标记「自动」的条目由 `windows/tests/WeChatBridge.Windows.Tests` 覆盖；其余需要在真实微信环境中人工验收。

## 自动化验证

```powershell
dotnet restore windows\WeChatBridge.Windows.sln
dotnet build windows\WeChatBridge.Windows.sln -c Release
dotnet test windows\WeChatBridge.Windows.sln -c Release
windows\scripts\pack-msix.ps1 -OutputPath windows\artifacts\WeChatBridge.ShareTarget.msix
```

自动覆盖的契约：

| 契约 | 覆盖测试 |
| --- | --- |
| 单个 ZIP 落盘后 manifest 与文件内容一致 | `InboxWriterTests` |
| manifest 的 `createdAt` 是 `...Z` 形式的 ISO-8601 UTC，与 macOS 一致 | `ManifestContractTests` |
| manifest 不含绝对路径，`relativePath` 相对批次目录 | `ManifestContractTests` |
| manifest 可被共用 `JsonSerializerOptions` 往返读写 | `ManifestContractTests` |
| 多个 StorageItems 全部成功后一次性提交为单个批次 | `ManifestContractTests` |
| 任一文件失败时 `Ready` 与 `Staging` 都不留半成品 | `ManifestContractTests` |
| 超过单文件上限的分享被拒绝并记录失败 | `ManifestContractTests` |
| 超过批次总量上限的分享被拒绝 | `ManifestContractTests` |
| 重复文件名不会互相覆盖 | `InboxWriterTests` |
| `..` 名称被拒绝且不会写到批次目录之外 | `InboxWriterTests` |
| 符号链接 / 重解析点被拒绝 | `InboxWriterTests` |
| 非 ZIP 被拒绝并写入 `Failed` | `InboxWriterTests` |
| 中断复制的 `Staging` 残留会在超时后被清理 | `InboxMaintenanceTests` |
| 仍在写入的旧批次不会被误清理 | `InboxMaintenanceTests` |
| `Ready` 批次不受清理影响 | `InboxMaintenanceTests` |
| 重新打开目录后仍能发现 Ready 批次（helper 与主程序是不同进程） | `ManifestContractTests` |
| 显示名不含连续 `微信`/`WeChat`/`Weixin` 且 U+2060 未被剥离 | `PackagingAssetsTests` |

## 微信真实环境验收

以下条目无法自动化，需要在真机上人工确认（✅ 为 2026-09-25 在微信 4.1.15.13 / Windows 11 上的实测结果）：

- [x] 本地开发证书签名和 `Add-AppxPackage -ExternalLocation` 注册成功。
- [x] `Get-AppxPackage -Name WeChatBridge.Windows.ShareTarget` 可查到包。
- [x] 微信 4.1.15.13 的分享菜单出现“微⁠信流”入口（仅一行；显示名经 U+2060 绕过品牌子串过滤，见 `docs/README.zh-CN.md`）。
- [ ] 微信 3.9.12.55 记录为不支持 Share Target 的兼容性结果。
- [x] 真实的多选聊天记录合并转发 ZIP 能进入 `%LOCALAPPDATA%\WeChatBridge\Inbox\Ready`。
- [x] `FileDropList` 可在文件管理器中手动粘贴出原始 ZIP。
- [x] helper 在没有 WPF 主程序运行时也能写入剪贴板。
- [x] 主程序未运行时能被 helper 启动。
- [x] 主程序已运行时能刷新 Ready 批次。
- [x] 连续快速分享两次不会互相覆盖。
- [ ] 微信分享窗口关闭、取消或文件读取失败时不会留下半成品批次。
- [ ] 卸载后 Share Target 从微信分享菜单消失。
- [ ] 入口页关掉某入口后，「微⁠信流」选择器里不再出现它；只开一个入口时直接转发不再弹选择器。
- [ ] 重启 Windows 后注册状态保持正确。

注意：微信菜单由 `WeChatAppEx` 宿主渲染并缓存枚举结果；重新注册后若菜单未更新，结束全部 `Weixin` / `WeChatAppEx` 进程再开微信。
