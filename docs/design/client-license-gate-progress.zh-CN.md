# 机器 C 进度与任务分解（feat/client-license-gate）

> 依据：`branch-work-assignment.zh-CN.md` §4（客户端授权门）。
> 本文只增不改，与另两条分支各自独立的进度文档并行。

## 0. 工作区（并发隔离）

两个开发智能体曾共用 `D:\WeChatBridge` 一个工作目录，已经实际发生过一次事故：
机器 C 的未跟踪文件一度落在机器 A 的分支状态里，靠 `git checkout` 抢救回来。

现状改为 git worktree：

| 机器 | 分支 | 目录 |
| --- | --- | --- |
| A | `feat/server-convergence` | `D:\WeChatBridge`（主检出） |
| **C（本分支）** | `feat/client-license-gate` | **`D:\WeChatBridge-C`（worktree）** |

后续所有命令在 `D:\WeChatBridge-C` 下执行。不要 `cd` 回主检出，也不要在主检出里
切到 `feat/client-license-gate`——分支被两个工作区同时检出时 git 会拒绝，而这类
拒绝往往发生在错误的时刻。

## 1. 任务分解与进度

出口标准：`dotnet build/test` 全绿 + 对着 `server/mock` 全流程可走。

| # | 任务 | 状态 | 产出 |
| --- | --- | --- | --- |
| 0 | 读契约 + 跑通 mock（auth / activation / admin 全链路 curl 实测） | ✅ | — |
| 1 | Core：契约 DTO + 错误信封 + 时间戳转换器 | ✅ | `Core/Activation/ActivationContract.cs` |
| 2 | Core：传输抽象（Core 不命名任何网络类型） | ✅ | `Core/Activation/IActivationTransport.cs` |
| 3 | Core：轮询退避（纯算术，可单测） | ✅ | `Core/Activation/PollSchedule.cs` |
| 4 | Core：令牌持久化（DPAPI） | ✅ | `Core/Activation/AuthTokenStore.cs` |
| 5 | Core：扫码登录 + 续期 | ✅ | `Core/Activation/AccountService.cs` |
| 6 | Core：登记 / 查状态 / 取票 / 验签落盘 | ✅ | `Core/Activation/ActivationService.cs` |
| 7 | Core：客户端状态机单一取值 | ✅ | `Core/Activation/GateState.cs` |
| 8 | App：HTTP 动词（唯一允许命名 `System.Net.Http` 的第二个文件） | ✅ | `Windows/Activation/HttpActivationTransport.cs` |
| 9 | App：惰性装配 + 服务地址解析 | ✅ | `Windows/Services/ActivationGateway.cs` |
| 10 | App：登录控制器（UI 无关状态机，照 `RedeemController` 的形） | ⬜ | `Windows/Activation/AccountController.cs` |
| 11 | App：购买控制器（登记 + 轮询 + 取票） | ⬜ | `Windows/Activation/ActivationController.cs` |
| 12 | UI：登录窗（QR 显示 / 轮询 / 过期 / 重试） | ⬜ | `Windows/Activation/LoginWindow.xaml(.cs)` |
| 13 | UI：购买窗（收款码静态图 / 机器码 / 登记 / 轮询 / 取票） | ⬜ | `Windows/Activation/PurchaseWindow.xaml(.cs)` |
| 14 | 机器码展示（installId 主键 + 锚摘要辅证，D2 建议值） | ⬜ | 含在 13 内，独立可复制控件 |
| 15 | 启动门禁接入（未登录→登录页；已登录未授权→限制模式） | ⬜ | `App.xaml.cs` + `Panes/GeneralPane.xaml` |
| 16 | 新文案进 `en.json`（`LocalizationTests` 断言中英逐条对应） | ⬜ | `Core/Localization/en.json` |
| 17 | Core 单测：退避 / 时间戳 / 登录 / 激活 | ⬜ | `tests/WeChatBridge.Windows.Tests/Activation*Tests.cs` |
| 18 | AppTests：`HttpActivationTransport` handler 注入 | ⬜ | `tests/WeChatBridge.Windows.AppTests/` |
| 19 | 出口：全量 build/test + 对着 mock 走通 | ⬜ | — |

## 2. UI 层的既有约定（第 10~16 步照此写）

- 窗口壳：chromeless + `WindowChrome`（`CaptionHeight=52` / `CornerRadius=12` /
  `GlassFrameThickness=0` / `ResizeBorderThickness=0` / `UseAeroCaptionButtons=False`）。
  **不要用 `AllowsTransparency`**——会关掉硬件加速和 DPI，代码注释里写明是禁用的。
- 每个窗口自带局部样式（`…Button` / `…Primary` BasedOn `SettingsButton` /
  `PrimaryButton`，`Heading` / `Body` / `Card`），**不要往 `AppTheme.xaml` 里加**。
