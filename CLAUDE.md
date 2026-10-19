# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 仓库是什么

一个产品、三个实现，同仓库并行演进：

| 目录 | 技术栈 | 角色 |
| --- | --- | --- |
| `macos/` | SwiftPM（Swift 6 / Swift 5） | macOS 端，主程序 + 12 个 Share Extension |
| `windows/` | .NET 10 + WPF | Windows 端，主程序 + Share Target 助手 + 安装/便携两条分发路线 |
| `server/` | Go 1.26 + gin | 授权服务端（**仅 Windows 版使用；macOS 完全离线，不碰这个服务**） |
| `admin-console/` | 零依赖零构建静态页（UMD + 原生 JS） | 卖家后台。与服务端同源部署，走扫码登录 |
| `shared/` | — | 两端共用的资产与契约，**唯一不允许出现平台特定代码的目录** |
| `site/` | — | 发布物：`appcast.xml`（Sparkle 更新源）+ 逐版本发布说明，由 `pages.yml` 推到 GitHub Pages |

产品分**买断版（Perpetual）**与**订阅版（Subscription）**两种形态，是同一份源码树构建出的两个产品，不是两套代码。当前发布的是买断版免费档。

代码里的注释就是规格。每个决策点几乎都写了「为什么这样、以及不这样会坏在哪」，改动行为时请同步更新注释——其中若干条是承重的（见下）。

## 核心数据流

这是理解整个仓库最重要的一件事，其余细节都是它的分支：

```
微信「合并转发」
   │
   ├─ macOS   Share Extension（沙盒、无网络）─┐
   └─ Windows Share Target（稀疏 MSIX 提供身份）─┤
                                             ▼
                            Inbox/Staging/<batchId>/  ← 校验 ZIP、原子暂存
                                             │  Directory.Move / rename（同卷原子发布）
                                             ▼
                            Inbox/Ready/<batchId>/
                            ├── manifest.json   批次事实（schemaVersion=1, createdAt ISO-8601 UTC Z）
                            ├── intent.json     一次性转发意图，90 秒新鲜度窗口
                            ├── state.json      投递结果回写（delivered/failed/expired/copied）
                            └── files/          原始 ZIP 与媒体
                                             ▼
                            主程序 InboxWatcher → 记录页 + 串行投递队列
                                             ▼
                            激活目标应用 → 写剪贴板 → 聚焦输入框 → ⌘V / Ctrl+V
```

三条贯穿全仓库的规则：

1. **分享侧只负责「收」，不负责「送」。** Share Extension / ShareTarget 只做校验 ZIP → 原子写入 → 剪贴板兜底 → 唤醒主程序。投递一律由主程序串行完成。
2. **剪贴板在投递前先写兜底内容。** 任何失败都必须让用户离一次 Ctrl+V 只有一步（macOS 的 `fallBack(_:)`、.NET 的 `DeliveryEngine.Fail()`）。
3. **intent 一次性：消费即删除。** `ConsumeIntent` 读后即删，所以重扫、重启、第二个窗口都不可能重放一次转发。

## 常用命令

### macOS（需 macOS + Xcode；本文件作者在 Windows 上无法运行这些）

```bash
cd macos
swift test                                    # 全部测试（XCTest，非 Swift Testing）
swift test --filter BatchStateTests           # 单个测试类/方法，正则匹配
swift Scripts/check-localizations.swift       # 中英文资源四重校验（CI 步骤）
Scripts/check-release-config.sh               # Bundle ID / App Group / entitlements / 12 槽位校验
mise run verify                               # build+test+i18n+release-config+bundle 全套
CONFIG=release Scripts/make-app.sh            # 装配 dist/微信流.app（主程序 + 12 个 appex）
Scripts/install-dev-build.sh                  # 装到 ~/Applications，让扩展真正注册
```

### Windows

