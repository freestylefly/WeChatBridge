# 买断授权收敛方案：扫码登录门 + 机器码登记 + 人工确认签发

> 记录时间：2026-10-18
> 阶段定位：**评估与架构设计，不含落地编码**
> 代码基线：`server/`（Go，非测试约 12,300 行，工作区未提交）、`windows/src/WeChatBridge.Windows.Core/Licensing/`
> 上游文档：`buyout-license-issuance.zh-CN.md`（客户端-服务端边界、故障分层、离线签发替代）
>
> **结论先行：**
> 1. **支付链路整条删除**——billing（2,207 行）+ redeem（1,064 行）连同兑换码概念一起消失，服务端净减约四分之一；但真正的收益不是行数，是减掉的全是高复杂度部分（支付证书验签、幂等重放、并发互斥、订单状态机），剩下的是 CRUD + 一个确认按钮。
> 2. **「简单」主要是转移而非消除**——微信扫码登录成为硬前置（现有 `auth` 包就是按这个长的，可直接复用）；新增成本在人工确认 SOP 与对账，不在代码。
> 3. **适用边界**：单卖家、日单 ≤ 20、低价买断。超过此线，人工确认成为瓶颈，必须回到自动支付。
> 4. **体验不倒退**：已授权用户仍永久离线可用（本地验签 + DPAPI 缓存），服务器故障只影响新购买、不影响存量用户——上一轮「服务器异常导致用户工作异常」的担忧在本方案中继续得到保证。
> 5. **最大外部风险不是代码**：微信开放平台的应用资质审核（企业主体）是硬前置，排期不可控。

---

## 1. 目标场景（步骤化）

| 步骤 | 动作 | 责任方 | 网络依赖 |
| --- | --- | --- | --- |
| S1 | 客户端启动，未登录 → 展示微信登录二维码，用户扫码 | 客户端 + 服务端 + 微信开放平台 | 必须联网 |
| S2 | 登录成功（openid 建号）→ 未授权状态显示**本机机器码** + 「购买」按钮 | 客户端 | — |
| S3 | 点购买 → 客户端展示卖家收款码（静态图，配置下发）+ 金额说明；同时向服务端登记 `activation_request`（openid + installId + 硬件锚摘要 + 金额） | 客户端 + 服务端 | 必须联网 |
| S4 | 用户微信扫码付款，并把机器码发给卖家（微信消息/截图） | 用户 | 线下，无 API |
| S5 | 卖家在管理后台看到待确认列表（含该机器码）→ **核对微信收款到账记录** → 点「确认签发」 | 卖家 + 管理后台 | 后台需联网 |
| S6 | 服务端用 ECDSA 私钥签发许可证票据（`sub=installId`，`exp=null` 永久） | 服务端 | — |
| S7 | 客户端轮询 status → confirmed → 拉取票据 → 本地验签通过 → DPAPI 缓存 → **永久可用，断网照常** | 客户端 | 仅激活时联网 |
| S8 | 换机器 / 重装 → 新 installId → 后台出现新登记 → 按换机政策重签或要求重新购买 | 卖家 | 后台需联网 |

与现行架构的差别只有一句话：**「自动烧码换票」变成「人工点确认按钮签票」**，签发的密码学、客户端的验签、票据的绑定语义，全部原样保留。

---

## 2. 评估：删除 / 保留 / 新增

### 2.1 直接删除

| 模块 | 规模 | 删除理由 |
| --- | --- | --- |
| `internal/billing` 整包 | 2,207 行 | 下单 / 异步通知 / 微信支付 APIv3 证书验签 / 订单状态机全部不再需要 |
| `internal/redeem` 整包 | 1,064 行 | 「兑换码」概念消失：burn 条件更新、幂等重放、设备槽、并发互斥、同机重签全部不需要 |
| `store/postgres` 中 orders / redeem_codes / qr_payloads / quotas 相关实现 | 约 700 行 | 数据模型瘦身 |
| `config` 中 `PaymentConfigured` 半配强校验、`PublicBaseURL` / `NotifyURL` | 约 30 行 | 无支付回调 |
| migrations 中 5~6 张表 | — | orders / redeem_codes / redemption_requests / qr_payloads / quotas 等 |

