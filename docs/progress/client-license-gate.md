# 进度看板：feat/client-license-gate（机器 C）

> 规则见 `docs/design/progress-sync-protocol.zh-CN.md` §2：只有机器 C 更新本文件，与 commit 同一时刻。

## 任务表

| # | 任务 | 状态 | commit | 备注 |
|---|---|---|---|---|
| C-① | 微信登录 UI（session → QR PNG → 轮询 → 存双令牌） | ⬜ | — | auth 契约已冻结，mock 全量实现 |
| C-② | 机器码展示页（installId 主键 + 硬件锚摘要辅证） | ⬜ | — | D2 建议值；纯本机零网络依赖 |
| C-③ | 购买页（收款码静态图 + 登记 + 轮询 + 取票据） | ⬜ | — | D1 建议值个人码 |
| C-④ | 启动门禁集成（未登录→登录页；已登录未授权→功能限制） | ⬜ | — | |
| C-⑤ | 轮询退避 + 卖家拒绝/超时提示 | ⬜ | — | |
| C-⑥ | 出口 = `dotnet build/test` 通过 + mock 全流程 | ⬜ | — | |

## 出口标准

- [ ] `dotnet build/test` 通过
- [ ] 对着 mock 走通：扫码登录 → 机器码展示 → 登记 → 确认 → 取票 → 验签缓存

## 红线（禁改）

`windows/src/WeChatBridge.Windows.Core/Licensing/` 下 LicenseResolver.cs / LicenseVerifier.cs / InstallIdentityStore.cs / DeviceAnchor.cs 四个文件——契约级冻结，只读复用。

## 阻塞项

（暂无）