```bash
# Git Bash 下 dotnet 不在 PATH 上，先 export
export PATH="$PATH:/c/Program Files/dotnet"

# Edition 必须同时传给 restore / build / test，只传给 build 会让测试跑在另一个 edition 上
dotnet restore windows/WeChatBridge.Windows.sln -p:WeChatBridgeEdition=Perpetual
dotnet build   windows/WeChatBridge.Windows.sln -c Release --no-restore -p:WeChatBridgeEdition=Perpetual
dotnet test    windows/WeChatBridge.Windows.sln -c Release --no-build -p:WeChatBridgeEdition=Perpetual
dotnet test    windows/WeChatBridge.Windows.sln -c Release -p:WeChatBridgeEdition=Perpetual \
                 --filter "FullyQualifiedName~DeviceAnchorTests"      # 单个测试类

# 买断版网络命名空间门禁（与 CI 同一条，本地提前跑省事）
powershell -ExecutionPolicy Bypass -File windows/scripts/Test-NoNetworkCode.ps1 -Edition Perpetual
```

### Go 服务端

```bash
cd server
GOTOOLCHAIN=go1.26.9 go build ./... && GOTOOLCHAIN=go1.26.9 go vet ./... && GOTOOLCHAIN=go1.26.9 go test ./...
GOTOOLCHAIN=go1.26.9 go test ./internal/license/ -run TestVerify -v     # 单包单测试
```

**`GOTOOLCHAIN=go1.26.9` 不能省。** `go.mod` 写 `go 1.26`，而发行版装的是 Go 1.22。裸跑 `go test ./...` 会先尝试下载 toolchain，在只能走内部镜像的机器上得到 `toolchain not available`——**这看着像网络问题，实际是版本没选对**：对应 toolchain 早已在 `$(go env GOMODCACHE)/golang.org/toolchain@v0.0.1-go1.26.9.linux-amd64` 里，显式指定即可命中缓存。装 Go 1.26+ 本机后这个前缀可以摘掉。

`go test ./...` 全绿**不需要**真实 PostgreSQL、微信凭据或网络——每个外部依赖都在接口后面，测试用 pgxmock / fake store 顶替。**但 pgxmock 只断言查询字符串，从不执行它**：`store/postgres` 里那些真正要紧的语义——部分唯一索引的并发冲突（`activation_requests_live_install_idx` 是 `UNIQUE … WHERE status='pending'`）、条件 UPDATE 守卫实际影响几行、隐式行序——一行都没被真跑过。覆盖它们是 A-⑧ 的活，需要真库。

#### 本机 PostgreSQL（供 A-⑧ 集成测试）

服务端的持久层需要一个真实例。它跟 CI 无关（CI 根本不测服务端），纯属本地开发依赖。

```bash
apt-get install -y --no-install-recommends postgresql-16 postgresql-client-16
pg_isready -h 127.0.0.1                                    # 16 装完即由 systemd 拉起

# 建角色与库，然后让 server 自己跑迁移（不要手灌 SQL，
# 否则 schema_migrations 台账缺失，启动时会撞 "relation already exists"）
su postgres -c "psql -c \"CREATE ROLE wcb LOGIN PASSWORD 'wcb'\""
su postgres -c "createdb -O wcb wcb"
```

启动需要五个变量，缺一即拒（`config.required`）：

```bash
WCB_DATABASE_URL="postgres://wcb:wcb@127.0.0.1:5432/wcb?sslmode=disable" \
WCB_WECHAT_APP_ID=wxtest WCB_WECHAT_APP_SECRET=secret \
WCB_LICENSE_PRIVATE_KEY_PATH=/path/to/ec-p256.pk8.pem \
WCB_TOKEN_SIGNING_SECRET=<≥32 字节> \
WCB_HTTP_ADDR=:18901 GIN_MODE=release ./cmd/wcb
```

监听地址是 `WCB_HTTP_ADDR`（默认 `:8080`），**不是** `WCB_ADDR`。`WCB_ADMIN_OPENIDS` 留空时 admin 路由整个不挂载（404 而非全信任，见下文）。

**常驻占用实测 85 MB**（PG 16.15 空闲态，6 个进程 RSS 求和，共享内存被重复计入，实际更低）。在 3.6 GiB / 2 vCPU 的机器上约占 `available` 的 3%，不必为它调 `shared_buffers`。