**连带消失的运维负担**（比代码更值钱的部分）：微信商户号申请、商户 API 证书与平台证书轮换、ICP 备案 + 公网 HTTPS（微信支付硬要求）、支付通知重推与对账、支付沙箱联调（今天的 G5/G6 两项直接归零）。

### 2.2 原样保留

| 模块 | 规模 | 保留理由 |
| --- | --- | --- |
| `internal/license` | 424 行 | ECDSA 签发/验签核心，canonical 格式由 `TestSharedCanonicalVector` 与 C# 双向钉死——**这是整个方案的信任根，一行不动** |
| `internal/auth` + `internal/wechat`（client.go + qr.go） | 1,858 行 | sns/oauth2 扫码登录、ticket 轮询、双令牌 handoff——正是新方案的前置门，现成实现 |
| `internal/token` | 364 行 | access/refresh 双令牌 |
| `internal/httpx`、`internal/config`（瘦身） | 509 行 | 基础设施 |
| 客户端 `Licensing/` 全链路 | — | LicenseResolver / Verifier / InstallIdentityStore / DeviceAnchor **零改动** |

### 2.3 新增（复杂度转移的去向）

| 新增项 | 估算 | 说明 |
| --- | --- | --- |
| `internal/activation`（登记 / 确认 / 触发签发） | 约 400 行 | 纯 CRUD + 调 `license.Signer`，无密码学验签、无幂等重放、无并发互斥 |
| admin handler（列表 / 确认 / 重签 / 作废） | 约 300 行 | `RegisterAdmin` 注入点已在，缺 UI |
| `cmd/main.go` 装配 + `/healthz` + 优雅关闭 | 约 200 行 | 今天的 G1/G3 仍需做，但装配面小得多 |
| 管理后台 Web 页面（卖家自己用） | 约 300 行 | 待确认列表 + 确认按钮 + 台账；可内嵌静态页 |
| 客户端购买页 / 机器码展示 / 状态轮询（C#） | 约 300 行 | 客户端侧唯一新增 UI |
| **人工 SOP + 台账**（非代码） | — | 核对收款、时效承诺、错单处理、事后补录——这才是真正的「新增成本」 |
| 私钥 custody 制度（非代码） | — | 签名私钥落在卖家自己的服务器：密钥文件权限、备份、轮换预案 |

### 2.4 净账

| | 行数 |
| --- | --- |
| 删除 | 约 4,000 |
| 新增 | 约 1,300 |
| **净变化** | **约 -2,700（服务端 -22%）** |

行数只减两成，但**复杂度下降远大于此**：删掉的是支付密码学、全局唯一状态机、幂等协议；留下的是「一个列表、一个按钮、一次签名」。

---

## 3. 目标架构

### 3.1 组件与职责

| 组件 | 职责 | 新/旧 |
| --- | --- | --- |
| Windows 客户端 | 微信登录门；机器码展示；收款码展示；轮询激活状态；**本地验签 + DPAPI 缓存票据** | 登录/购买 UI 新写；Licensing 验签链零改动 |
| 授权服务端 | 扫码登录（auth）；activation 登记与确认；ECDSA 签发；4 张表 | billing/redeem 删除，activation/admin 新增 |
| 卖家管理后台（Web） | 待确认列表、确认签发、换机重签、作废、台账导出 | 新增（原 `RegisterAdmin` 只有 API 没有界面） |
| 微信开放平台 | sns/oauth2 扫码登录（拿 openid/unionid） | 现成 |
| 支付 | **线下**：客户端展示卖家收款码，用户扫码付款，无任何 API | 从零集成退为静态图 |

### 3.2 数据模型（4 张表）

