# 买断版授权体系分析：客户端-服务端边界、故障影响与离线签发替代方案

> 记录时间：2026-10-18
> 代码基线：`server/`（Go，工作区未提交）、`windows/src/WeChatBridge.Windows.Core/Licensing/`
> 结论先行：
> 1. **机器码只是绑定锚点，不是授权凭据**——买断制的授权凭据是服务端 ECDSA 签发的许可证票据，所以必须存在一个「发证机关」。
> 2. **日常认证完全离线**——服务器故障不影响已激活用户，只影响首次兑换、支付完成、客服解绑这三个一次性时刻。
> 3. **上门现场签发在验签层与在线模式等效**（客户端零改动），但安全模型从「密码学 + 全局账本」退为「密码学 + 物理保管」，必须配 HSM、领用核销、事后补录三件事。

---

## 1. 为什么买断制需要客户端 + 服务端

### 1.1 机器码的真实角色：绑定锚点

客户端有两层「机器标识」，都**不是**授权凭据：

| 标识 | 来源 | 用途 |
| --- | --- | --- |
| `installId` | 首次运行生成 16 字节 CSPRNG 随机数（`InstallIdentityStore.cs:58/89`），**不从硬件计算** | 许可证票据的签名 subject（`license.go` 的 `sign()`） |
| `DeviceAnchor` | 固件型号 + 物理网卡 MAC + OS 名（`DeviceAnchor.cs`）。刻意弃用 `MachineGuid`——它是操作系统身份，重装即失效（`DeviceAnchor.cs:35-39`） | 仅作换机/重装恢复判据，三档证据 `SameMachine / NoEvidence / DifferentMachine` |

也就是说：机器码决定「这张票钉在哪台机器上」，不决定「这张票有没有效力」。

### 1.2 服务端独有的三件事

**① 私钥签发。** 许可证票据是 ECDSA P-256 签名 envelope，客户端只内嵌公钥（编译期常量，`LicenseVerifier.cs:26-29`），负责离线验签；签发私钥只能放在客户端够不到的地方（`license.go:170-172`：*"this key must live somewhere the client cannot reach — the server's secret store, never this repository"*）。若允许客户端本地「验机器码 → 发证」，等于把铸币权交到 exe 里，逆向即可自签任意许可。

**② 一次性烧码。** `BurnRedeemCode` 是 `UPDATE ... WHERE redeemed_at IS NULL` 的条件更新（`store/store.go` 注释：*"two concurrent attempts cannot both win"*），保证一个兑换码全局只换一本许可，「在另一台用过了」与「在两台同时用」因此可区分（`redeem/service.go:299-322`）。这个全局唯一状态只能存在于服务端数据库。

**③ 支付对账。** 「付过钱」这个事实只有服务端知道：微信支付异步通知 → `MarkPaid` 条件置已付（`billing/service.go:534`）→ `IssueOrderLicense` 发票据 → 交付兑换码（`billing/service.go:447/482`）。客户端无从判断。

### 1.3 早期草案的教训

`redeem/service.go` 包注释记录了被否掉的「key 激活」方案四宗罪：泄漏的 key 成为永久铸币权；「另一台用」与「两台用」不可区分；响应丢失不可恢复；没有 burned 状态客服无可引用。现行 burn + 幂等（requestId 重放，`redeem/service.go:213-232`）设计正是对这四点的回应。

---

## 2. 服务器故障的影响分层

### 2.1 日常认证零网络

每次启动的解析链路（`LicenseResolver.Resolve()`，`LicenseResolver.cs:66-149`）：

```
读 license.json → ECDSA 验签（LicenseVerifier：No network, no files）
  → DPAPI 身份 blob 能打开且 sub == 本机 installId？
      ├─ 是 → 放行（Matched，连硬件锚都不读，invariant 1）
      └─ 否（重装/换机）→ 比对 device-anchor.json
            ├─ SameMachine / NoEvidence → 本地恢复放行（Adopted / Recovered）
            └─ DifferentMachine → 拒绝（全层唯一拒绝路径）
```

买断票据 `exp = null` 永久有效（`license.go:83-85`：*"a buy-out licence has no expiry and that absence is the product"*）。**已购用户每次启动 0 次网络请求。**

### 2.2 真正需要联网的三个时刻

| 时刻 | 依赖 | 服务器故障后果 |
| --- | --- | --- |
| 首次兑换（`POST /v1/license/redeem`） | 烧码 + 签名 | 新用户付了钱激活不了 |
| 支付完成（下单/异步通知） | 微信支付对账 | 购买流程中断（通知会重推，不丢单） |
| 客服解绑 / 延长码 / 作废 | 管理端 + 数据库 | 支持动作暂停 |

### 2.3 换机与重装

DPAPI blob 重装后打不开 → 退硬件锚三档证据：`SameMachine`/`NoEvidence` 均本地恢复（「无证据不是罪」——`LicenseResolver.cs:134-141`：为重装用户丢掉产品是这一层唯一不能犯的错）；只有整机迁移到另一台物理机才拒绝，且该拒绝**同样不依赖服务器**。

---

## 3. 上门现场签发（离线授权）替代方案

### 3.1 隐含前提：现场生成的「码」必须是签名票据本身

架构里有两样东西，不可混淆：