### 契约 mock 与卖家后台（Windows 端的前后端，不在 CI 里）

```bash
node server/mock/server.js --port 8901     # 零依赖 Node，契约 mock；字段/状态码/错误信封与真服务端逐字节一致
cd admin-console && node test/smoke.mjs     # 契约冒烟 21 项
cd admin-console && node test/browser.mjs   # 浏览器级 17 项，需 playwright-core + 本机 Chrome（可选）
```

mock 的诚实边界写在 `server/mock/README.md`：签名与 QR PNG 都是占位，任意非空 bearer/admin 密码都接受。真实 handler 已在 `feat/server-convergence` 落地，**mock 该删了但还留着**——它是当前唯一能在无 PostgreSQL 的机器上跑通客户端全流程的手段。

`admin-console/` 的约束比代码更重要：**页面必须与服务端同源**。服务端不带任何 CORS 响应头，`baseURL` 指向别的源时浏览器在预检阶段就拦掉全部请求，页面只得到一句 `Failed to fetch`。改后端地址改 `js/config.js` 的 `baseURL` 一行；**不要为了省事给管理接口开 `*` 通配 CORS**。

## 架构要点

### macOS：12 个入口 = 1 个二进制

`WeChatBridgeCore`（静态库，Swift 6，带 `-application-extension`）← `WeChatBridgeApp`（Swift 5）+ `WeChatBridgeShare`（Swift 5）。Sparkle **故意不链进 appex**（appex 里的 framework 是第二份要签名公证的东西），`check-release-config.sh` 会 grep `Package.swift` 强制这一点。

macOS 的分享菜单只在构建期由已签名的 appex 条目决定，不能按已安装 App 动态生长。所以「十二个入口」是十二个 **appex 包**，但它们是同一个 `WeChatBridgeShare` 二进制的十二份拷贝——`Scripts/make-app.sh` 遍历 `Scripts/share-slots.sh` 里的槽位表，改写 `CFBundleIdentifier` / `DKShareAction` / `DKAppGroupIdentifier`。`check-release-config.sh` 里 `${#SHARE_SLOTS[@]} -ne 12` 直接硬失败。

**`Core/ShareAction.swift` 的 `bundleIdentifierSuffix` 与 `Scripts/share-slots.sh` 的槽位表是同一张表的两个半边，必须一起读。** 检查脚本双向断言：每个槽位对应真实 action，每个 action 都有槽位。

其他几个不读代码看不出来的点：

- **ShareViewController 必须挂一个 1×1 的 `alphaValue = 0` 视图。** 注释里记着实测史：viewless 的 `NSExtensionRequestHandling` 会在字节已拷贝、批次未提交之间 SIGTRAP。
- **主程序故意不用 SwiftUI `App` 生命周期**，走 `AppDelegate`——防止状态恢复复活一个空窗口。
- **主应用故意不进沙盒，扩展必须进沙盒**，两个不对称都被脚本断言。原因：沙盒内任何 `pluginkit` 调用都必然失败（pkd 拒绝沙盒客户端），而「入口」设置页整个建在 pluginkit 上。
- 串行化在 `ActionRunner.enqueueExclusive`（`private var pending: Task?` 链式 await）。等待队列之后**必须重新检查** `arrival.isFresh`，不能只在读盘时查一次。

### Windows：Core 是零依赖，两个应用都只能单向依赖它

```
WeChatBridge.Windows ──► Core ──► (仅 BCL)
WeChatBridge.ShareTarget ──► Core
                     └────► (Link 主项目的 10 个源文件，无 ProjectReference)
```

`WeChatBridge.Windows.Core` 是 `net10.0`（**不是** `-windows`）、无 WPF、**零 NuGet 引用**——这个保证是显式的，由测试守着。

ShareTarget 从主程序 Link 了 10 个文件（XAML 选入口浮层等），让同一个面板在助手宿主里长得一模一样。代价：AppTests 对两个项目引用都设 `ReferenceOutputAssembly="false"`（否则 CS0433 类型二义），改用 `CopyShareTargetForProcessTests` 复制 exe 跑进程级测试。

