# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 仓库是什么

一个产品、三个实现，同仓库并行演进：

| 目录 | 技术栈 | 角色 |
| --- | --- | --- |
| `macos/` | SwiftPM（Swift 6 / Swift 5） | macOS 端，主程序 + 12 个 Share Extension |
| `windows/` | .NET 10 + WPF | Windows 端，主程序 + Share Target 助手 + 安装/便携两条分发路线 |
| `server/` | Go 1.26 + gin | 授权服务端（**仅 Windows 版使用；macOS 完全离线，不碰这个服务**） |
| `shared/` | — | 两端共用的资产与契约，**唯一不允许出现平台特定代码的目录** |

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
go build ./... && go vet ./... && go test ./...
go test ./internal/license/ -run TestVerify -v     # 单包单测试
```

`go test ./...` 全绿**不需要**真实 PostgreSQL、微信凭据或网络——每个外部依赖都在接口后面，测试用 pgxmock / fake store 顶替。

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
auth/    微信扫码登录（redirect 落在服务端，桌面进程永远拿不到 code）
token/   access = HS256 JWT；refresh = 不透明 wcb1_ 串（自包含的 refresh 只能靠「不认」作废）
license/ ECDSA P-256 签发，客户端只嵌 SPKI 公钥
billing/ 微信支付 Native 下单（订阅形态）
redeem/  兑换码（买断形态）
store/   接口在 store.go，实现在 store/postgres
```

**签名从不是对 JSON 文本签的**，而是对规范化后的 8 行字符串签（域分隔符 `wechatbridge.license.v1`，空段用 `-` 占位，**无尾随换行**）。ent 排序去重，因为 map 迭代或没写 ORDER BY 的 SELECT 每次返回顺序都不同。

**跨语言的东西一律是复制而非共享**：许可证规范化向量 + SHA-256、兑换码归一化表、时间戳格式、wire 枚举字符串，两端各有一份并各有一对测试。`go test ./...` 全绿**不代表**客户端还验得过——唯一无法被编译器检查的跨语言契约就是这个 canonical 格式，所以两端各自钉死了同一个常量与同一个摘要。

## 不看代码看不出来的约定（红线）

**用户数据只有三个根，其余全部由 `AppLayout` 派生。** 新增落盘点必须走它，否则便携版会把一部分状态写到 U 盘、一部分写到本机——这在拔盘之前完全看不出来。`LayoutWiringTests.NoSourceFileResolvesLocalAppDataOutsideTheAllowlist` 守着这条。

**Core 不得命名任何网络类型。** `EditionIsolationTests` 用 `System.Reflection.Metadata` 读编译产物，按**精确集合相等**断言 Core 的 `System.Net.*` TypeRef 只有三条 `NetworkInformation`（离线恢复用的 MAC 锚点）。`Test-NoNetworkCode.ps1` 的白名单已含 `System.Net.Http` / `Http.Headers` / `Primitives`，所以 HTTP 动词放 WPF 工程即可，两个白名单都不用动。脚本里写着「若要加第四条，先停下来」——那是产品决策，不是测试修复。

**本地的中文字符串 .ps1 必须是 UTF-8 with BOM。** PowerShell 5.1 会把无 BOM 的文件按系统 ANSI 代码页（中文 Windows 上是 GBK）读，静默毁掉字符串而不报错。反过来 `.bat` 必须纯 ASCII——cmd 同样按 ANSI 读。`Directory.Build.props` 也带中文，同理。`LayoutWiringTests` 会守住 `FirstRun.ps1` 的头三个字节。

**本地化是单向的。** `L10n.Text("中文")` 的 key 就是中文字符串本身（miss 时原样返回，所以每个用户可见中文字符串都是自己的翻译 key），英文只存在于 `Core/Localization/en.json`（74KB，EmbeddedResource）。XAML 侧用 `{loc:Loc Key='中文'}`。加新文案 = 改代码里的中文 + 在 en.json 补一条，`LocalizationTests` 会断言两边的复合格式占位符索引集合完全一致。

**`tests/…/TempInbox.cs` 是绝不能碰真实 `%LOCALAPPDATA%` 的 fixture。** 所有测试经由它拿一个临时 Inbox 根。

**函数内部的顺序就是正确性论证。** `HandleCallback` 是「先落凭据再完成 ticket」、`HandleNotify` 是「验签→解密→查单→核对金额→MarkPaid」、`redeem` 是「先签后烧」。每处都有注释说明，不要「顺手简化」。

**回调里返回 200 等于丢钱。** 未处理的回调一律非 200（未知订单 500、金额不符 400、验签失败 401）；已支付的重投回调才回 200 SUCCESS。`trade_state` 非 SUCCESS 时回 200 成功体但**什么都不标记**——这是唯一能既停掉重投循环又不误付的组合。

**拒绝是一种功能。** 状态错、码错、上游不可用是三个不同的 typed error；refresh 的 `transient` 必须是 HTTP 503 且带错误体，绝不能返回 `status:"expired"` 的 token 形状体——一次网络抖动把付费用户登出是这个端点能有的最坏故障。

