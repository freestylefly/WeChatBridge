# 三分支并行工作分配（2026-10-18 定稿，3 台机器）

> 设计依据：`buyout-flow-convergence.zh-CN.md` §11（分支划分、API 契约、并行规则）。
> 本文是执行版：每台机器领哪条分支、干什么、出口标准、git 怎么走。

## 0. 前置状态（已完成，2026-10-18 19:30）

| 项 | 状态 |
| --- | --- |
| 基线提交 | ✅ `446dc5f`（server/ 入 git + 两份设计文档，16,670 行） |
| fork 推送 | ✅ `fork/feat/windows-portable-usb` 已更新 |
| 三分支 | ✅ 已从基线切出并推送：`feat/server-convergence` / `feat/admin-console` / `feat/client-license-gate` |
| 契约 mock | ✅ `server/mock/server.js`（零依赖 Node，14 项实测通过）——**机器 B/C 第一天即可对着开发，不用等机器 A** |
| 基线质量 | ✅ `go build/vet/test ./...` 全绿；顺手修了两个真 bug（`accountOnly` 吞 handler 致三个鉴权端点静默 200 空 body；notify 事件类型/交易状态常量混淆） |

三台机器的仓库入口：`https://github.com/zhuimeng2026-hub/WeChatBridge.git`（fork）。

## 1. 机器分配总览

| 机器 | 分支 | 代码域（独占，互不触碰） | 周期 | 关键路径？ |
| --- | --- | --- | --- | --- |
| **A（本机）** | `feat/server-convergence` | `server/` | 2~3 天 | **是**（地基，最先合主干） |
| **B** | `feat/admin-console` | `admin-console/`（新目录） | 1~2 天 | 否 |
| **C** | `feat/client-license-gate` | `windows/` | 1~2 天 | 否 |

**合并顺序**：A 先合主干（改 store/config/migrations，是地基）→ B/C 分别 rebase 最新主干后合（二者互不接触，先后随意）。

## 2. 机器 A：`feat/server-convergence`（本机）

**任务**（顺序依赖，本身不可再分）：

1. ~~`server/` 入 git~~ ✅ 基线已完成
2. ~~契约 + mock stub~~ ✅ `server/mock/` 已完成（本分支第一个 commit）
3. 删 `internal/billing`、`internal/redeem` 两个包 + config 支付字段
4. store 12 表 → 4 表（`accounts` / `auth_sessions` / `activation_requests` / `licence_bindings`）：migrations 重写 + postgres 实现同步瘦身
5. 新增 `internal/activation` 包（登记 / 状态 / 取票，签名复用 `license.Signer`）
6. 新增 admin handler（列表 / 确认 / 重签 / 作废，鉴权按 D5 最小版占位）
7. `cmd/main.go` 装配 + `/healthz` + 优雅关闭 + embed 迁移执行
8. 全量单测 + 集成测试；出口 = `go build/vet/test` 全绿 + 一条命令起服务（含迁移）

**边界**：不碰 `windows/`；微信登录联调不占本分支（汇合任务，见 §5）。

## 3. 机器 B：`feat/admin-console`（卖家管理后台）

**任务**：

1. `admin-console/` 新目录，纯静态 HTML/JS（无构建或轻构建）
2. 按契约 §11.3 对接 mock：`baseURL` 做成配置项（合并后改一行切真服务端）
3. 单密码登录页（只传 `X-Admin-Password`；IP 白名单是服务端的事）
4. 待确认列表：机器码（installId）/ 硬件锚摘要 / 微信昵称 / 金额 / 时间
5. 操作：确认 / 重签 / 作废 + 结果反馈（成功/失败/网络错误三态）
6. 已签发台账页（confirm/reissue/revoke 的历史）
7. mock 下全流程走通即为出口

**开工命令**：

```bash
git clone https://github.com/zhuimeng2026-hub/WeChatBridge.git
cd WeChatBridge
git checkout feat/admin-console
node server/mock/server.js --port 8901   # 契约后端
```

**注意**：对照收款是人工 SOP——后台只展示事实（机器码 + 金额 + 时间），「是否真到账」由卖家在微信零账核对，SOP 文案见设计文档 §3.4。

## 4. 机器 C：`feat/client-license-gate`（客户端授权门）

**任务**：

1. 微信登录 UI：`POST /v1/auth/session` → QR PNG → ticket 轮询 → 存双令牌（auth 契约已冻结，mock 全量实现）
2. 机器码展示页：installId 主键 + 硬件锚摘要辅证（D2 建议值）
3. 购买页：收款码静态图 + `POST /v1/activation/request` + 轮询 + 取票据
4. 启动门禁集成：未登录 → 登录页；已登录未授权 → 功能限制模式
5. 轮询退避；卖家拒绝/超时提示
6. 出口 = `dotnet build/test` 通过 + 对着 mock 全流程可走

**红线**：`windows/src/WeChatBridge.Windows.Core/Licensing/` 下 **LicenseResolver.cs / LicenseVerifier.cs / InstallIdentityStore.cs / DeviceAnchor.cs 四个文件禁改**（既有验签链，契约级冻结）。

**开工命令**：

```bash
git clone https://github.com/zhuimeng2026-hub/WeChatBridge.git
cd WeChatBridge
git checkout feat/client-license-gate
node server/mock/server.js --port 8901   # 契约后端
```

## 5. 汇合任务（三分支合并后，不属于任何分支）

- 全链路联调：真机扫码 → 登记 → 后台确认 → 签票 → 客户端激活
- 微信登录联调（需 AppID/Secret + 回调域配置，前置清单见设计文档 §8.1）
- 生产部署件：Dockerfile / 部署文档 / 公网 HTTPS
- 微信开放平台审核已通过（网站应用 + 备案域名），联调只剩配置

## 6. 并行规则（冲突预防）

1. **契约冻结**：§11.3 定稿；任何分支发现缺字段 → 先改契约文档 → 机器 A 跟进实现 → **禁止各自私改**。
2. **代码归属**：`server/` 只有 A 有权改；`windows/` 只有 C 有权改；`admin-console/` 只有 B 有权改；设计文档三分支都只加不改，冲突时任取其一。
3. **产品决策未定按建议值动工**（D1 个人码 / D2 双展示 / D3 同硬件重签 / D5 最小鉴权），决策变更只影响各分支局部，不重构。
4. **每分支出口标准即合并门槛**：自己分支 build/test 全绿 + mock 全流程，才准提合并。
5. git 网络操作（push/fetch）若在同类沙箱环境执行，需要在沙箱外跑（本机已验证：沙箱内 git 网络静默失败）。

## 7. 当前待用户拍板的产品决策（不阻塞任何分支）

D1 收款方式（建议个人码）/ D2 机器码定义（建议 installId 主键 + 硬件摘要辅证）/ D3 换机政策（建议同硬件免费重签）/ D4 后台形态（建议本地 Web）/ D5 admin 鉴权（建议单密码 + IP 白名单）/ D7 确认时效（建议 12 小时 + 超时原路退款）。