**分享注册**：稀疏 MSIX 只提供身份、程序在包外（`uap10:AllowExternalContent`）。`AppxManifest.xml` 的 `DisplayName` 里**绝不能出现连续的「微信 / WeChat / Weixin」**——微信会直接隐藏这类目标，所以全仓库写的是微`<U+2060>`信流。

**keeper / stub 选举**：Windows 每次分享都起一个新助手进程（不上 WindowsAppSDK 无法避免）。`StubForwarder` 先用 400ms 超时试连命名管道，失败就自己当 keeper 常驻；成功则 stub 转发后立刻退出，warm keeper 负责浮层与提交。`--registration-check` 在 `Program.Main` 最前面处理完就退出——它 P/Invoke `GetCurrentPackageFullName` 证伪「注册记录 ≠ 外部 exe 真的有身份」。

### 版本形态：`WeChatBridgeEdition` 是 MSBuild 属性

定义在 `windows/Directory.Build.props`。**不是常量、不是 item**，这样 `build-installer.ps1` 才能按条件反查对应的 `<Version>`。

四个载体（Version / 编译常量 / 产物名 / MSI 包名）在同一个条件 `PropertyGroup` 里，改一个不改其余的会串。`ValidateWeChatBridgeEdition` 对任何别的取值报 **WCB0001**（拼错会静默地用 Perpetual 的源码构建出 Subscription 的名字）。

源码选择在 `Directory.Build.targets` 里用 `Compile Remove="Cloud\**\*.cs"` 等实现——必须是 `.targets` 的 Remove 而不是 `.props` 的条件 Include，因为 `.targets` 在项目体之后、`Sdk.targets` 之前导入，是 Remove 还能作用于 SDK 隐式 glob 的唯一时点。

`WcbEditionStampIsACompileInput` 存在的理由：不这么做，紧跟着构建 Subscription 会把 Perpetual 的程序集留在 `bin` 里戴着新名字，同时静默破坏版本区分和上面的 Remove。

### 服务端：许可证是唯一的信任根

```
auth/      微信扫码登录（redirect 落在服务端，桌面进程永远拿不到 code）
token/     access = HS256 JWT；refresh = 不透明 wcb1_ 串（自包含的 refresh 只能靠「不认」作废）
license/   ECDSA P-256 签发，客户端只嵌 SPKI 公钥
activation/ 买断版客户侧三端点：登记 → 轮询 → 取票（无支付网关）
admin/     卖家控制台（Bearer + OpenID 白名单）
cmd/wcb/   唯一入口：装配、迁移执行、优雅关闭
store/     接口在 store.go，实现在 store/postgres
```

`billing/`（微信支付 Native）与 `redeem/`（兑换码）已随兑换码退役删除（`86abca3 [A-③]`）。**没有支付网关**：买家自己转账，卖家人工核对微信零账后在后台一键发证。

### 客户端侧：Activation 三件套（`windows/`）

`Core/Activation/`（AccountService / ActivationService / PollSchedule / GateState）+ `Windows/Activation/`（LoginWindow / PurchaseWindow / 两个 Controller / `HttpActivationTransport`）。注意 `HttpActivationTransport` 在 **WPF 工程**里而不在 Core——Core 的 `System.Net.*` 白名单只有三条 `NetworkInformation`，放一个 `HttpClient` 进去 `EditionIsolationTests` 当场红。

启动门禁是 `ActivationGate.Decide` 这个**纯函数**：`Licensed`/`Portable` 不开页，`Anonymous` 开登录页，`SignedIn` 直接接购买页，`Rejected`/`LicenseUnverifiable` 提示联系卖家。

**门禁不锁任何功能。** 转发对所有人可用，`LicensingBootstrap.IsShareDeliveryAvailable` 恒为 true 并有测试守着；全仓库没有一处 UI 因 `IsPro` 为 false 而禁用。这是产品决策（买断版卖的是能力不是锁），改之前先看 `GateState.SignedIn` 的文档注释。`ActivationGateway` 全部惰性装配——已授权用户每次启动**连 `HttpClient` 都不构造**，因为隐私承诺写的是「0 次网络请求」。

