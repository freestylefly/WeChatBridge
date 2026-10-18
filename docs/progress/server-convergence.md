# 进度看板：feat/server-convergence（机器 A）

> 规则见 `docs/design/progress-sync-protocol.zh-CN.md` §2：只有机器 A 更新本文件，与 commit 同一时刻。

## 任务表

| # | 任务 | 状态 | commit | 备注 |
|---|---|---|---|---|
| A-① | `server/` 入 git（基线） | ✅ | `446dc5f` | 50 文件 16,670 行；含三个 billing latent 问题修复 |
| A-② | API 契约 mock（`server/mock/`） | ✅ | `6a3285a` | 14 项 curl 实测通过；真实 handler 落地后删除 |
| A-③ | 删 billing/redeem 包 + config 瘦身 | 🔜 下一个 | — | 收敛第一刀，后面全压它 |
| A-④ | store 12 表 → 4 表 + migrations 重写 | ⬜ | — | accounts / auth_sessions / activation_requests / licence_bindings |
| A-⑤ | activation 包（登记/状态/取票） | ⬜ | — | 契约 §11.3，签名复用 license.Signer |
| A-⑥ | admin handler（列表/确认/重签/作废） | ⬜ | — | D5 最小版占位 |
| A-⑦ | cmd/main.go 装配 + healthz + 优雅关闭 + embed 迁移 | ⬜ | — | 服务第一次真正 listen |
| A-⑧ | 全量单测 + 集成测试 | ⬜ | — | 出口标准 |

## 出口标准

- [ ] `go build/vet/test ./...` 全绿
- [ ] 一条命令起服务（含迁移），`/healthz` 返回 200
- [ ] activation/admin 集成测试通过（内存 store 端到端：登记→确认→取票→验签）

## 阻塞项

（暂无）
