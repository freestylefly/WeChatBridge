# PostgreSQL → SQLite 切换手册

> 本文只回答一个问题：**从现行的 PostgreSQL 主线切到 SQLite，要做什么、代价是什么、怎么退回来。**
> 迁移的技术依据在 `docs/design/sqlite-migration.zh-CN.md`（评估文档，只读），实施记录与踩坑在
> `docs/progress/sqlite-migration.md`。

## 0. 现状：两条线并存，主线不动

| 线 | 位置 | store 实现 | 状态 |
|---|---|---|---|
| PG 主线 | fork `zhuimeng2026-hub/main` | `server/internal/store/postgres/` | **现行**，未被本次工作改动 |
| SQLite 待命线 | fork `feat/sqlite-migration` | `server/internal/store/sqlite/` | 已完成并通过真库验证 |
| 不可变快照 | tag `sqlite-migration-2026-10-19`（= `455ae0e`） | 同上 | 防止分支被 force push / 重组抹掉 |

几何关系（这是"顺利"的技术前提）：

```
git rev-list --left-right --count zmuimeng/main...feat/sqlite-migration
→ 0	7          # SQLite 线落后 0、领先 7，纯线性
```

**落后 0 意味着合并是纯 fast-forward，没有任何冲突要解。** 上游 `origin`（freestylefly）无写权限，所以这一切都发生在 fork 上；要让 upstream 拿到，走 PR。

## 1. 为什么是"切换分支"而不是运行时开关

有人会问：能不能同时保留两套 store，靠 `WCB_DATABASE_URL` 的 scheme 在运行时选？结论是**不做**，理由是成本结构不对：

- 两套 store 意味着两套 SQL 常量、两套错误处理（PG 的命名约束 vs SQLite 的扩展码 + 列清单）、两套测试（PG 那套是 pgxmock 的假库，本次已证明它证明不了数据库会回答什么）。维护面直接翻倍。
- 切换成本已经是一条命令（见 §2）。为一个本来就很便宜的动作付双倍的长期维护成本不划算。
- 评估文档的结论是"换库"，不是"双栈并存"。

如果将来真出现"同一份二进制要在两种库上跑"的需求，代价是：把 `internal/store` 变成接口 + 两个实现包 + 两套测试，并把 DSN 解析做成工厂。今天不做，但路径是清楚的。

## 2. 切到 SQLite：三步

```bash
# 1) 拿到成果（分支被删也能从 tag 恢复）
git fetch zmuimeng feat/sqlite-migration
#   或：git fetch zmuimeng tag sqlite-migration-2026-10-19

# 2) 在 PG 主线上快进——落后 0，冲突数为零
git switch main
git merge --ff-only zmuimeng/feat/sqlite-migration
#   若要留 merge commit 记录：git merge --no-ff ...

# 3) 验证
cd server && go build ./... && go vet ./... && go test ./...
```

`--ff-only` 是有意的：它会在"主线已经漂移、不再是祖先"时**拒绝合并并报错**，而不是制造一个需要人去解的冲突。那种情况下该先看 §5 的再基线流程。

## 3. 部署侧必须同步改的事

代码切换只完成一半，另一半在运维：

| 项 | PG 版 | SQLite 版 |
|---|---|---|
| `WCB_DATABASE_URL` | `postgres://user:pass@host:5432/wcb` | **文件路径** `/var/lib/wcb/store.db`、`:memory:` 或 `file:` URI |
| 数据库生命周期 | 独立服务，另行部署/备份 | 进程启动时自动迁移（`migrateSchema`），幂等；第二次启动只记 `schema up to date` |
| 备份 | `pg_dump` | 用 `sqlite3 src .backup dst`（或在停写时 `VACUUM INTO`）；**直接 `cp` 一个正在写的库文件会拿到撕裂的快照** |
| 并发写 | 连接池多连接 | 连接池**刻意单连接**，写串行化；WAL + `busy_timeout(5000)` 兜底 |
| 多实例部署 | 天然支持 | **一个库文件只服务一个服务实例**；多实例要么各用各的文件，要么前置单写者 |
| 目录与权限 | 无 | 服务进程需要对 db **文件所在目录**有写权限（WAL 要建 `-wal`/`-shm` 同目录文件） |

启动时的硬门禁：`postgres://` / `postgresql://` 会被 `sqlite.New` 明确拒绝，错误信息里点名迁移文档。所以**忘了改环境变量不会静默连错库，而是启动失败**——这是有意设计的。

其他环境变量与配置项完全不变（WeChat、token、license、admin OpenID 白名单等）。

## 4. 数据怎么办

**SQLite 分支是一个新库，不是 PG 库的原地转换。** 切过去后启动即建库跑迁移，拿到一张空表。

- 当前实际状态：服务**从未在 PostgreSQL 上真正跑起来过**（沙箱无 PG，`server-convergence` 看板 A-⑦ 把"真冒烟"登记为待 DB 阻塞项）。所以现实中**没有生产数据要搬**。
- 若将来确有 PG 存量数据：迁移脚本不在本次范围内，需要自行导出再导入（两张 schema 列名一致， exporting 到 SQL 再灌进 SQLite 是直的）。评估文档未包含此项。

## 5. 回退

- **切换前 / 刚切换完未产生数据**：`git reset --hard zmuimeng/main`（或 reset 到 PG 线任一 commit），重部署。SQLite 库文件删掉即可。
- **已切并已产生数据**：先按 §3 的 SQLite 备份方式把 db 文件留一份，再回退代码。回退后 PG 库仍是切换前的状态，两边数据不互通。
- **分支漂移后再基线**（`--ff-only` 拒绝时）：不要硬合并。正确做法是在漂移后的主线上重放这 7 个 commit（`git cherry-pick f0735a1..455ae0e`），逐个解决冲突并跑 `go test ./...`；冲突面集中在 `cmd/wcb/main.go` 的装配与 `internal/config`。

## 6. 切换后验证清单

- [ ] `go build ./... && go vet ./... && go test ./...` 全绿（领域层 182 个业务测试应零改动通过）
- [ ] 启动日志出现 `migration applied version=0001_init` 与 `routes mounted`
- [ ] 第二次启动日志出现 `schema up to date`（迁移幂等）
- [ ] 库文件与 `-wal` 同目录生成，进程对该目录可写
- [ ] 故意填一个 `postgres://` 的 DSN，确认启动失败并看到指名迁移文档的错误（门禁有效）

## 7. 已知边界（切换前请读）

| 项 | 说明 |
|---|---|
| 集成测试 | activation/admin 的内存 store 端到端测试（登记→确认→取票→验签）**未做**，沿用 `server-convergence` 的 A-⑧ |
| CI | `ci.yml` 仍只有 macOS/Windows 两个 job，**没有 Go 服务端 job**；服务端测试目前靠手工跑（评估文档 R4） |
| 多实例 | 见 §3，单写者假设是硬约束 |
| pgx 残留 | `go.mod` 已无 pgx；代码里剩下的 "PostgreSQL" 字样全是解释性注释（说明某个写法为什么变了），不是活代码 |
