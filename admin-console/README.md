# 卖家管理后台（admin-console）

买断授权产品的卖家端：一个**零依赖、零构建**的纯静态页面。买家完成购买登记后，
卖家在这里核对事实（机器码 + 金额 + 时间，「是否真到账」以微信零账为准），
一键**确认签发 / 换机重签 / 作废**。

契约依据：`docs/design/buyout-flow-convergence.zh-CN.md` §11.3（API）、§11.7（鉴权变更）。

## 快速开始

```bash
# 1. 起契约 mock（该文件随分支 1 合主干后位于 server/mock/；
#    当前在 feat/server-convergence 分支上）
node server/mock/server.js --port 8901

# 2. 直接用浏览器打开本目录的 index.html（双击即可，file:// 可用）
#    或在设置页把服务端指向真实服务端
```

登录：页面展示微信开放平台网站应用的登录二维码 → 卖家本人微信扫一扫 →
自动进入后台。没有「用户 + 密码」这套凭证（D5 单密码方案已作废，见 §11.7）。

## 配置

| 项 | 位置 | 说明 |
| --- | --- | --- |
| 服务端地址 | `js/config.js` 的 `baseURL` | **合并后改这一行切真服务端**，如 `https://license.example.com` |
| 自动刷新间隔 | `js/config.js` 的 `autoRefreshMs` | 待确认列表轮询，默认 10 秒 |
| 运行时覆盖 | 页面「设置」页 | 写本浏览器 localStorage，开发期在 mock / 真服务端间切换用；「恢复默认」即回到 config.js |

**部署约束：页面必须与服务端同源。** 服务端目前不带任何 CORS 响应头
（`internal/` 下无 CORS 中间件），`baseURL` 指向别的源时浏览器会在预检阶段
拦掉全部请求，页面只会得到一句 `Failed to fetch`。要么让静态页与 API 同源部署，
要么由服务端显式放行——**不要**为了图省事给管理接口开 `*` 通配 CORS。


## 功能

- **扫码登录**：复用冻结的 auth 端点（建会话 → QR → 轮询 → 双令牌）。访问令牌只存
  sessionStorage，关标签页即失效；到期前 60 秒自动续期，401 自动续期重试一次，
  两层都失败才要求重新扫码。
- **待确认列表**：登记时间 / 机器码 installId（可复制）/ 硬件锚摘要 / 微信昵称 /
  期望金额；10 秒自动刷新 + 手动刷新。
- **三个管理动作**（均带确认弹窗防误点）：
  - 确认签发 `POST /v1/admin/confirm {requestId}` → 展示返回的许可证号；
  - 换机重签 `POST /v1/admin/reissue {installId}`；
  - 作废 `POST /v1/admin/revoke {licenseId}`。
  - 列表之外的机器码 / 许可证号：页面底部「手动操作」区直接输入执行。
- **已签发台账**：confirmed 登记列表 + 本机操作日志（localStorage）。
- **三态反馈**：成功（绿）/ 操作失败（红，含 HTTP 状态、错误码、request_id）/
  网络错误（黄，提示检查 mock 是否启动）。

## 已知边界（诚实清单）

1. **许可证号列依赖本机日志**：冻结契约的 `GET /v1/admin/requests` 不返回
   `licenseId`（候选契约增量已登记于设计文档 §11.7，待机器 A 评估）。非本机完成的
   签发显示「—」，作废请用手动操作区输入许可证号。
2. **mock 与真实服务端的重签差异**：真实 `store.ReissueLicence` 会一并确认该
   install_id 的待处理登记（买家轮询当即看到 confirmed）；mock 不翻转登记态。
   控制台行为以契约为准。
3. **管理员身份**：mock 接受任何非空 Bearer；真实服务端按 OpenID 白名单判定
   （`WCB_ADMIN_OPENIDS`，机器 A 任务 6 实现）。白名单外的微信扫码可以登录但
   管理调用会 403。

## 验证（出口标准）

```bash
# A. 契约冒烟（Node，驱动与浏览器相同的 js/api.js）
WCB_MOCK_SCRIPT=../server/mock/server.js node test/smoke.mjs
# 或外挂 mock：node server/mock/server.js --port 8901 && node test/smoke.mjs

# B. 浏览器全流程（需 playwright-core + 系统 Chrome；mock 先起在 8901）
NODE_PATH=<playwright-core 所在 node_modules> node test/browser.mjs
```

两条各 21 / 17 项断言：登录 → 列表 → 确认（幂等）→ 重签 → 作废 → 台账，
外加错误信封形状、网络错误态、无 JS 异常。截图输出在 `test/shot-*.png`。

## 文件

```
admin-console/
├── index.html        登录视图 + 应用外壳（待确认 / 台账 / 设置）
├── css/style.css     浅色主题，无构建
├── js/config.js      baseURL 等配置（UMD，Node 可读同一份）
├── js/api.js         契约客户端（UMD：浏览器挂 window，Node 可 require）
├── js/app.js         DOM 逻辑（唯一碰页面的文件）
└── test/             smoke.mjs（契约）/ browser.mjs（UI）/ shot-*.png（出口证据）
```

## 安全说明

- 无任何凭证写盘：访问令牌只在 sessionStorage；本地操作日志只记动作与许可证号。
- 页面经 `X-Admin-Password` 之外的方式鉴权（Bearer，见 §11.7）；IP 白名单等服务端
  侧控制由真实服务端落实（D5 原文中的 IP 白名单部分仍然有效）。
- 所有错误提示都不回显令牌本身；request_id 是故意展示的——排查时那是服务端日志的钥匙。