| 表 | 字段 | 说明 |
| --- | --- | --- |
| `accounts` | openid, unionid, nickname, avatar, created_at | 原样保留 |
| `auth_sessions` | ticket, openid, status, expires_at | 扫码登录 handoff 票据 |
| `activation_requests` | request_id, openid, install_id, anchor_digest, amount_fen, status(pending/confirmed/rejected/issued), created_at, confirmed_at, license_id | **替代原 redeem_codes + redemption_requests + orders 三张表** |
| `licence_bindings` | license_id, install_id, openid, issued_at, revoked_at | 原样保留，加 openid 便于台账 |

删除：`orders` / `redeem_codes` / `redemption_requests` / `qr_payloads` / `quotas` / `sessions` / `refresh_tokens`（refresh 改存 `accounts` 或内存 handoff，二选一，见 D6）。

### 3.3 API 面（最小集）

| 端点 | 方法 | 状态 |
| --- | --- | --- |
| `POST /v1/auth/ticket`（轮询换双令牌） | 扫码登录 | 已有，不动 |
| `POST /v1/activation/request` | 登记 {openid, installId, anchorDigest, amount} | 新增 |
| `GET /v1/activation/status` | 轮询 pending/confirmed | 新增 |
| `GET /v1/activation/licence` | confirmed 后取票据 | 新增 |
| `GET /v1/admin/requests` | 待确认列表 | 新增 |
| `POST /v1/admin/confirm` | 确认 → 签发 | 新增 |
| `POST /v1/admin/reissue` | 换机重签（新 installId，作废旧绑定） | 新增 |
| `POST /v1/admin/revoke` | 作废（数据库标记；见 §6 关于不可撤销的诚实说明） | 新增 |

### 3.4 客户端状态机

```
[未登录] ──扫码登录(S1)──► [已登录·未授权] ──点购买·登记(S3)──► [待确认]
                              ▲                                  │
                              │                         卖家确认·签发(S5/S6)
                    换机器/重装·新installId                        │
                              │                                  ▼
                              └────── 重新走流程 ◄────── [已授权·永久]
                                                          （本地验签+DPAPI，0 网络请求）
```

异常分支：卖家拒绝 / 超时未确认 → 回 `已登录·未授权`（保留 request 记录，卖家可翻旧账）。

### 3.5 管理后台（最小形态）

一个本地 Web 页面，一张表：

| 列 | 内容 |
| --- | --- |
| 机器码 | installId（可复制） |
| 硬件摘要 | 固件 + MAC + OS（seller 肉眼核对「是不是同一台机器」） |
| 用户 | openid 昵称 / 头像 |
| 金额 / 时间 | 期望金额、登记时间 |
| 状态 | pending / confirmed / revoked |
| 操作 | 确认签发 / 换机重签 / 作废 |

配一个「已签发台账」页（license_id ↔ installId ↔ openid ↔ 确认时间），供事后对账。

---

## 4. 机器码定义：一个必须拍板的差异

用户的直觉是「验证码跟硬件相关」，但**现状不是这样**，必须先对齐再设计：

| 标识 | 现状 | 用途 |
| --- | --- | --- |
| `installId` | 16 字节 CSPRNG **随机数**，不从硬件计算（`InstallIdentityStore.cs:58/89`） | 票据签名 subject（`sub`） |
| `DeviceAnchor` | 固件型号 + 网卡 MAC + OS 名（`DeviceAnchor.cs`） | 仅作换机/重装恢复判据 |

**提案：机器码 = installId（主键）+ 硬件锚摘要（辅证）双展示。**

- 票据绑定仍用 installId（密码学语义不变，客户端零改动）；
- 硬件摘要给 seller 做人工核对：重装后硬件没变 → 摘要一致 → 免费重签；真换了机器 → 摘要不同 → 要求重新购买（或卖家豁免）；
- 不推荐把硬件摘要升级为主键：MAC 会因换网卡/虚拟网卡变动，误伤比现在大。

---

## 5. 故障与体验分层（回应「服务器异常」担忧）