- 用户可见字符串一律 `{loc:Loc Key='中文'}`（XAML）或 `L10n.Text("…")`（C#），
  并在 `Core/Localization/en.json` 补英文条目。
- 一个 `Render()` 收口，控制器 `Changed` 事件驱动；同步点击处理器写
  `private void OnXxx(...) => _ = DoAsync();`，异步体是 `private async Task`。
- 轮询用控制器内的 `Task.Delay(退避, ct)`，UI 侧不用 `DispatcherTimer`
  （控制器已经自己管节奏，UI 只渲染状态）。
- 图片从字节构造后**必须 `Freeze()`**，否则跨线程会炸。

## 3. 必须守住的边界

- **红线四个文件禁改**：`LicenseResolver.cs` / `LicenseVerifier.cs` /
  `InstallIdentityStore.cs` / `DeviceAnchor.cs`（既有验签链，契约级冻结）。
  本分支迄今未碰。
- **Core 不得出现任何网络类型**。`EditionIsolationTests` 按**精确相等**断言 Core 的
  `System.Net.*` TypeRef 只有三条 `NetworkInformation`。`Test-NoNetworkCode.ps1`
  的白名单已含 `System.Net.Http` / `Http.Headers` / `Primitives`，所以 HTTP 动词放
  WPF 工程即可，**两个白名单都不用动**。那个脚本里写着「若要加第四条，先停下来」——
  那是产品决策，不是测试修复。
- **转发功能不受许可证门禁**。`LicensingBootstrap.IsShareDeliveryAvailable`
  恒为 true，有测试守着；限制模式不得碰它。
- **`--background` 实例保持静默**：无窗口、无人机弹出。
- **不做自动重试的网络请求**。`PendingRedemptionStore` 的「先落盘再发第一个字节」
  是同一原则的另一种写法。

## 4. 已发现并上报的两处跨分支契约缺陷

按 §11.3 的规则「发现缺字段 → 先改契约文档 → 机器 A 跟进实现 → 禁止各自私改」，
这里只记录，不在本分支改服务端或冻结文件。

### 4.1 许可证信封的时间戳：Go 是 int64，C# 是 DateTimeOffset

`server/internal/license/license.go` 的 `Envelope` 把 `iat` / `nbf` / `exp` 声明为
`int64`（unix 秒）；客户端 `Core/Licensing/LicenseEnvelope.cs` 同名 JSON 字段是
`DateTimeOffset`。`System.Text.Json` 不会把 JSON 数字读进 `DateTimeOffset`，所以
**既有 `RedeemService.Accept`（`Core/Redeem/RedeemService.cs:265`）对着真服务端会抛
`JsonException`**；mock 用的是同一套形状，同样会抛。

本分支的新激活链路用 `ActivationJson.Options`（带 `TimestampConverter`）绕开了，
激活页因此能走通；但兑换码那条老路没动，仍然是坏的。修法有两条，需要拍板：

- 服务端改成写 RFC3339 字符串（`Canonical()` 本来就只用 unix 秒签，改 wire 不改签名）；
- 或客户端在 `LicenseEnvelope` 上加 unix 秒转换器（会动冻结文件，需机器 C 之外的
  统一决定）。

倾向第一条：wire 格式属于签发方，且改 wire 不影响任何已发票据。

### 4.2 签名字母表：Go 用标准 base64，客户端按 base64url 解

`license.go` 的 `Signer.Issue` 写 `base64.StdEncoding`；`LicenseVerifier.cs:68` 走
`Base64Url.TryDecode`。64 字节随机签名的标准 base64 含 `+` / `/` 的概率约 12%，且必然
以 `=` 结尾——所以即使时间戳问题解决了，验签也会随机失败。mock 同样用标准 base64。

同 4.1，建议服务端改（`base64.RawURLEncoding`），因为客户端的规范化与验签链是冻结的。

### 4.3 对出口标准的影响（如实记录）

`server/mock` 的 README 自己写明签名是占位符，「the licence envelope will NOT verify
against the client's embedded public key」。所以对着 mock 能验证到「取票 → 进入
Accept → 验签被拒」为止，**拿不到 IsPro=true 的终态**——这不是本分支的 bug，是 mock
按设计的边界。真正证明落盘链路正确的是用真密钥对（`AppTests` 里已有 `TestLicenseIssuer`）
驱动同一个 `ActivationService` 的单测（任务 17）。

另外 `LicensePublicKeys.PlaceholderKeyV1` 至今还是占位串，未替换前真服务端签的票
也验不过。这是发版阻塞项，需机器 A 提供公钥。

## 5. 命令备忘