**签名从不是对 JSON 文本签的**，而是对规范化后的 8 行字符串签（域分隔符 `wechatbridge.license.v1`，空段用 `-` 占位，**无尾随换行**）。ent 排序去重，因为 map 迭代或没写 ORDER BY 的 SELECT 每次返回顺序都不同。

**跨语言的东西一律是复制而非共享**：许可证规范化向量 + SHA-256、兑换码归一化表、时间戳格式、wire 枚举字符串，两端各有一份并各有一对测试。`go test ./...` 全绿**不代表**客户端还验得过——唯一无法被编译器检查的跨语言契约就是这个 canonical 格式，所以两端各自钉死了同一个常量与同一个摘要。

**签名是 64 字节的 raw `r‖s`，不是 DER。** `internal/license/crypto.go` 的 `signRaw` 手工 `FillBytes` 拼两个 32 字节再 base64；`verifyRaw` 的注释写明它自己做 malleability 检查。任何用 `openssl dgst -verify` 之类的通用工具去验这张许可证都会得到 `Error verifying data`——那条命令要 DER（P-256 通常 70–72 字节）。手工验证时先把 raw 转成 `SEQUENCE{INTEGER r, INTEGER s}` 再验，否则会误判成事故。

顺带记下 canonical 的确切形状（`license.go:115`），每行都带 `key=` 前缀，漏掉就会算出不同的字节：

```
wechatbridge.license.v1
v=1
sub=…
plan=…
iat=…
nbf=…
exp=-          ← 空段占位 Absent
ent=PRO        ← 大写、排序、去重后逗号连接
```

无尾随换行。客户端 `IsPro` 恒为 true 的前提是这三件事同时成立：内嵌公钥与签发私钥配对、canonical 逐字节一致、raw 签名被正确拆回 `r‖s`。

## 不看代码看不出来的约定（红线）

**用户数据只有三个根，其余全部由 `AppLayout` 派生。** 新增落盘点必须走它，否则便携版会把一部分状态写到 U 盘、一部分写到本机——这在拔盘之前完全看不出来。`LayoutWiringTests.NoSourceFileResolvesLocalAppDataOutsideTheAllowlist` 守着这条。

**Core 不得命名任何网络类型。** `EditionIsolationTests` 用 `System.Reflection.Metadata` 读编译产物，按**精确集合相等**断言 Core 的 `System.Net.*` TypeRef 只有三条 `NetworkInformation`（离线恢复用的 MAC 锚点）。`Test-NoNetworkCode.ps1` 的白名单已含 `System.Net.Http` / `Http.Headers` / `Primitives`，所以 HTTP 动词放 WPF 工程即可，两个白名单都不用动。脚本里写着「若要加第四条，先停下来」——那是产品决策，不是测试修复。

**本地的中文字符串 .ps1 必须是 UTF-8 with BOM。** PowerShell 5.1 会把无 BOM 的文件按系统 ANSI 代码页（中文 Windows 上是 GBK）读，静默毁掉字符串而不报错。反过来 `.bat` 必须纯 ASCII——cmd 同样按 ANSI 读。`Directory.Build.props` 也带中文，同理。`LayoutWiringTests` 会守住 `FirstRun.ps1` 的头三个字节。

**本地化是单向的。** `L10n.Text("中文")` 的 key 就是中文字符串本身（miss 时原样返回，所以每个用户可见中文字符串都是自己的翻译 key），英文只存在于 `Core/Localization/en.json`（74KB，EmbeddedResource）。XAML 侧用 `{loc:Loc Key='中文'}`。加新文案 = 改代码里的中文 + 在 en.json 补一条，`LocalizationTests` 会断言两边的复合格式占位符索引集合完全一致。

**`tests/…/TempInbox.cs` 是绝不能碰真实 `%LOCALAPPDATA%` 的 fixture。** 所有测试经由它拿一个临时 Inbox 根。