| 故障 | 已授权用户 | 新用户 | 卖家 |
| --- | --- | --- | --- |
| 授权服务器宕机 | **无感**（每次启动 0 网络请求，票据永久本地有效） | 登录不了、登记不了、买不了 | 后台不可用 |
| 签名私钥丢失 | 已发票据继续有效 | 无法签新票 | 需备份恢复（custody 制度） |
| 卖家延迟/失联 | 无感 | 等待（需时效承诺 SOP） | — |
| 微信开放平台故障 | 无感 | 登录不了（一次性） | — |

**缓解手段**：session 长效化（refresh TTL 拉到 90 天，减少重新扫码频次）；管理后台可跑在卖家自己的电脑上（不依赖公网）；票据 `exp=null` 永久有效是产品本身的选择，不是疏漏。

---

## 6. 安全边界（诚实清单）

1. **客户端验签防普通用户，不防破解。** 纯本地授权理论上都可被 patch。买断低价 = 破解收益低，v1 接受此边界。若未来要硬化（关键功能上云、混淆、反调试），与「断网可用」存在内在冲突，需二选一——这是产品决策不是工程决策。
2. **票据跨机无效**：`sub` 绑 installId + DPAPI identity blob 换机打不开 + 硬件锚拒绝 DifferentMachine，三层叠加。泄漏的票据文件本身没有铸币权。
3. **人工确认的诈骗面**：伪造付款截图 + 催促是最现实的攻击。**确认动作必须绑定「微信收款记录到账」这一事实**，SOP 写明：未见零钱到账不确认。
4. **机器码伪造在经济上不划算**：每个假机器码都要真付一份钱。
5. **管理后台暴露面**：单密码 + IP 白名单（卖家自己的网络）是 v1 底线；不要暴露公网。
6. **已发票据不可撤销**（旧文档 §4.1 的缺口仍在）：`exp=null` + 离线验签意味着作废只能挡服务端重新签发。真要撤销需客户端公钥黑名单 + 定期联网刷新——与离线目标冲突，v1 诚实接受，靠 DPAPI/硬件锚防扩散。

---

## 7. 与现有代码的差距

| 动作 | 内容 | 工作量 |
| --- | --- | --- |
| 保留 | license / token / auth / wechat(client+qr) / httpx + 客户端 Licensing 全链路 | 0 |
| 瘦身 | store（12 表 → 4 表）、config（去支付校验）、migrations 重写 | 半天 |
| 删除 | billing、redeem 两包及相关表 | 0.5 小时（删比写快） |
| 新增 | activation 包 + admin handler + `cmd/main.go` + 管理后台页 + 客户端购买 UI | 2~3 天 |
| 外部 | 微信开放平台应用创建与审核（企业主体） | **排期不可控，最优先启动** |

关键路径 = 微信审核 → 登录链路联调 → 登记/确认 → 签发 → 客户端购买页。

---

## 8. 待决策点（需用户拍板）

| # | 决策 | 建议 |
| --- | --- | --- |
| D1 | 收款方式：个人收款码 / 商户号 Native 支付 | v1 个人码；商户号 = 回到重架构，仅当日单上来再做 |
| D2 | 机器码定义：installId+锚摘要双展示 / 纯硬件摘要 | 双展示（§4） |
| D3 | 换机政策：同硬件免费重签 / 一律重购 | 同硬件免费重签（重装不该惩罚用户），真换机重购 |
| D4 | 管理后台形态：本地 Web / CLI / 远程 Web | 本地 Web（卖家自己电脑，无公网暴露） |
| D5 | admin 鉴权：单密码+IP白名单 / 微信 OAuth 二次登录 | v1 单密码+白名单（今天 G4 的轻量版） |
| D6 | 微信侧形态 | **已闭合（2026-10-18）：网站应用形态，方案 A，现有代码零改动**。用户确认审核通过的应用为网站应用且已有 ICP 备案域名；独立桌面应用类型已被微信取消（见 §8.1） |
| D7 | 确认时效承诺（SOP 文案）：几小时内确认、超时怎么办 | 建议 12 小时 + 超时原路退款流程 |

