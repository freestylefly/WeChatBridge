# 机器 C 续跑清单（feat/client-license-gate）

> 本文件是**机器独占**的续跑状态：谁执行一步，谁就把状态和 commit 一起提交。
> 与看板 `client-license-gate.md` 的区别是——看板回答「做到哪了」，本文件回答
> 「下一个动作是什么」，并且只被 `resume-client-license-gate.ps1` 读。
>
> 约定：**每一步结束时工作区必须是绿的，且已提交。** 一步被额度中断时，它的标记仍是
> ⬜，下次运行原样重做，最多损失一步的工作量，不留下半提交状态。

## 状态

⬜ 未做 · ⏳ 进行中 · ✅ 已提交 · 🔴 被外部依赖阻塞

| # | 步骤 | 状态 | commit |
|---|---|---|---|
| S0 | 提交任务 15（门禁接入） | ✅ | `2d994b0` |
| S1 | `PollSchedule` 单测 | ⬜ | — |
| S2 | `ActivationContract` / `TimestampConverter` 单测 | ⬜ | — |
| S3 | `AccountService` 单测 | ⬜ | — |
| S4 | `ActivationService` 单测 | ⬜ | — |
| S5 | `HttpActivationTransport` handler 注入 | ⬜ | — |
| S6 | mock 全流程 + 出口复核 | 🔴 | — |

清单里只剩 🔴 时，计划任务会**自己停用**（`Disable-ScheduledTask`）——没有可执行的步骤
就不该再唤醒机器。要恢复：

```powershell
Enable-ScheduledTask -TaskName WeChatBridge-Resume-LicenseGate
```

用停用而不是删除，是为了让触发器、参数、设置留在原地；恢复只需一行，不必重写最容易
写错的那部分。

## 计划任务是单次触发，不是每日轮询

`WeChatBridge-Resume-LicenseGate` 只触发一次（次日 01:00）。这一次里脚本**循环执行
所有剩余步骤**，每步一个独立的 `claude` 进程：

- **额度各自独立。** 第一步把额度用光，第二步仍能起新进程继续。单进程串行做完五步
  的话，额度一耗尽五步一起卡住。
- **每步以「工作区绿且已提交」收尾**，所以中途断掉只丢当前那一步。
- **连续两步没有进展就停**，否则一个改不出绿状态的步骤会被无限重试，每次还烧一次额度。

只有当本次结束时**仍剩步骤**（撞到 8 小时上限，或额度耗尽），脚本才重排一次次日同一
时刻——这是额度中断的兜底，不是每日轮询。清空即停用，之后不会再唤醒。

**机器要在触发时刻开着。** `StartWhenAvailable` 能补睡眠错过的情况，补不了关机。

## 每一步的硬要求

不论做哪一步，结束前必须全部满足，否则不算完成：

```bash
export PATH="$PATH:/c/Program Files/dotnet"
dotnet test windows/WeChatBridge.Windows.sln -c Release -p:WeChatBridgeEdition=Perpetual --nologo
dotnet test windows/WeChatBridge.Windows.sln -c Release -p:WeChatBridgeEdition=Subscription --nologo
powershell -ExecutionPolicy Bypass -File windows/scripts/Test-NoNetworkCode.ps1 -Edition Perpetual
```

- 两个 edition 都要绿，且 Subscription 恒少一条（edition 条件测试）
- **新文案必须同步进 `Core/Localization/en.json`**，Windows 侧没有测试会替你抓这件事
- **测试工程是 `net10.0` 且只引用 Core**，不要为了方便把 `HttpActivationTransport` 拖进 Core 侧测试
- Core 不得出现任何网络类型，`EditionIsolationTests` 按精确相等钉死三条 `NetworkInformation`
- commit message 带步骤编号：`test(client): [S1] PollSchedule 退避单测`

## 步骤说明

### S0 提交任务 15

代码已完成并全绿，待提交：`ActivationGate.cs` / `App.xaml.cs` / `AppSettings.cs` /
`GeneralPane.xaml(.cs)` / `ActivationGateTests.cs` / `en.json` + 看板。

**需要产品方确认**（已在看板标注，实现按此口径）：门禁不锁任何功能，只引导购买；
只主动弹一次，常驻入口在通用页。

### S1–S2 纯函数，无外部依赖

`PollSchedule` 是退避算术，`TimestampConverter` 同时读 unix 秒与 RFC3339——这两个是
最容易写错又最难被发现的转换，值得钉死。测试放 `tests/WeChatBridge.Windows.Tests/`。

### S3–S4 需要假 transport

`IActivationTransport` 就是为这件事留的接缝，Core 侧有现成的 fake 模式可循
（`RedeemServiceTests` 是最近的先例）。注意 `Accept` 的顺序是承重的：先验签、再核对
subject、最后落盘，三步之间没有 await——测试要能区分「验不过的票据一次都没落盘」。

### S5 handler 注入

`HttpActivationTransport` 在 WPF 工程，测试放 `AppTests`。照 `RedeemChannelTests` 的形。

### S6 有外部阻塞

`LicensePublicKeys.PlaceholderKeyV1` 仍是占位串，真公钥要机器 A 给。在换掉之前：

- 对着 mock 能走到「取票 → 进入 Accept → 验签被拒」，**拿不到 `IsPro=true` 的终态**
  （`server/mock` 的 README 自己写明签名是占位符）
- 真正的落盘链路证明由 **S4** 承担：用 `AppTests` 里已有的 `TestLicenseIssuer` 驱动
  同一个 `ActivationService`

S6 的产出因此是「如实记录边界」，不是「跑通全部」。

## 外部依赖

| 项 | 找谁 | 状态 |
|---|---|---|
| 许可证公钥（SPKI base64） | 机器 A / `feat/server-convergence` | 🔴 未提供 |
| mock 服务 | `D:\WeChatBridge\server\mock`（本分支树上没有） | ✅ 可用 |

跑 mock：

```bash
node /d/WeChatBridge/server/mock/server.js --port 8901
export WECHATBRIDGE_LICENSE_SERVER=http://127.0.0.1:8901/
```
