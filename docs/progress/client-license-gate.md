# 进度看板：feat/client-license-gate（机器 C）

> 规则见 `docs/design/progress-sync-protocol.zh-CN.md` §2：只有机器 C 更新本文件，与 commit 同一时刻。
>
> 2026-10-18 校准：此前看板未随 `d48827f` 更新，C-①/③/⑤ 的数据层与控制器其实已落地。
> 本表按 `docs/design/client-license-gate-progress.zh-CN.md` 的 0–19 细目重新核对代码后重写。
> 逐项状态 ⬜ / ⏳ / ✅，⏳ = 已有代码但未收尾或未验证。
>
> 2026-10-19 校准：C-⑤ 的退避单测补齐（`cbcaa5d`）；C-④ 的 `ActivationGate` 早已随
> `2d994b0` 落树，此前的「待提交」是看板滞后，不是代码缺失。同批补了 C-1 时间戳转换器
> 修复（`b822761`）与 S1–S4 共 80 项新测试。
>
> 2026-10-19 再校准（服务端真机联调后）：**C-① 降级 ⏳** —— 见下「微信登录二维码的错配」。
> 原判 ✅ 只核对了「代码存在且编译通过」，没拿真机扫码跑通一次。
> 另：`LicensePublicKeys.PlaceholderKeyV1` 的占位公钥已换成真实签发公钥，
> 此前的 🔴 阻塞项解除（私钥在服务端 `/etc/wechatbridge/license-ec.pk8.pem`，
> 公私钥 DER 逐字节一致，签名/验签实测通过）。

## 任务表

| # | 任务 | 状态 | commit | 备注 |
|---|---|---|---|---|
| C-① | 微信登录（session → QR PNG → 轮询 → 存双令牌） | ⏳ | `98061ea` | 代码在，**但 QR 那一步是错的**，见下「微信登录二维码的错配」 |
| C-② | 机器码展示（installId 主键 + 硬件锚摘要辅证） | ✅ | `7232d39` | 购买窗内的可复制机器码 + 锚摘要；通用页另有一行常驻显示 |
| C-③ | 购买（收款码静态图 + 登记 + 轮询 + 取票） | ⏳ | `7232d39` | `PurchaseWindow` 已写；**待产品方补齐收款码与开发者微信** |
| C-④ | 启动门禁集成（未登录→登录页；已登录未授权→引导购买） | ✅ | `2d994b0` | `ActivationGate`；**不锁任何功能**，见下 |
| C-⑤ | 轮询退避 + 卖家拒绝/超时提示 | ✅ | `4ce2420` `d48827f` `cbcaa5d` | `PollSchedule` + 控制器失败分支；退避算术单测已补（`cbcaa5d`） |
| C-⑥ | 出口 = `dotnet build/test` 通过 + mock 全流程 | ⏳ | 见下 | 两形态 build/test 全绿， `-background` 实跑常驻；mock 全流程仍未跑 |

## 细目状态（对照 `client-license-gate-progress.zh-CN.md`）

| # | 任务 | 状态 |
|---|---|---|
| 0–9 | 契约 DTO / 传输抽象 / 退避 / 令牌 / 账户 / 激活 / 状态机 / Http 动词 / 惰性装配 | ✅ `4ce2420` |
| 10 | `AccountController`（取票+取图+等扫码合一，取消为终态） | ✅ `d48827f` |
| 11 | `ActivationController`（登记+轮询+取票） | ✅ `d48827f` |
| 12 | 登录窗 UI | ✅ `98061ea` |
| 13 | 购买窗 UI | ✅ 代码完成，⏳ 两处占位符待补 |
| 14 | 机器码展示控件 | ✅ 可复制 TextBox + 复制按钮，嵌在购买窗内；通用页另有常驻一行 |
| 15 | 启动门禁接入 | ✅ `ActivationGate` + `App.xaml.cs` + 通用页 |
| 16 | 新文案进 `en.json` | ✅ 40 条，登录 + 购买 + 两个控制器 |
| 17 | Core 单测（退避 / 时间戳 / 登录 / 激活） | ✅ S1–S4 共 80 项：`PollScheduleTests` `cbcaa5d`、`ActivationContractTests` `77c33b2`、`AccountServiceTests` `a1624e3`、`ActivationServiceTests` `7de97be` |
| 18 | AppTests：`HttpActivationTransport` handler 注入 | ⬜ 传输层靠 `IActivationTransport` seam 覆盖，真实 HttpClient 未测 |
| 19 | 出口 | ⏳ build/test 双形态绿、`-background` 实跑常驻；mock 全流程未跑 |