**函数内部的顺序就是正确性论证。** `auth.Service.HandleCallback` 是「先落凭据再完成 ticket」。admin 的三条更细：**envelope 每张许可证只签一次**（ECDSA 每次签随机取 k，重签同一份 claims 会让买家每次轮询拿到不同的 token，store 写一次字节，买家的 `/licence` 端点只回放那份字节）；**confirm 按 requestId 幂等**（两个卖家同时点一次，只铸一张票，第二次读回第一张）；**卖家的身份只来自 token，永不来自 body**。每处都有注释说明，不要「顺手简化」。

**错误码的严格程度不统一是有意的。** `admin` 比它写的时候所对的 mock 更严：未知的 `?status=` 是 400 而不是空列表——卖家把 "confirmed" 打成 "comfirmed" 时被告知「没有待确认的东西」，是这个 API 最不能给出的答案。`auth` 的 state 无效一律 400 且**故意不说是伪造、已用还是过期**，告诉攻击者是哪一种就等于送了他一个探针。上游（微信）不可用是 503 `service_unavailable` 而**不是**「凭据已过期」——ticket 原封不动、浏览器拿到可重试的答案；一次网络抖动把付费用户登出是这个端点能有的最坏故障。

**admin 的 403 不是 401。** 鉴权走买家同一套微信扫码 bearer token，`WCB_ADMIN_OPENIDS` 白名单决定谁在后面。token 验得过但账号不在名单上是 403：凭据没问题，背后的身份不是卖家。空白名单是接线错误，`NewService` 直接拒绝——`cmd/wcb` 干脆不挂载 admin 路由（404 而非全信任）。

## CI 门禁的排序是有意的

`.github/workflows/ci.yml` 的 Windows job 跑 `Perpetual | Subscription` 双矩阵且 `fail-fast: false`（一个腿红不能取消另一个，否则 Subscription 的日志会因 Perpetual 失败而消失——而这恰好是这个矩阵存在的意义）。

**`Test-NoNetworkCode.ps1` 必须排在所有签名/发布步骤之前。** 签名是对一个持有密钥的服务的请求，不是对产物的批处理：一旦买断版二进制上了签名服务，事后再发现版本形态标错就不是重建，而是吊销——签名过的产物已经分发，用户机器上的 Authenticode 签名里已经烙死了版本字符串。这条线以下新增的任何东西都属于「发布候选」，需要先确认吊销预案成立。

**当前 CI 不测 Go 服务端，也不测 `admin-console`**（`ci.yml` 只有 macOS 与 Windows 两个 job）。服务端的全绿、后台的冒烟全绿都只在你本地成立。另有两条不在 `ci.yml` 里的 workflow：`windows-sign.yml`（手动 dispatch，走 SignPath）与 `pages.yml`（`site/**` 推到 GitHub Pages，即 macOS 的 Sparkle 更新源）。

## 三分支并行开发协议

**这轮已经合完了**（`487231c` server、`0762a85` admin，`feat/client-license-gate` 与 `feat/windows-portable-usb` 也都已在 `main` 的祖先里）。协议本身仍是下一次并行开工时的规矩，规则见 `docs/design/progress-sync-protocol.zh-CN.md` 与 `branch-work-assignment.zh-CN.md`：

- **一个分支同一时刻只允许一台机器写。** 交接只能先确认对方 24 小时内不会回来。
- **看板路径按分支隔离**：`docs/progress/{server-convergence,admin-console,client-license-gate}.md`，只有各自主人写，git 层面零冲突。**看板与代码同一个 commit**——「先 push 代码回头补看板」，补的那次永远不来。
- **commit 带任务编号**：`feat(server): [A-③] …`，`git log --oneline` 即进度流水。子任务粒度，不攒大 commit。
- **契约冻结**：license canonical 格式、auth 端点、httpx 错误码体系禁止私改。发现缺字段 → 先改 `docs/design/buyout-flow-convergence.zh-CN.md` §11.3 并推送，各分支跟进。§11.7 是缺陷与变更登记册。
- **工作区用 git worktree 隔离**（历史上真出过未跟踪文件跨分支的事故）。stash 栈在主检出与所有 worktree 间共享，`git stash` / `git stash pop` 会和其他会话抢——用 WIP commit，或 `git stash push -u -m "<唯一标签>"` 后立刻记下 SHA、用 `apply <sha>` 不用 `pop`。
- 沙箱类环境里 git 网络操作会**静默失败或超时**（特征是 `Empty reply from server`，同时 curl 正常——别误判成凭据问题），必须在沙箱外跑。

