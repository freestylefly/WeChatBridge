# 进度看板：feat/sqlite-migration（PostgreSQL → SQLite）

> 规则见 `docs/design/progress-sync-protocol.zh-CN.md`：看板与 commit 同一时刻更新。
> 分支基线 = `docs/sqlite-migration-evaluation`（`f0735a1`，评估文档 `docs/design/sqlite-migration.zh-CN.md`）。
> 评估文档只加不改，本看板只记录实施结果，结论一律以评估文档为准。

## 结论先行

迁移**已完成并通过真库验证**：`server/` 从 `pgx/v5` 换到 `modernc.org/sqlite v1.34.5`，领域层（`internal/activation|admin|auth|license|token|wechat`）与 `store` 接口共 182 个业务测试**零改动**通过——与评估文档「范围只在 store，领域层零改动」的判断一致。

最大的一处收益是**测试从假库回到真库**：pgxmock 的正则匹配只能证明 SQL 文本长得对，证明不了数据库会回答什么；SQLite 临时文件让真库比假库更便宜，于是断言全部改成行为级。它当场抓出了一个 PG 时代没人见过的真实缺陷（见 S-③ 备注）。

## 任务表

| # | 任务 | 状态 | commit | 备注 |
|---|---|---|---|---|
| S-① | `0001_init` schema 方言重写 + 换依赖 | ✅ | `57c1d82` | timestamptz→TIMESTAMP、bytea→BLOB、gen_random_uuid()→lower(hex(randomblob(16)))、now()→CURRENT_TIMESTAMP；新增 `insert_marker` 哨兵列替代 `(xmax = 0) AS created`（方案 A2）；表头写入两条 SQLite 铁律（时间戳走 Go 绑定、foreign_keys 是每连接 pragma）；`go mod tidy` 移除 pgx/pgxmock |
| S-② | 迁移运行器搬进 `internal/migrate` | ✅ | `bc98c8a` | 原先在 `cmd/wcb`（package main，不可被 import，R11 零覆盖的原因）；`pgxpool`→`database/sql`；导出 Plan/AppliedVersions/Apply/From；`AppliedVersions` 先查 `sqlite_master` 判表存在（不匹配 "no such table" 错误串）；每文件一个 `BEGIN IMMEDIATE` 事务 |
| S-③ | store 包换基 `postgres` → `sqlite` + 装配切换 | ✅ | `212acfa` | 去 `FOR UPDATE`（改 `BEGIN IMMEDIATE` + 条件 UPDATE）；`errors.go` 改（表名+列名）封闭白名单 + 扩展码 2067/1555；DSN pragmas 用 `_pragma=key(value)` 形式 + `_txlock=immediate`；单连接池 + foreign_keys 回读自检 |
| S-③′ | 真库测试抓出的两处生产修正 | ✅ | `47d5f74` | ① `created_at` 改绑定（DEFAULT 写秒级、驱动写亚秒级，同列两形态是 R-new 隐患）；② token 链的 live/prev 哈希列改 `sql.NullString`——**从未轮换过的链没有上一代，NULL 是合法状态，扫描进 string 会报错**；PG mock 永远喂非 NULL 且服务从未真跑过，所以这个形状此前没人见过 |
| S-④ | store 测试全量切换为真实 SQLite | ✅ | `feea0ca` | 5 文件 58 例（auth 18 / activation 16 / sqlite 11 / errors 8 / sql 5）；替代 pgxmock；含文档 §4.1 点名的真库 `created` 断言、R6 pragma 自检、R15 占位符不混用、R11 迁移运行器覆盖 |
| S-⑤ | 验证、文档与收尾 | 🔄 | — | config 注释已更新；真冒烟已通过（见下）；看板；push fork |

## 真库验证记录（S-⑤）

首次让服务在真实数据库上跑起来——`server-convergence` 看板里 A-⑦ 遗留的「真冒烟待 DB（沙箱无 postgres）」阻塞项，正是这次迁移消掉的：

```
WCB_DATABASE_URL=/tmp/wcb-smoke/store.db ./wcb
→ level=INFO msg="migration applied" version=0001_init
→ level=INFO msg="licence issuer ready" public_key=MFkw…
→ level=INFO msg="routes mounted" base_path=/
→ store.db 102400 B，store.db-wal 生成（WAL 生效）

第二次启动
→ level=INFO msg="schema up to date"      # 迁移幂等
```

`go build ./... && go vet ./... && go test ./...` 全绿；store 包 `-count=3` 与 `-race` 均绿。

## 出口标准

- [x] `go build/vet/test ./...` 全绿（10 个包，含 182 个零改动业务测试）
- [x] 一条命令起服务（含迁移），真实 SQLite 文件上启动成功、路由挂载
- [x] 迁移幂等（第二次启动 "schema up to date"）
- [ ] activation/admin 集成测试（内存 store 端到端：登记→确认→取票→验签）——沿用 A-⑧，本分支未做

## 遗留与可选

| 项 | 说明 |
|---|---|
| R4（CI） | 评估文档 §9 建议给 CI 加一个 Go 服务端 job；现有 `.github/workflows/ci.yml` 只有 macOS/Windows 两个 job。本分支**未做**，属可选增量 |
| 集成测试 | 见上出口标准最后一行，与 A-⑧ 同源 |
| `pubkey.txt` | 仓库根未跟踪文件，来自 windows 分支的签发公钥工作，与本迁移无关，未纳入任何 commit |

## 踩坑记录（对下一分支有用）

1. **同刻种子会让 ORDER BY 自由发挥**：两个 seeded 行共享一个 `created_at`/`issued_at` 时，"取最新" 的断言实际是在断言查询计划。表现是「单独跑过、整包跑挂」的 flaky。种子函数已拆出 `seedActivationRequestAt` / `seedLiveBindingAt`，凡断言顺序的用例显式给不同时刻。
2. **`ON CONFLICT` 让"第二行"消失**：同一 `install_id` 在 pending 状态下的第二次注册是**更新**（产品行为），不会新建行。要造"同一机器两行"，得先让第一行脱离 pending。
3. **CHECK 会抓出倒错的夹具**：`licence_bindings_revoked_is_dated`（`revoked_at >= issued_at`）拒绝"在签发之前吊销"。夹具若让 revoke/reissue 早于 seed 的签发时刻就会炸——这是夹具说谎，不是约束错。
4. **NULL 只有真库会告诉你**：见 S-③′。