```bash
export PATH="$PATH:/c/Program Files/dotnet"      # dotnet 不在 bash PATH 上

# 契约 mock（分支 A 上有，本分支没有这棵树，用 worktree 跑）
node /d/WeChatBridge/server/mock/server.js --port 8901

# 本分支指定到 mock（dev 用，不进代码）
export WECHATBRIDGE_LICENSE_SERVER=http://127.0.0.1:8901/

# build / test
dotnet build   windows/WeChatBridge.Windows.sln -c Release
dotnet test    windows/WeChatBridge.Windows.sln -c Release

# 网络命名空间门禁
powershell -ExecutionPolicy Bypass -File windows/scripts/Test-NoNetworkCode.ps1 -Edition Perpetual

## 6. 校准（2026-10-18，追加，不改上文）

本文 §1 的状态列写于 `4ce2420` 之后、`d48827f` 之前，未随该提交更新。核对代码后的实际状态：

| # | 任务 | §1 记的 | 实际 |
|---|---|---|---|
| 10 | `AccountController` | ⬜ | ✅ `d48827f` |
| 11 | `ActivationController` | ⬜ | ✅ `d48827f` |
| 12 | 登录窗 | ⬜ | ✅ 已修好并编译通过，但**尚未提交** |
| 14 | 机器码展示 | ⬜ | ⏳ `ActivationGateway` 已暴露 `InstallId` / `AnchorDigest`，UI 未做 |
| 16 | `en.json` | ⬜ | ⬜ `扫码登录` / `登录二维码` / `正在获取二维码…` / `扫码链接` 四条均未进表 |
| 17 | Core 单测 | ⬜ | ⬜ 无任何 `*Activation*Tests.cs` |
| 18 | AppTests 注入 | ⬜ | ⬜ |

其余 0–9 与 13/15/19 与 §1 一致。

## 7. 编译修复记录（2026-10-18）

登录窗带着三处编译错误进来，本机修掉后 build/test 全绿（Perpetual 497+45、
Subscription 496+45、`Test-NoNetworkCode.ps1` 通过）：

1. **`LoginWindow.xaml:49-56`** — Border 塞了 `QrImage` 与 `QrPlaceholder` 两个子元素，
   WPF 的 Border 只接受一个。图片与占位文字本就要叠放切换，套一层 Grid。
2. **`LoginWindow.xaml.cs:128`** — `BitmapFrame` 没有 `CanFree`。改成直接 `Freeze()`。
3. **`LicensingCanonicalPayloadTests.SharedCanonicalVector`** — 见下，这条最值得记。

### ⚠️ 钉死字节的向量不能直接放源文件字面量

`TheClientProducesTheSharedVectorExactly` 红：期望含 `\r\n`，实际 `\n`。

**不是生产代码的错。** 同文件的 `TheSharedVectorHasTheDocumentedShapeAndDigest` 用
`Vector().Bytes`（生产输出）断言 109 字节与摘要 `0897d8…`，一直绿——证明生产端
canonical 与 Go 侧钉死的摘要一致。

真因是 `core.autocrlf=true` 且仓库**没有 `.gitattributes`**（`git ls-files --eol` 为
`i/lf w/crlf`）。C# raw string literal 保留文件行尾，于是 Windows 检出的字面量每个
换行都是 `\r\n`。

这类失败最阴的地方：**签名照样完全有效、字段照样全解析、Pro 标记照样同时熄灭**，
只是客户端和服务端永远差那一个字节。凡是钉字节的向量（许可证 canonical、兑换码
归一化表、时间戳格式）都要防这一手——要么归一化，要么加 `.gitattributes` 强制
`*.cs text eol=lf`（后者是仓库级决定，需与另两条分支协调）。

### 更正 §1 任务 16 的一处误解

§1 写「`LocalizationTests` 断言中英逐条对应」。**Windows 侧并没有这样的测试**——
`LocalizationTests` 只把 `en.json` 跟它自己比对（校验复合格式占位符索引集合），
不扫源码。`LayoutWiringTests` 虽然扫 `.cs`/`.xaml`，但只查 `LocalApplicationData` /
`portable.flag` / `CoordinationSuffix` / `FirstRun.ps1` BOM。

所以任务 16 漏掉新文案**不会打红 CI**，只会让英文用户看到中文。是真实功能缺口，
但不会自己暴露——macOS 的 `check-localizations.swift` 才有源码扫描，那是 macOS 侧的事。

### 仍未守住的边界（补测试时注意）

§3「Core 不得出现任何网络类型」目前是守住的，但任务 17 一旦开始补测试：测试工程是
`net10.0` 且只引用 Core，不要为了方便把 `HttpActivationTransport` 拖进来。

看板 `docs/progress/client-license-gate.md` 已按此校准。