看板本身就是进度源，别只看文档：`docs/progress/*.md` 里 ⬜ / ⏳ 的行是**代码尚未落地**，`server-convergence.md` 的 A-⑧（全量集成测试）与 `client-license-gate.md` 的 C-⑥ / S5 / S6（mock 全流程）都还没做完。

## 已知缺口（别当成新 bug 去排查）

- **`windows/src` 下不存在 `Cloud/`、`CloudQr/`、`Billing/` 目录。** 版本隔离机制全部建好并被 CI 守着，但订阅形态的源码还没有落地，所以 `Test-NoNetworkCode.ps1` 在 Subscription 下会打印 SKIPPED。
- **`LicensePublicKeys.PlaceholderKeyV1` 是刻意的占位串**，不是忘了填。未替换前**每一张许可证都验不过**，`IsPro` 对所有用户恒为 false——这是 fail-closed 设计，不是 bug。填入服务端 SPKI 公钥是发版阻塞项。
- **`PurchaseWindow.SellerWeChat = "（待填写）"`，收款码静态图也是占位。** 购买窗代码完成但产品方未填，C-③ 因此是 ⬏️。和上面那条一样是发版阻塞项。
- **服务端已无 `POST /v1/license/redeem`，但 C# 侧的 `Redeem/` 还在**（Core 的 `RedeemContract.Path` 指向那个不存在的路由，`App.xaml.cs` 的 `--redeem` 仍会开兑换窗，`en.json` 里还有一整组兑换文案）。兑换码退役只做了服务端，客户端清理没跟上——今天兑换窗能开、能发请求、必然失败。
- **`BuyoutProductID` 与 `httpx.CodeWrongProduct` 目前没有任何消费方**：字段被读进 `Config` 就没人用，错误码定义了也没有处理器返回。两者都是兑换码时代的残留，产品校验随兑换码一起退役了。**若日后重建产品校验，默认值已与客户端 `RedeemContract.ProductId` 统一为 `wechatbridge-windows-buyout`**，但别以为今天存在这条校验路径。
- **Go 与 C# 的许可证信封仍有一处不一致**：base64（Go 用 std）对 base64url（C# `Base64Url`）。目前靠 C# 解码器的宽松映射兜住，实测可 roundtrip；一旦有人给 C# 加严格字母表校验就会当场炸。时间戳那处（int64 vs `DateTimeOffset`）已由 `b822761` 修复。成因与排查路径见 `windows/docs/LICENCE-ENVELOPE-WIRE-CONTRACT.zh-CN.md`。
- **`server/mock/server.js` 该删了但还留着**（`server/mock/README.md` 自己写着「真实 handler 落地后本文件删除」）。它现在是有用的——无 PostgreSQL 时唯一能跑通客户端全流程的东西——但它的签名是占位，envelope 过不了客户端内嵌公钥。
- **`admin-console` 的台账页许可证号列依赖本机 localStorage 日志**，因为冻结契约的 `GET /v1/admin/requests` 不返回 `licenseId`。非本机完成的签发显示「—」，作废得用页面底部的「手动操作」区输许可证号。
- **`Tests/FolderDeliveryTests.swift` 的 11 个测试不在任何 target 里**，`swift test` 从不编译它们。
- **`App.swift` / `ShareEntryProbe.swift` / `AppLink.swift` / `ShareFailure.swift` 引用的 ADR-0005 / ADR-0006 在仓库里不存在。**
- **`ci.yml` 只在 `[main, feat/windows-port]` 上触发**，当前分支不在其中，本地必须自己跑。