## 门禁不锁任何东西（与任务书措辞不同）

任务 15 写的是「已登录未授权→功能限制模式」，但设计文档自己的注释否掉了这个说法：

- `GateState.SignedIn` 的文档注释：*"The customer can buy; **nothing is locked behind a
  licence they have not paid for**."*
- `LicensingBootstrap.IsShareDeliveryAvailable => true`，恒为 true，有测试守着
- 全仓库 grep `IsPro`：只被兑换窗用来显示「已经是 Pro」，**没有任何 UI 真的受它限制**

所以按「引导购买」而非「上锁」实现。`ActivationGate.Decide` 是纯函数，快照决定开哪一页：

| 状态 | 开什么 |
| --- | --- |
| `Licensed` / `Portable` | 什么都不开 |
| `Anonymous` | 登录页（没账号，登记无从挂靠） |
| `SignedIn` | 购买页 |
| `Rejected` / `LicenseUnverifiable` | 提示联系卖家——这两样登录都修不好 |

两个刻意的选择：

**登录成功后直接接购买页。** 两页回答的是同一个问题（「这怎么买」），刚证明自己有微信的
用户不该还要自己找下一步。

**只主动弹一次。** `AppSettings.ActivationGateShown` 记的是「已经弹过」而不是「拒绝过」
也不是「买过」——一个弹窗第二次出现就不再被阅读，常驻入口改放在通用页。任何 upsell 都
不该在每次启动时重复索取注意力。

**这两条是我的判断，不是文档规定的。** 若产品上要求每次启动都提示，把
`ShowAtStartup` 里读 `ActivationGateShown` 那一行去掉即可。

## 购买窗的流程与两处待补

窗口自己多持有一个 `_confirmed`，用来把收款码挡在「用户看过金额和绑定机器」之后：

```
[1] 待确认   机器码（可复制）· 硬件锚摘要 · 开发者微信 · [确认购买]
[2] 待付款   ← 收款码在这一步才出现，配「转账请备注：<机器码>」
[3] 等待卖家  点「我已付款」→ ActivationController.RegisterAsync() → 退避轮询
[4] 完成 / 拒绝 / 失败
```

**确认 ≠ 登记。** 「确认购买」只让收款码露出来，不发任何请求；真正登记发生在用户点
「我已付款」。所以开窗即关不会在卖家台账上留痕。

两处占位符待产品方补：

| 位置 | 现状 |
|---|---|
| `PurchaseWindow.xaml.cs` 的 `SellerWeChat` 常量 | `（待填写）`。单一来源，同时用作售后联系方式与购买条款 |
| `Assets/PaymentCodePlaceholder.png` | 670 字节占位图，替换为卖家收款码 |

**机器码同时是转账备注。** 卖家那头唯一能把一笔微信转账对上一张许可证的线索就是它，
所以窗内给了独立可复制控件（`GateSmall` 样式 + 等宽字体），并明确提示「没备注的转账
需要人工核对，会慢一些」。

## 当前状态：绿（2026-10-19 本机复核）

| 检查 | 结果 |
|---|---|
| `dotnet build -c Release -p:WeChatBridgeEdition=Perpetual` | ✅ 0 警告 0 错误 |
| `dotnet build -c Release -p:WeChatBridgeEdition=Subscription` | ✅ 0 警告 0 错误 |
| `dotnet test -p:WeChatBridgeEdition=Perpetual` | ✅ 580 + 55 通过 |
| `dotnet test -p:WeChatBridgeEdition=Subscription` | ✅ 579 + 55 通过（少一条为 edition 条件测试） |
| `Test-NoNetworkCode.ps1 -Edition Perpetual` | ✅ 白名单内的 `System.Net.Http` 仍是 only Redeem 例外 |
| `WeChatBridge.Windows.exe --background` 实跑 12 秒 | ✅ 常驻不崩，托盘注册成功；`--background` 不开窗，符合设计 |

