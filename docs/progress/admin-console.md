# 进度看板：feat/admin-console（机器 B）

> 规则见 `docs/design/progress-sync-protocol.zh-CN.md` §2：只有机器 B 更新本文件，与 commit 同一时刻。

## 任务表

| # | 任务 | 状态 | commit | 备注 |
|---|---|---|---|---|
| B-① | 目录 + baseURL 配置（`admin-console/` 纯静态） | ⬜ | — | mock 先行，不等机器 A |
| B-② | 登录页（admin 密码，D5 最小版） | ⬜ | — | |
| B-③ | 待确认列表（`GET /v1/admin/requests`） | ⬜ | — | 含机器码 + 金额 + 登记时间 |
| B-④ | 确认签发（`POST /v1/admin/confirm`） | ⬜ | — | 核心动作，防误点 |
| B-⑤ | 重签 / 作废（`/reissue` `/revoke`） | ⬜ | — | |
| B-⑥ | 台账页（对账视图） | ⬜ | — | |
| B-⑦ | 出口 = 对着 mock 全流程可走 | ⬜ | — | |

## 出口标准

- [ ] 静态页 + `node server/mock/server.js` 全流程：登录→列表→确认→重签→作废
- [ ] 无构建依赖（或构建步骤入 commit）

## 阻塞项

（暂无）