### 8.1 微信接入形态定论（2026-10-18，平台审核已完成、用户确认应用类型后闭合）

**定论：网站应用扫码登录（方案 A）是唯一官方路径，与现有代码完全一致，零改动直接联调。D6 闭合。**

两个事实已确认：① 审核通过的应用类型为**网站应用**；② 已有 **ICP 备案域名**。

**B1 分支作废的核实结论**：用户判断正确——微信开放平台已取消独立的「桌面应用」应用类型（2023-10 官方答复原话：「目前是不支持接入桌面软件的」；官网应用类型列表现仅移动应用 / 网站应用 / 公众号 / 第三方平台 / 小程序）。现存挂在网站应用下的「桌面应用」是另一回事：它是**网站应用的扩展能力**（微信内网页拉起桌面应用，类似协议唤起），前置条件恰是「网站应用已审核通过」，且用途是唤起而非登录。对本项目登录场景无意义，但记入 v2 可选：将来可在微信内做「点击链接直接唤起 WeChatBridge 客户端」。

**现有代码的形态**（已核实）：`wechat/client.go` 包注释原话 "the website app's OAuth2.0 QR sign-in"——qrconnect + `scope=snsapi_login` + `state=ticket`，回调 `GET /v1/auth/wechat/callback` 落在服务端，客户端只轮询；注释并明确记录约束："The redirect domain is configured on the open platform and must be a filed public HTTPS domain. Not 127.0.0.1, not a port, not an IP." 这不是疏忽，是设计前提：AppSecret 与 access token 全程不出服务端，客户端只见二维码 PNG。

| 维度 | A 网站应用（现状） | B1 桌面应用 + 本地回环 | B2 网站应用 + IP:端口回调域 |
| --- | --- | --- | --- |
| 应用类型 | 网站应用（审核过的即是） | ~~需另建桌面应用~~（该类型已取消，见定论） | 网站应用 |
| 回调落地 | 服务端公网域名 | 用户本机 `127.0.0.1:port` | 用户本机 `127.0.0.1:port` |
| 备案域名 | **必须**（ICP + HTTPS） | 不需要 | 不需要 |
| 服务端需被微信主动访问 | 是 | 否（只需客户端可达） | 否 |
| 现有代码改动 | **0** | redirect_uri 配置化 + 客户端本地监听 + code 提交端点 | 同 B1 |
| AppSecret 暴露面 | 不出服务端 | 不出服务端 | 不出服务端 |
| 规则风险 | 官方标准路径 | 官方桌面应用路径 | 社区有成功实践，但官方口径是域名，**能否配 IP 需在后台实测**，非承诺 |

**联调前置清单（方案 A，代码侧零改动，全是配置）：**

| # | 事项 | 位置 / 值 |
| --- | --- | --- |
| 1 | 开放平台后台配「授权回调域」 | 填备案域名，**不带协议前缀、不带路径**；校验是精确字符串匹配 |
| 2 | 回调完整 URL | `https://<备案域名>/v1/auth/wechat/callback`（`CallbackPath` 在 `auth/http.go:20` 硬编码，`CallbackURL = PublicBaseURL + CallbackPath`，`auth/service.go:143`） |
| 3 | `WCB_WECHAT_APP_ID` / `WCB_WECHAT_APP_SECRET` | 应用详情页获取；config 中为必填项（`config.go:97-98`），AppSecret 只进内存不进日志 |
| 4 | `WCB_PUBLIC_BASE_URL` | `https://<备案域名>`；生产环境强制 https（`config.go:185-186` 强校验） |
| 5 | 服务端公网可达 + HTTPS 终止（nginx） | 微信要能主动访问第 2 条的回调 URL |
| 6 | `WCB_WECHAT_AUTH_URL` / `WCB_WECHAT_API_BASE` | 有默认值（`config.go:99-100`），联调无需改 |

> 原「待确认事实」已于 2026-10-18 晚间确认完毕：审核通过的应用为网站应用、已有备案域名，D6 闭合。

