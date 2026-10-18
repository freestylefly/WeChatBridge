# 进度看板：feat/client-license-gate（机器 C）

> 规则见 `docs/design/progress-sync-protocol.zh-CN.md` §2：只有机器 C 更新本文件，与 commit 同一时刻。
>
> 2026-10-18 校准：此前看板未随 `d48827f` 更新，C-①/③/⑤ 的数据层与控制器其实已落地。
> 本表按 `docs/design/client-license-gate-progress.zh-CN.md` 的 0–19 细目重新核对代码后重写。
> 逐项状态 ⬜ / ⏳ / ✅，⏳ = 已有代码但未收尾或未验证。

## 任务表

| # | 任务 | 状态 | commit | 备注 |
|---|---|---|---|---|
| C-① | 微信登录（session → QR PNG → 轮询 → 存双令牌） | ⏳ | `d48827f` | Core + `AccountController` 完成；`LoginWindow` 未提交且编译红，见下 |
| C-② | 机器码展示（installId 主键 + 硬件锚摘要辅证） | ⏳ | `4ce2420` | `ActivationGateway` 已暴露 `InstallId` / `AnchorDigest`，**无 UI** |
| C-③ | 购买（收款码静态图 + 登记 + 轮询 + 取票） | ⏳ | `d48827f` | `ActivationController` 完成；`PurchaseWindow` 未开工，资产已放进工作区 |
| C-④ | 启动门禁集成（未登录→登录页；已登录未授权→限制模式） | ⬜ | — | `ActivationGateway` **零引用**，`App.xaml.cs` 未接线 |
| C-⑤ | 轮询退避 + 卖家拒绝/超时提示 | ⏳ | `4ce2420` `d48827f` | `PollSchedule` + 控制器失败分支已写，**无测试** |
| C-⑥ | 出口 = `dotnet build/test` 通过 + mock 全流程 | ⬜ | — | build 当前红；测试一行未加 |

## 细目状态（对照 `client-license-gate-progress.zh-CN.md`）

| # | 任务 | 状态 |
|---|---|---|
| 0–9 | 契约 DTO / 传输抽象 / 退避 / 令牌 / 账户 / 激活 / 状态机 / Http 动词 / 惰性装配 | ✅ `4ce2420` |
| 10 | `AccountController`（取票+取图+等扫码合一，取消为终态） | ✅ `d48827f` |
| 11 | `ActivationController`（登记+轮询+取票） | ✅ `d48827f` |
| 12 | 登录窗 UI | ⏳ 未提交，`LoginWindow.xaml:54` 编译失败 |
| 13 | 购买窗 UI | ⬜ |
| 14 | 机器码展示控件 | ⬜ |
| 15 | 启动门禁接入 | ⬜ |
| 16 | 新文案进 `en.json` | ⬜ 四条登录文案一条都没进 |
| 17 | Core 单测（退避 / 时间戳 / 登录 / 激活） | ⬜ 无任何 `*Activation*Tests.cs` |
| 18 | AppTests：`HttpActivationTransport` handler 注入 | ⬜ |
| 19 | 出口 | ⬜ |

## 当前状态：绿

编译与测试已恢复全绿（2026-10-18 本机验证）：

| 检查 | 结果 |
|---|---|
| `dotnet build -c Release -p:WeChatBridgeEdition=Perpetual` | ✅ 0 警告 0 错误 |
| `dotnet test -p:WeChatBridgeEdition=Perpetual` | ✅ 497 + 45 通过 |
| `dotnet test -p:WeChatBridgeEdition=Subscription` | ✅ 496 + 45 通过（少一条为 edition 条件测试） |
| `Test-NoNetworkCode.ps1 -Edition Perpetual` | ✅ |

修了三处，都是让工作区从红回到绿所必需的：

1. **`LoginWindow.xaml:49-56`** — Border 塞了 `QrImage` 与 `QrPlaceholder` 两个子元素，
   WPF 的 Border 只接受一个。图片与占位文字本就要叠放切换，套一层 Grid。
2. **`LoginWindow.xaml.cs:128`** — `BitmapFrame` 没有 `CanFree`，该守卫不编译。改成
   直接 `Freeze()`：frame 由本方法 `using` 掉的 MemoryStream 构造且 `OnLoad` 已把像素
   读入，没有 freeze 会损坏的共享状态，而 `Freeze` 对已冻结对象本就是空操作。
3. **`LicensingCanonicalPayloadTests.SharedCanonicalVector`** — 见下，这是本次最值得记的一条。

## ⚠️ 跨语言契约踩坑：CRLF 污染字节精确向量

`LicensingCanonicalPayloadTests.TheClientProducesTheSharedVectorExactly` 红了，期望含
`\r\n` 而实际产出 `\n`。

**不是生产代码的错。** 同一文件的 `TheSharedVectorHasTheDocumentedShapeAndDigest` 用
`Vector().Bytes`（生产输出）断言 109 字节与摘要 `0897d8…`，**它一直是绿的**——证明生产端
canonical 与 Go 侧钉死的摘要一致。

真因是 `core.autocrlf=true` 且仓库**没有 `.gitattributes`**：

```
git ls-files --eol → i/lf  w/crlf
```

C# raw string literal 保留文件里的行尾，所以 Windows 检出的字面量每个换行都是 `\r\n`。
这类失败最阴的地方在于**签名照样完全有效、字段照样全解析**，只是客户端永远和服务端对不上。

修法：字面量末尾加 `.ReplaceLineEndings("\n")`，仍然是手写的、不由被测实现生成
（该文件开头的注释明确要求这一点），但不再受检出方式影响。`const` 因此改为
`static ... { get; }`。

教训：**任何钉死字节的向量都不能直接放在源文件字面量里**。要么归一化，要么加
`.gitattributes` 强制 `*.cs text eol=lf`（后者是仓库级决定，需与另两条分支协调）。

## 出口标准

- [ ] `dotnet build/test` 通过（当前 build 红）
- [ ] 对着 mock 走通：扫码登录 → 机器码展示 → 登记 → 确认 → 取票 → 验签缓存
- [ ] 真服务端签名对下的终态验证（任务 17，走 `AppTests` 已有的 `TestLicenseIssuer`）

## 红线（禁改）

`windows/src/WeChatBridge.Windows.Core/Licensing/` 下 LicenseResolver.cs / LicenseVerifier.cs / InstallIdentityStore.cs / DeviceAnchor.cs 四个文件——契约级冻结，只读复用。

## 阻塞项

无外部阻塞（契约已冻结）。

范围外但影响出口的两项，等机器 A：

- 🔴 `LicensePublicKeys.PlaceholderKeyV1` 仍是占位串，发版前必须换真公钥；未换前一切票据验不过，`IsPro` 恒 false。这是设计上的 fail-closed，不是 bug。
- 🟡 `server/mock/` 不在本分支树上，mock 全流程需借主检出跑：`node /d/WeChatBridge/server/mock/server.js --port 8901`，客户端 `export WECHATBRIDGE_LICENSE_SERVER=http://127.0.0.1:8901/`。