- **兑换码**（`WCB-XXXXX-…`）：一张取票凭证，无授权能力，须回服务端 burn 换票。若现场只生成这个，客户端最终仍要联网，**方案自我矛盾**。
- **许可证票据**（`licenseToken` / envelope）：ECDSA 签名串，即授权本身。只有现场直接产出它，离线授权才成立。

### 3.2 两种签发扬对比

| 维度 | 服务器兑换（现行） | 上门现场签发（提案） |
| --- | --- | --- |
| 客户端验签 | ECDSA + 内嵌公钥 | **完全相同，代码零改动** |
| 一次性保证 | 数据库条件更新，全局唯一 | 物理 custody + 客户端 DPAPI/`sub` 绑定兜底 |
| 私钥位置 | 服务端 secret store | 现场 HSM / 密码卡，随人出诊 |
| 断网可用 | 首激活需联网 | 完全离线 |
| 撤销 / 作废 | 服务端改状态 | 基本不可撤销（除非客户端公钥黑名单） |
| 客服解绑 | 数据库操作 | 线下处理 |
| 审计 | 自动落账 | 人工台账 + 事后补录 |
| 支付对账 | 微信支付闭环 | 线下收款，需财务流程补位 |

注意：**票据级「一次性」客户端已经保证**——`sub` 绑死本机 installId，DPAPI 保护的 identity blob 换机打不开，硬件锚拒绝 `DifferentMachine`。现场签发的票被拷到第二台机器也用不了。丢失的只是「码级」全局防重放与审计。

### 3.3 适用场景

- 离网 / 内网隔离环境（工厂、涉密、政企）——服务器根本进不去，唯一可行解；
- 客户强要求数据不出内网——现场签票据文件（U 盘导入）比劝客户架服务端容易；
- 展会 / 试点 / 一次性批量部署——出发前预签 N 个票据。

### 3.4 三项必备配套（否则等于把私钥送出保险库）

1. **现场签必须用 HSM / 密码卡**（YubiKey PIV、国产密码卡等），PIN 双人分管，出发登记、回来核销；
2. **领用核销制度**——签了几张、哪几张、给哪个客户，纸质或离线台账；
3. **回公司后补录服务端账本**（installId + licenseID 对账），审计与客服解绑才有着落。

### 3.5 落地路径

`license.Signer` 本就独立（`NewSigner(key)`），`redeem.Service` 只是调用方之一；canonical 签名格式由 `TestSharedCanonicalVector` 在 Go 与 C# 两侧测试钉死（`license.go:14-21`）。离线签发器 = 复用 `license` 包写一个 CLI：接 HSM → 输入 installId + entitlements → 输出 base64url token。**服务端兑换链路与客户端均零改动。**

---

## 4. 已知未闭合的设计缺口

1. **撤销机制缺失**：现场签发（或任何已发出的票据）无法作废，除非客户端引入公钥黑名单/CRL 并定期刷新——与「离线验签」存在内在张力。
2. **错误文案断层**：`httpx.ErrorBody` 只有 code/message/request_id，没有 `reason` 字段；客户端的 `RedeemReason`（camelCase）拿不到精确文案，退化为通用提示（`redeem/handler.go:16-23` 自述）。修复需两侧同改。
3. **管理端鉴权未定义**：`RegisterAdmin` 的 `Authenticator` 为注入式，nil 时拒绝一切（`redeem/handler.go:63-72`）；谁来充当 admin、角色模型如何，尚无设计。

---

## 5. 附：服务端完成度盘点（2026-10-18）

已完成（约 12,300 行 Go，`server/` 未提交）：

- 配置加载与 prod 强校验（`config/config.go`，含 `PaymentConfigured` 半配拒绝）
- 数据库 schema：12 张表（`migrations/0001_init.up.sql`）
- 许可证签发 / 验签、canonical 格式双向钉死（`license`）
- 兑换烧码：幂等重放、设备槽、并发互斥、同机重签（`redeem`）
- 管理端发码 / 延长 / 作废（`RegisterAdmin`）
- 微信扫码登录、双令牌（`auth` / `token`）
- 微信支付下单 / 通知 / 二维码（`billing` / `wechat` / `qr`）
- Postgres store 实现（`store/postgres`）
- 测试：auth / license / redeem / token / wechat / store.postgres 全过；**billing 测试编译失败**（`fake_test.go:195` 的 `fakeStore` 缺 `BurnRedeemCode` 方法）

差距：

| # | 事项 | 严重度 | 说明 |
| --- | --- | --- | --- |
| G1 | **无 `cmd/main.go` 入口** | 🔴 阻塞 | 各包 `Register` 均在等人装配：config → store → signer → services → gin engine → http.Server。不解决则服务无法启动 |
| G2 | billing 测试编译失败 | 🔴 | `fakeStore` 补 `BurnRedeemCode` 方法即可（约 3 行） |
| G3 | 运维件缺失 | 🟡 | 无 `/healthz`、无 Dockerfile / Makefile、迁移执行工具未定（go.mod 无 migrate 依赖）、无优雅关闭 |
| G4 | 管理端 admin 模型 | 🟡 | `Authenticator` 注入点已留，角色设计未做 |
| G5 | 端到端未联调 | 🟡 | 无真实微信支付沙箱验证、无真实兑换链路实录 |
| G6 | 公网依赖 | ⚪ | 微信支付要求公网 HTTPS（`PublicBaseURL` / `NotifyURL`），本地开发需内网穿透 |