**一个关键论证：域名 + HTTPS 不是微信带来的额外负担。** 收敛架构里客户端本来就要访问服务端（登记、轮询、取票据），服务端本来就需要稳定公网入口；网站应用方案只是让同一个入口顺便收微信回调。相对裸 IP 部署，真正新增的只有「ICP 备案」这一项行政成本。

**B 方案的隐性代价**（选 B 前需知道）：客户端起本地监听收 code，Windows 防火墙首次弹窗、端口占用与多实例冲突要处理；A 方案没有这些问题。B2 现降级为备胎：仅当将来服务端失去稳定公网域名时才重新评估。

---

## 9. 路线图

| 阶段 | 内容 | 出口标准 |
| --- | --- | --- |
| v1 最小闭环 | 登录门（已有）→ activation 登记/确认 → 签发 → 客户端购买页+轮询 → admin 列表+确认 | 真机走通：扫码→付款→后台确认→无限使用；全程实录 |
| v1.5 | 换机重签流程、台账导出、时效 SOP 落地 | 客服三场景（重装/换机/作废）可操作 |
| v2 可选 | 收款到账推送机器人（代替人工刷手机看零钱）、对账报表 | 日单 >20 前的自动化预备 |
| 不做 | 自动支付、自动开票、多销售渠道 | 除非商业模式变化 |

---

## 10. 遗留事项（今天未闭合，交接用）

1. **billing 通知测试失败**：`billing: unsupported callback event "TRANSACTION.SUCCESS"`——notify 把交易状态 `TRANSACTION.SUCCESS` 当作事件类型校验，常量混淆（约 1~2 行修复）。本方案删除 billing 整包后此问题随之消失；若收敛暂缓，需单独修。
2. **推送暂缓**：`server/` 未入 git、本地领先 `fork/feat/windows-portable-usb` 一个提交。建议架构定稿后连同收敛一次提交，避免推上去就删。
3. **`cmd/main.go` 装配**（原 G1）、**admin 鉴权**（原 G4 → D5）、**/healthz 等运维件**（原 G3）在本方案中仍是必做项，只是装配面小得多。
4. **撤销机制缺失**（旧文档 §4.1）：本方案不改变该缺口，v1 接受「已发票据不可撤销」。

---

## 11. 实施拆分：分支并行计划（2026-10-18 重新评估定稿）

> 评估结论：**按代码域隔离切分，三个特性分支 + 两个汇合任务**。并行度上限由「代码接触面隔离」决定：`server/`（Go）、`windows/`（C#）、`admin-console/`（静态页）三个域 git 层面零冲突，超过 3 个分支必然在同一个域内切分——Go 同包并行会天天互相踩编译，合并成本大于并行收益（详见 §11.6 的推演）。

### 11.1 任务全集盘点

| 任务 | 归属 |
| --- | --- |
| `server/` 纳入 git、billing 测试失败随包删除消失 | 分支 1 前置 |
| 服务端收敛：删 billing/redeem、store 瘦身、activation/admin、装配、运维件 | 分支 1 |
| 微信扫码登录联调（配置 + 真机验证） | 汇合任务 E（阻塞于外部输入，见 §11.5） |
| 卖家管理后台（Web） | 分支 2 |
| 客户端授权门（登录 UI、机器码、购买页、轮询） | 分支 3 |
| 合并主干后全链路联调 + 部署 | 三方汇合 |

### 11.2 三分支划分

> 机制说明：三台机器各自 `git clone` + 各自分支并行开发；同一台机器多目录并行则用 `git worktree`（判断矩阵见 §11.6）。分支间代码接触面按目录隔离，冲突热点只有文档与契约，按 11.4 规则处理。

**分支 1：`feat/server-convergence`（服务端收敛与装配）**