测试数从 497 + 45 → 580 + 55：新增 80 项（C-1 回归 3、退避 10、契约 29、登录 25、激活 16），
AppTests 45 → 55 是 `git log` 之外同期落地的部分，勿回退。

### `Test-NoNetworkCode.ps1` 的前置条件（本机踩过）

脚本会读 `ShareTarget.dll` 的版本号来确认被测产物确实是 Perpetual 形态。**刚 build 过
Subscription 就跑它会失败**（ShareTarget 版本串还停留在订阅版本号）。先重跑 Perpetual
的 build 再检查：

```powershell
dotnet build windows/WeChatBridge.Windows.sln -c Release -p:WeChatBridgeEdition=Perpetual
powershell -File windows/scripts/Test-NoNetworkCode.ps1 -Edition Perpetual
```

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

- [x] `dotnet build/test` 通过（两形态均绿，见上）
- [ ] 对着 mock 走通：扫码登录 → 机器码展示 → 登记 → 确认 → 取票 → 验签缓存
- [ ] 真服务端签名对下的终态验证（任务 17，走 `AppTests` 已有的 `TestLicenseIssuer`）

未勾选两项都是 **需要人或另一台树** 才能完成的，不是代码缺口：

- mock 全流程要 `server/mock/server.js`，而它**不在这棵树上**（本 worktree 的 `server/`
  只有 `internal/`、`migrations/`，没有 `mock/`），得借主检出或机器 A 的侧鳍。
- 终态验证要 `LicensePublicKeys.PlaceholderKeyV1` 换成真公钥（见下方阻塞项）。

## 红线（禁改）

`windows/src/WeChatBridge.Windows.Core/Licensing/` 下 LicenseResolver.cs / LicenseVerifier.cs / InstallIdentityStore.cs / DeviceAnchor.cs 四个文件——契约级冻结，只读复用。

## 契约登记册 §11.7 收尾情况

| 项 | 状态 | 说明 |
|---|---|---|
| C-1 时间戳转换器 | ✅ 已修 `b822761` | Go 写 unix 秒数字，两处老链路 options 未注册转换器：`Load` 静默返回 null、`Accept` 抛 `JsonException`。各加一行注册 + 3 项回归；临时注掉转换器则红 |
| C-2 base64 字母表 | ⬜ 归机器 A | Go 用 `StdEncoding`，C# 侧宽容才巧合工作。客户端不动，等服务侧收紧后会产生一次联调 |
| M-1 admin 鉴权 | ✅ 无关本侧 | 改 Bearer + OpenID 白名单后，客户端登录拿到的就是 Bearer，正好对上 |

## 发货前必换（代码层最后一处占位符）

`ActivationContract.DefaultBaseAddress = "https://api.wechatbridge.invalid/"`——与
`RedeemEndpoint` 一样的处理：RFC 2606 保留域名，首次请求必定大声失败，而不是悄悄连到
别人的服务器。可用环境变量覆盖，本地与 mock 联调走这条：

```
set WECHATBRIDGE_LICENSE_SERVER=http://127.0.0.1:8901/
```

## 微信登录二维码的错配（2026-10-19 真机联调发现）

**结论：服务端 `/v1/auth/session/:ticket/qr` 返回的码扫不通。要改成浏览器打开 `authorizeUrl`。**

两种二维码不是一回事：

| | 内容 | 扫了会怎样 |
| --- | --- | --- |
| 服务端 `/qr` 渲染的 | `renderQR(session.AuthorizeURL)`，即整个 qrconnect URL | 手机扫 → 微信在**手机里打开那个页面** → 页面在手机上渲染不出 PC 登录 → 提示「不允许在手机中扫码」，回调永远不来 |
| 微信自己发的 | `open.weixin.qq.com/connect/qrcode/<uuid>`，嵌在 qrconnect 页面的 `<img class="js_qrcode_img web_qrcode_img">` 里 | 手机扫 → 微信识别为登录码 → 确认授权 → 回调 `redirect_uri` |

联调证据（服务端日志，2026-10-19 08:39–08:45）：

