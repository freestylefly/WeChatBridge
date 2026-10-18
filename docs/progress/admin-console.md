# 进度看板：feat/admin-console（机器 B）

> 规则见 `docs/design/progress-sync-protocol.zh-CN.md` §2：只有机器 B 更新本文件，与 commit 同一时刻。

## 任务表

| # | 任务 | 状态 | commit | 备注 |
|---|---|---|---|---|
| B-① | 目录 + baseURL 配置（`admin-console/` 纯静态） | ✅ | 3efc06c | `js/config.js`（UMD，Node 可读同一份）；零构建零依赖，file:// 直接打开 |
| B-② | 登录页 | ✅ | 3efc06c | **微信开放平台扫码登录**（§11.7 契约变更，D5 单密码作废）；令牌只存 sessionStorage，到期自动续期 + 401 重试一次 |
| B-③ | 待确认列表（`GET /v1/admin/requests`） | ✅ | 3efc06c | 登记时间 / 机器码（可复制）/ 硬件锚摘要 / 昵称 / 金额；10s 自动刷新 |
| B-④ | 确认签发（`POST /v1/admin/confirm`） | ✅ | 3efc06c | 弹窗展示目标事实防误点；重复确认幂等（同一许可证号） |
| B-⑤ | 重签 / 作废（`/reissue` `/revoke`） | ✅ | 3efc06c | 行内按钮 + 「手动操作」区（列表之外的机器码 / 许可证号） |
| B-⑥ | 台账页（对账视图） | ✅ | 3efc06c | confirmed 列表 + 本机操作日志补 `licenseId` 列（契约缺口与候选增量见 §11.7） |
| B-⑦ | 出口 = 对着 mock 全流程可走 | ✅ | 6c0b355 | `test/smoke.mjs` 21/21 + `test/browser.mjs` 17/17；截图 `test/shot-*.png` |

## 出口标准

- [x] 静态页 + `node server/mock/server.js` 全流程：登录（扫码）→ 列表 → 确认 → 重签 → 作废
- [x] 无构建依赖（零依赖零构建）

## 阻塞项

（暂无）

## 备注

- admin 鉴权契约变更（`X-Admin-Password` → `Authorization: Bearer` 扫码令牌）已按规则走文档先行：设计文档 §11.7；mock 侧由分支 1 commit `a8610e2` 同步落地（可运行契约）。
- mock 文件不在本分支（`server/` 归机器 A）：出口验证用 `WCB_MOCK_SCRIPT` 指向该文件；A 合主干后即可用仓库内路径 `server/mock/server.js`。
- 浏览器验证抓到并修复一个真 bug：`.modal-mask` 的 `display:flex` 覆盖 `hidden` 属性 UA 样式，导致弹窗遮罩常驻拦截全页点击（css 已加 `[hidden]{display:none!important}`）。