| 项 | 内容 |
| --- | --- |
| 代码范围 | `server/`（唯一）；不碰 `windows/` |
| 任务 | ① `server/` 入 git；② 删 billing/redeem 包 + config 支付字段；③ store 12 表 → 4 表 + migrations 重写 + postgres 实现；④ 新增 `activation` 包（登记/状态/取票）；⑤ 新增 admin handler（列表/确认/重签/作废，鉴权按 D5 最小版占位）；⑥ `cmd/main.go` 装配 + `/healthz` + 优雅关闭 + embed 迁移；⑦ 全量单测 + 集成测试 |
| 出口标准 | `go build/vet/test` 全绿；一条命令起服务（含迁移）。**微信联调不占本分支**（见 §11.5，避免外部输入卡住出口定义） |
| 第一个 commit | **契约 + mock stub**（§11.3 定稿 + 一个可跑的 mock server），48 小时内交付——这是分支 2/3 并行度的真正开关 |
| 依赖 | 无（地基分支，**最先合并主干**） |

**分支 2：`feat/admin-console`（卖家管理后台）**

| 项 | 内容 |
| --- | --- |
| 代码范围 | `admin-console/`（新目录，纯静态 HTML/JS，无构建或轻构建）；不碰 `server/`、`windows/` |
| 任务 | ① 按 §11.3 契约写 mock API；② 单密码登录页；③ 待确认列表（机器码/硬件摘要/用户/金额/时间）；④ 确认/重签/作废操作 + 结果反馈；⑤ 已签发台账页；⑥ mock 下全流程走通 |
| 出口标准 | 对着 mock 全流程可走；合并后切到真 API 无需改代码（仅改 baseURL 配置项） |
| 依赖 | §11.3 API 契约（冻结后即可动工，不等分支 1 代码） |

**分支 3：`feat/client-license-gate`（客户端授权门）**

| 项 | 内容 |
| --- | --- |
| 代码范围 | `windows/src/WeChatBridge.Windows.Core/Licensing/` 与启动流程/UI 层；**不改既有 LicenseResolver/Verifier/InstallIdentityStore/DeviceAnchor 四个文件** |
| 任务 | ① 微信登录 UI（`POST /v1/auth/session` → QR PNG → ticket 轮询 → 存双令牌）；② 机器码展示页（installId 主键 + 硬件锚摘要辅证，按 D2 建议值）；③ 购买页（收款码静态图 + 登记 + 轮询 + 取票据，按 D1 建议值个人码）；④ 启动门禁集成（未登录→登录页；已登录未授权→功能限制）；⑤ 轮询退避与卖家拒绝/超时提示 |
| 出口标准 | `dotnet build/test` 通过；对着 mock/契约服务器全流程可走 |
| 依赖 | §11.3 API 契约 + 已有 auth API（已冻结不动） |

### 11.3 API 契约（三分支共同冻结点，先于编码定稿）

契约变更流程：任何分支发现缺字段 → 先改契约文档 → 分支 1 跟进实现 → 禁止各自私改。

**客户端 → 服务端**（均需 bearer access token）：

```
POST /v1/activation/request
  req:  { installId, anchorDigest, amountFen? }
  resp: { requestId, status: "pending" }

GET /v1/activation/status
  resp: { status: "pending" | "confirmed" | "rejected", licenseId? }

GET /v1/activation/licence
  resp: { token, envelope }        // envelope 即 C# LicenseResolver 直接消费的格式
```

**管理后台 → 服务端**（admin 鉴权按 D5 最小版：单密码 + IP 白名单）：

```
GET  /v1/admin/requests?status=pending
  resp: { items: [{ requestId, installId, anchorDigest, nickname, amountFen, createdAt, status }] }

POST /v1/admin/confirm   { requestId }   → { licenseId }
POST /v1/admin/reissue   { installId }   → { licenseId }   // 换机重签：作废旧绑定、签新票
POST /v1/admin/revoke    { licenseId }   → {}
```