- 08:39:15 有人用浏览器直接打开 `/v1/auth/wechat/callback?code=xxx&state=ticket`（字面量占位），返回 400 —— **回调端点本身是好的**，伪造 state 被正确拒绝
- 整段时间内**真实回调一次都没有**，nginx 与服务端两侧计数都是 0 —— 微信在跳转之前就中止了
- AppID/Secret 单独验证有效（`cgi-bin/token` 换到 access_token），排除凭据问题
- 最终改用「电脑浏览器打开 `authorizeUrl` → 手机扫微信那张码」，08:44:46 回调成功、令牌交付、激活登记落库

**服务端不需要改**：`authorizeUrl` 一直在响应里（`AccountService.cs:168` 已经解析）。要改的是客户端——`LoginWindow` 现在拿 `qrImageUrl` 调 `FetchQrAsync` 拉 PNG 自己渲染（`LoginWindow.xaml` 的 `QrImage`），这条路径要走系统浏览器打开 `authorizeUrl` 代替。

> 排查时的教训：这个错配在「编译通过 + 单测全绿」下完全看不出来，客户端一直连不上服务端，
> 而服务端日志干净得只剩轮询。任何写着「二维码登录」的端到端流程，都值得拿真机扫一次再标 ✅。

## 阻塞项

无外部阻塞（契约已冻结）。

范围外但影响出口的两项，等机器 A：

- ✅ ~~`LicensePublicKeys.PlaceholderKeyV1` 仍是占位串~~ **已解除（2026-10-19）**：已替换为服务端真实签发公钥。公私钥 DER 逐字节一致，且用服务端私钥签名、客户端公钥验签实测通过。常量名 `PlaceholderKeyV1` 未改（交接中刻意只换值，改名要连带改三处引用）。
- 🟡 `server/mock/` 不在本分支树上，mock 全流程需借主检出跑：`node /d/WeChatBridge/server/mock/server.js --port 8901`，客户端 `export WECHATBRIDGE_LICENSE_SERVER=http://127.0.0.1:8901/`。

### 真实服务端已就位（2026-10-19）

买家侧全链路已用真机跑通：扫码 → 回调 → 双令牌交付 → 激活登记落库（`activation_requests` 1 行 `pending`）。服务端在 `https://ocbot.aixifs.com/v1/*`，配置与密钥在 `/etc/wechatbridge/`，systemd unit `wechatbridge.service`。

客户端连它时注意两点：

- `WECHATBRIDGE_LICENSE_SERVER` **必须带结尾斜杠**。`HttpActivationTransport.cs:46` 专门处理这个：base 不带尾斜杠时 `new Uri("https://host/api", "v1/auth/session")` 会解析成 `https://host/v1/auth/session`，路径段被静默丢弃。
- 库里是 **PostgreSQL 不是 SQLite**。`sqlite3 "$WCB_DATABASE_URL"` 连不上；等价写法 `PGPASSWORD=… psql -h 127.0.0.1 -U wcb -d wcb -tAc "SELECT open_id FROM accounts WHERE nickname='…';"`。
- 卖家后台在 `https://ocbot.aixifs.com/admin/`（静态页，与 `/v1/*` 同源，无 CORS 头也不需要）。卖家身份靠同一个 Bearer + `WCB_ADMIN_OPENIDS` 白名单判定：token 验得过但不在名单上是 **403** 而非 401。

### 签发链路已实测跑通（2026-10-19）

卖家扫码登录 → `POST /v1/admin/confirm` → `activation_requests` 转 `confirmed` → `licence_bindings` 落库 → 买断版 envelope 签出（`exp: null`、`ent: [PRO]`）。

拿**客户端 `LicensePublicKeys.cs` 里内嵌的那把公钥**验签通过，即 `IsPro` 为 true 的三个前提（公私钥配对、canonical 逐字节一致、raw 签名拆分正确）同时成立。

手工验证这张许可证时注意两件事，都会让人误判成事故：

- 签名是 **64 字节 raw `r‖s`**（`internal/license/crypto.go` 的 `signRaw`），不是 DER。`openssl dgst -verify` 要 DER，直接喂会报 `Error verifying data`，得先转 `SEQUENCE{INTEGER r, INTEGER s}`。
- canonical 每行都带 `key=` 前缀（`license.go:115`），无尾随换行，空段用 `-`。凭印象拼很容易少写前缀，那样算出的是不同的字节。
