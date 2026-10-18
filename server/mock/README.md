# Mock 服务器（分支 2/3 的并行开关）

`node server/mock/server.js [--port 8901]` — 零依赖，Node 18+。

这是收敛后买断后端（`docs/design/buyout-flow-convergence.zh-CN.md` §11.3）的**可运行契约**：
每个路由、字段名、状态码、错误信封都与真实服务端逐字节一致——成功响应是裸 payload
（`httpx.OK` 就是 `c.JSON(payload)`），错误响应是 `{ "error": { code, message, request_id } }`
并带 `X-Request-Id` 头（`httpx.Fail`），时间戳格式 `2006-01-02T15:04:05Z` 等价，
许可证 token 是 envelope JSON 的 base64url（与 `redeem.Result.LicenseToken` 同一编码）。
真实的 `activation`/`admin` handler 在 `feat/server-convergence` 落地后，本文件删除。

## 哪些是真的，哪些不是

| 真实（可依赖） | Mock（不可依赖） |
| --- | --- |
| 全部路由与 HTTP 方法 | 签名是占位符——envelope **不会**通过客户端内嵌公钥验签 |
| 字段名、状态码、错误信封 | QR PNG 是 QR 形状的占位图（合法 PNG，不可扫） |
| 时间戳格式、token 编码 | 任意非空 bearer token / admin 密码均接受 |
| CORS 全开（`*`） | 无数据库、无限流、无并发互斥 |

## 机器 B（admin-console）用法

```bash
node server/mock/server.js --port 8901
# 后台页所有请求带 header： X-Admin-Password: dev
# baseURL 做成配置项，合并后改一行切到真服务端
```

- `GET /v1/admin/requests?status=pending` — 已种子 3 条数据（2 待确认 + 1 已签发），首屏不空
- `POST /v1/admin/confirm {requestId}` → `{licenseId}`（幂等：重复确认返回同一 licenseId）
- `POST /v1/admin/reissue {installId}` → `{licenseId}`（先作废旧绑定再签新票）
- `POST /v1/admin/revoke {licenseId}` → `{}`
- `POST /mock/reset` — 恢复种子数据

## 机器 C（client-license-gate）用法

```bash
node server/mock/server.js --port 8901
```

完整流程（全部已实测通过）：

1. `POST /v1/auth/session {installId, deviceId}` → `{ticket, qrImageUrl, expiresAt}`
2. 展示 `qrImageUrl` 指向的 PNG，轮询 `GET /v1/auth/session/:ticket`
3. **模拟扫码**：`POST /mock/scan/:ticket`（真实环境由微信回调触发，mock 用它代替）
4. 轮询返回 `{status:"authorized", accessToken, refreshToken, account}`
5. `POST /v1/activation/request`（Bearer）→ `{requestId, status:"pending"}`
6. 卖家在后台 confirm 后，`GET /v1/activation/status` → `{status:"confirmed", licenseId}`
7. `GET /v1/activation/licence` → `{token, envelope}`；未确认时 409 `conflict`

其余 auth 端点（`DELETE /v1/auth/session`、`POST /v1/auth/refresh`、`GET /v1/account`、
`GET /v1/quota`）同样可用，形状与 `internal/auth/http.go` 一致。

## 已知的 mock 自定语义（分支 1 落地时以真实实现为准）

- `GET /v1/activation/licence` 在未确认时返回 **409 conflict**（防客户端缓存永远拿不到的票）；
  真实 handler 选什么状态码是分支 1 的决定，若不同以真实实现 + 契约文档更新为准。
- admin 鉴权只检查 `X-Admin-Password` 头非空；真实实现按 D5（单密码 + IP 白名单）。
- 会话 5 分钟过期，轮询在过期后返回 `{status:"expired"}`。