**不得更改的既有契约**：license envelope 的 canonical 格式（`TestSharedCanonicalVector` 在 Go/C# 双侧钉死）、auth 包全部端点、`httpx` 错误码体系。错误响应统一为 `httpx.Fail` 的既有信封 `{ "error": { "code", "message", "request_id" } }`（带 `X-Request-Id` 响应头），成功响应为裸 payload（`httpx.OK` 即 `c.JSON(payload)`，无包装层）；新增错误码在契约文档登记。可运行契约见 `server/mock/`（零依赖 Node mock，分支 2/3 第一天即可对着开发，真实 handler 落地后删除）。

### 11.4 并行规则与合并顺序

0. **第 0 步（一切的前提）**：当前 `server/` 整个目录未入 git。先把基线提交（server/ + 客户端现状 + 本文档）推到功能分支，三个特性分支从该基线切出。注意：git 网络操作须在沙箱外执行（本机 Bash 沙箱会静默拦截 push/fetch）。
1. **契约先行**：§11.3 与设计文档定稿、分支 1 提交 mock stub 后，三台机器才同时动工；分支 2/3 全程对着契约 + mock，不等分支 1 完成。
2. **合并顺序**：分支 1 先合主干（改 store/config/migrations，是地基）→ 分支 2/3 分别 rebase 最新主干后合（二者互不接触，先后随意）。
3. **代码归属**：migrations / go.mod / store 接口 / config 只有分支 1 有权改；`windows/` 只有分支 3 有权改；`admin-console/` 只有分支 2 有权改；设计文档三个分支都只加不改，冲突时任取其一。
4. **产品决策未定的部分按建议值动工**（D1 个人码、D2 双展示、D3 同硬件重签、D5 最小鉴权），决策变更只影响各分支局部，不重构。
5. **每分支出口标准即合并门槛**：自己分支 build/test 全绿 + mock 全流程，才准提合并。

### 11.5 合并后（汇合）事项

| # | 任务 | 阻塞 / 依赖 |
| --- | --- | --- |
| E | 微信登录联调：开放平台回调域配置 + `WCB_WECHAT_APP_ID/SECRET` + `WCB_PUBLIC_BASE_URL` 填入 + 真机扫码 | **阻塞于用户提供 AppID/Secret 与确认回调域名**（其余全就绪） |
| F | 全链路联调（真机扫码 → 登记 → 后台确认 → 签票 → 客户端激活）+ 生产部署件（Dockerfile / 部署文档，分支 1 内一并产出）+ `server/` 推送 fork | 依赖三分支合并 + E 完成 |

### 11.6 重新评估记录：为什么是 3 个分支，不是 2 / 4 / 5 个

| 候选拆分 | 评估 | 结论 |
| --- | --- | --- |
| 2 个分支（管理后台并入服务端） | 后台是纯静态页、与 Go 零接触面，并入分支 1 只是让一台机器顺序做两块活，白损失一个并行轨道（后台 1~2 天） | 否（除非只有 2 台机器） |
| 4 个分支 | 无合格第四块：微信联调太小且阻塞外部输入（占机器空转）；server/ 内再分（收敛/新增）违反同域并行的编译冲突问题；客户端再分同 C# 项目 | 否 |
| 5 个（按端到端切片：登录门/购买闭环/收敛清理） | 跨层切分——每个切片都同时碰服务端和客户端，冲突面爆炸 | 否 |
| **3 个（按代码域）** | 唯一满足「接触面隔离 + 每块 1~3 天 + 可独立验收」的切法；关键路径 = 服务端分支（2~3 天，顺序依赖链决定不可再分），并行轨道恰好吸纳后台与客户端 | **是** |

**分支 vs worktree（用户问到的机制选择）：**

| 场景 | 机制 | 理由 |
| --- | --- | --- |
| 3 台不同机器 | 各自 `clone` + 各自分支，中央仓库（Gitee）同步 | 物理隔离，只能如此 |
| 1 台强机多目录并行 | `git worktree`（共享一个 `.git`，三个目录同时 checkout） | 免反复推拉；三个 IDE 窗口各开各的 |
| 2 台机器 | 分支 1 独占一台；另一台跑 2 个 worktree（后台 + 客户端顺序或交替） | 后台与客户端无编译依赖，worktree 切换零成本 |