## CI 门禁的排序是有意的

`.github/workflows/ci.yml` 的 Windows job 跑 `Perpetual | Subscription` 双矩阵且 `fail-fast: false`（一个腿红不能取消另一个，否则 Subscription 的日志会因 Perpetual 失败而消失——而这恰好是这个矩阵存在的意义）。

**`Test-NoNetworkCode.ps1` 必须排在所有签名/发布步骤之前。** 签名是对一个持有密钥的服务的请求，不是对产物的批处理：一旦买断版二进制上了签名服务，事后再发现版本形态标错就不是重建，而是吊销——签名过的产物已经分发，用户机器上的 Authenticode 签名里已经烙死了版本字符串。这条线以下新增的任何东西都属于「发布候选」，需要先确认吊销预案成立。

**当前 CI 不测 Go 服务端**（只有 macOS 与 Windows 两个 job）。服务端的全绿只在你本地成立。

## 三分支并行开发协议

当前开发走的是「3 台机器各领一条分支，fork 是唯一共享通道」的协议，规则见 `docs/design/progress-sync-protocol.zh-CN.md` 与 `branch-work-assignment.zh-CN.md`：

- **一个分支同一时刻只允许一台机器写。** 交接只能先确认对方 24 小时内不会回来。
- **看板路径按分支隔离**：`docs/progress/{server-convergence,admin-console,client-license-gate}.md`，只有各自主人写，git 层面零冲突。**看板与代码同一个 commit**——「先 push 代码回头补看板」，补的那次永远不来。
- **commit 带任务编号**：`feat(server): [A-③] …`，`git log --oneline` 即进度流水。子任务粒度，不攒大 commit。
- **契约冻结**：license canonical 格式、auth 端点、httpx 错误码体系禁止私改。发现缺字段 → 先改 `docs/design/buyout-flow-convergence.zh-CN.md` §11.3 并推送，各分支跟进。
- **工作区用 git worktree 隔离**（历史上真出过未跟踪文件跨分支的事故）。stash 栈在主检出与所有 worktree 间共享，`git stash` / `git stash pop` 会和其他会话抢——用 WIP commit，或 `git stash push -u -m "<唯一标签>"` 后立刻记下 SHA、用 `apply <sha>` 不用 `pop`。
- 沙箱类环境里 git 网络操作会**静默失败或超时**（特征是 `Empty reply from server`，同时 curl 正常——别误判成凭据问题），必须在沙箱外跑。

看三块总览：

```bash
git fetch --all --quiet && for b in server-convergence admin-console client-license-gate; do \
  echo "=== $b ==="; git log --oneline -3 origin/$b; done
```

## 已知缺口（别当成新 bug 去排查）

- **`server/` 是纯库，没有 `main.go`、没有 `cmd/`、没有迁移执行器。** 全仓库找不到一个调用 `config.Load` / `postgres.New` / `RegisterRoutes` 的地方——服务现在起不来，装配是分支 A 的 A-⑦。
- **`windows/src` 下不存在 `Cloud/`、`CloudQr/`、`Billing/` 目录。** 版本隔离机制全部建好并被 CI 守着，但订阅形态的源码还没有落地，所以 `Test-NoNetworkCode.ps1` 在 Subscription 下会打印 SKIPPED。
- **`LicensePublicKeys.PlaceholderKeyV1` 是刻意的占位串**，不是忘了填。未替换前**每一张许可证都验不过**，`IsPro` 对所有用户恒为 false——这是 fail-closed 设计，不是 bug。填入服务端 SPKI 公钥是发版阻塞项。
- **`server/mock/` 当前不在磁盘上**（在分支 A 上），`docs/design/` 里几处提到它的命令现在跑不通。
- **服务端 `WCB_BUYOUT_PRODUCT_ID` 默认值 `windows-buyout` 与客户端硬编码的 `wechatbridge-windows-buyout` 不一致**，用默认值会导致每一次兑换都返回 422 `wrong_product`。
- **客户端 `ActivationContract` 声明的 `v1/activation/*` 三个端点在 Go 服务端还不存在**——那是收敛设计的新 API，机器 A 的活。
- **Go 与 C# 的许可证信封有两处已知不一致**（时间戳 int64 vs `DateTimeOffset`、base64 vs base64url），记录在 `docs/design/client-license-gate-progress.zh-CN.md` §4。按协议「只记录不私改」，新激活链路用 `ActivationJson.Options` 绕开了，老的兑换码路径仍然是坏的。
- **`Tests/FolderDeliveryTests.swift` 的 11 个测试不在任何 target 里**，`swift test` 从不编译它们。
- **`App.swift` / `ShareEntryProbe.swift` / `AppLink.swift` / `ShareFailure.swift` 引用的 ADR-0005 / ADR-0006 在仓库里不存在。**
- **`ci.yml` 只在 `[main, feat/windows-port]` 上触发**，当前分支不在其中，本地必须自己跑。
