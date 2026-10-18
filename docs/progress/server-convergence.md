# 进度看板：feat/server-convergence（机器 A）

> 规则见 `docs/design/progress-sync-protocol.zh-CN.md` §2：只有机器 A 更新本文件，与 commit 同一时刻。

## ⚠️ 主线裁决（2026-10-18 22:30，用户拍板）

两台机器曾平行推进本分支并分叉：另一方（git 身份 zhuimeng2025）完成 5 表版 A-④ + A-⑤，本会话完成 4 表版 A-④。**用户裁决：以 4 表为主线**（依据：`branch-work-assignment.zh-CN.md` §2 任务 4 明文 accounts/auth_sessions/activation_requests/licence_bindings 四张表 + 用户此前拍板「本会话接管」）。本分支已 force-with-lease 推送 `53796e1`。

被覆盖的对方提交（**未丢失**，在其本地 clone 与 GitHub 对象库中按 SHA 可取，A-⑤ 的 HTTP 包将移植到 4 表 store）：

| SHA | 内容 |
|---|---|
| `3a9ab08` | docs: A-⑤ 完成，A-⑥ 进行中；§11.7 补登 R-1/R-2 |
| `885ab40` | feat: [A-⑤] activation HTTP 包（3 端点 + 17 测试 + R-1 修复） |
| `2a51cf9` | docs: A-④ 完成，A-⑤ 进行中 |
| `b133ce4` | feat: [A-④] store 11 表 → 5 表（sessions/refresh_tokens 双表 + bindings 并入 requests） |

移植口径：A-⑤ 的服务层/handler/契约字段**原样搬**（其三侧对齐结论有效），store 调用适配我方 9 方法；R-2 口径（status 透传三态、revoked 不再出票）适用并采纳；R-1（upsert 幻写 requestId）在我方「插入即单播、无 upsert」设计下不存在，登记册将注明。

## 任务表

| # | 任务 | 状态 | commit | 备注 |
|---|---|---|---|---|
| A-① | `server/` 入 git（基线） | ✅ | `446dc5f` | 50 文件 16,670 行；含三个 billing latent 问题修复 |
| A-② | API 契约 mock（`server/mock/`） | ✅ | `6a3285a` | 14 项 curl 实测通过；真实 handler 落地后删除 |
| A-③ | 删 billing/redeem 包 + config 瘦身 | ✅ | `86abca3` | 13 文件 -5,438 行；build/vet/test 全绿；已推 fork |
| A-④ | store 12 表 → 4 表 + migrations 重写 | ✅ | `88c07c9` | 4 表契约（AuthStore 9 + ActivationStore 9 + Close）；ConfirmActivationRequest/ReissueLicence 两事务；26 文件 +1754/-2321；build/vet/test 全绿；activation_test 22 用例 |
| A-⑤ | activation 包（登记/状态/取票） | ✅ | `42ccdc8` | 4 表线上移植对方 A-⑤：HTTP 包 3 端点原样搬 + store upsert 化（ON CONFLICT 保号刷新 + RETURNING 防幻写 + 跨账号回落）；reissue 无 pending 时新插已确认行（复制 anchor/amount）；licence 判活走 LiveBindingByInstallID；18 测试 + 迁移加 live_install 部分索引；§11.7 补登 R-1/R-2/移植记录；build/vet/test 9 包全绿 |
| A-⑥ | admin handler（列表/确认/重签/作废） | ✅ | `ad457b6` | Bearer + WCB_ADMIN_OPENIDS 白名单（403 非 401；§11.7 M-1 落地）；confirm Build+Issue 落 envelope 且幂等（双卖家竞态返赢家票）；reissue 委托 store 换机路径；revoke 幂等；item 契约字段 + RFC3339；29 测试；与 mock 两处严格化已登记 I-1 |
| A-⑦ | cmd/main.go 装配 + healthz + 优雅关闭 + embed 迁移 | ⬜ | — | 服务第一次真正 listen |
| A-⑧ | 全量单测 + 集成测试 | ⬜ | — | 出口标准 |

## 出口标准

- [ ] `go build/vet/test ./...` 全绿
- [ ] 一条命令起服务（含迁移），`/healthz` 返回 200
- [ ] activation/admin 集成测试通过（内存 store 端到端：登记→确认→取票→验签）

## 阻塞项

（暂无）
