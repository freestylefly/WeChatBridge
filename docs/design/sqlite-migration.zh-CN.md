# 服务端数据库从 PostgreSQL 迁往 SQLite：可行性评估与决策

> 记录时间：2026-10-19
> 阶段定位：**评估与决策，不含落地编码**
> 代码基线：`b8029a1`，`server/` 生产代码 6,997 行 / 测试 8,172 行
> 关联文档：`buyout-flow-convergence.zh-CN.md`（买断流程与契约冻结）、`windows/docs/LICENCE-ENVELOPE-WIRE-CONTRACT.zh-CN.md`（许可证信封线格式）

---

## 结论先行

1. **可行，且范围比预期小。** 约 2,000+ 行领域代码（`auth` / `admin` / `activation` / `license` / `token` / `httpx` / `wechat`）与 182 个业务测试**完全不用动**——`store` 接口层 309 行是刻意留的缝，`store.go` 的文档明写「the interfaces here are the seam every other package is written against」。
2. **不损失任何扩展性。** 服务**架构上本来就禁止多实例**：`internal/auth/handoff.go:29` 明写内存 handoff「对单进程正确，对多进程**不正确**」，`cmd/wcb/migrate.go:23` 明写没有 advisory lock。两个既有事实合起来说明水平扩展不是被放弃的能力，而是从未拥有过。
3. **并发正确性零风险。** 三处 `FOR UPDATE` 全是**防御性冗余**（代码注释自己写明），真正承重的是条件 UPDATE 的 `RowsAffected()` 与部分唯一索引——SQLite 完整保留后两者。
4. **真正的工程量在方言重写，不在并发**：store 包生产代码 1,182 行 + 测试 1,555 行全量重写，`errors.go` 分类层重新设计。
5. **spike 实测推翻了四处初判，其中一条让难度实质下降**——`(xmax = 0)` 的返回值**生产代码里没有任何一处消费**（§4.1），它不是「唯一会静默改变买家可见行为的点」。成本下调至 **9–13 人日**。
6. **建议分两步走，先做技术验证 spike。** 最大的一块成本是测试重写（3–5 人日），在没有反馈回路的情况下直接开工会让这块工作在盲飞中消耗。

---

## 1. 范围界定：只有服务端需要动

评估前先确认了一件事，否则整个估算是错的：**客户端完全没有数据库。**

| 端 | 数据库现状 | 证据 |
| --- | --- | --- |
| macOS | 无 | 状态全在文件系统（`Inbox/Staging` → `Inbox/Ready` + `manifest.json` / `intent.json` / `state.json`） |
| Windows | 无 | `WeChatBridge.Windows.Core` 零 NuGet 引用、`net10.0` 无 WPF，状态同样走 `AppLayout` 派生出的三个根 |
| `admin-console` | 无 | 零依赖静态页，许可证号列依赖 localStorage |
| `server/` | **PostgreSQL 5.6（pgx）** | 4 张表，231 行 DDL |

因此本次迁移的影响面**完全落在 `server/`**，客户端与服务端之间冻结的契约（auth 端点、license canonical 格式、envelope 线格式）**一行不动**。

---

## 2. 规模基线

| 项 | 规模 | 说明 |
| --- | --- | --- |
| `internal/store/postgres/` 生产代码 | 1,182 行 | `auth.go` 462 / `activation.go` 504 / `postgres.go` 237 / `scan.go` 216 / `errors.go` 122 |
| 同包测试 | 1,555 行 | 49 个测试函数，全部走 `pgxmock` |
| `migrations/0001_init.up.sql` | 231 行 | 4 张表：accounts / auth_sessions / activation_requests / licence_bindings |
| `cmd/wcb/migrate.go` | 170 行 | embedded `.up.sql` + 版本表 + 每文件一个事务 |
| `store.go`（接口） | 309 行 | **零改动** |
| 领域层合计 | ~2,000 行 | **零改动** |

依赖面很窄：`pgx/v5` + `pgxmock/v3`，**没有 ORM**。配置面只有一个入口——`internal/config/config.go:96` 的 `required("WCB_DATABASE_URL")`，换库不需要动配置结构的大半。

---

## 3. 实测证据

本节全部条目为**在 SQLite 3.45.1 与 `modernc.org/sqlite` 上实际执行验证**，不是查文档推断。评估过程中有四处初判被实测推翻，记录在此以免下一轮重复踩：

| 构造 | 位置 | 实测结论 |
| --- | --- | --- |
| `ON CONFLICT (install_id) WHERE status = 'pending'` | `activation.go:61` | **可用**。冲突目标带 WHERE 谓词能精确定向 partial unique index 并执行 DO UPDATE。**不必重新设计幂等语义** |
| partial unique index（4 处） | `0001_init.up.sql:84,93,187,218,225` | **可用**。`WHERE revoked_at IS NULL` / `WHERE union_id IS NOT NULL AND union_id <> ''` 全部建得起来且行为与 PG 一致 |
| `RETURNING`（3 处） | `auth.go:41`、`activation.go:66,265` | **可用**（SQLite 3.35+）。零行时同样返回 `ErrNoRows`，与现在的 `pgx.ErrNoRows` 分支一一对应 |
| `CHECK` 约束（5 条） | `0001_init.up.sql:160-171,209` | **可用** |
| `ON DELETE SET NULL` / `CASCADE` | `0001_init.up.sql:112,130,196` | 语法可用，但**默认不生效**，必须 `PRAGMA foreign_keys=ON` |
| `timestamptz`（8 处列） | `0001_init.up.sql` | 无此类型。`DEFAULT now()` 甚至是 **prepare 期语法错误**，必须改 `CURRENT_TIMESTAMP`。**声明类型名很关键——必须写成 `TIMESTAMP`（`DATETIME`/`DATE` 行为相同），写成 `TEXT` 会让 `Scan(&time.Time)` 直接报错** |
| `gen_random_uuid()::text` | `0001_init.up.sql:48` | 无此函数。替代式 `lower(hex(randomblob(16)))` 实测可用，但 UUID 形状变为 32 位无连字符 hex（非 RFC4122）——`accounts.id` 只被当不透明串用，无害 |
| `bytea` | `0001_init.up.sql:155` | 建议显式写 `BLOB`。实测驱动按**绑定类型**存 blob，声明名不参与，写 `TEXT` 也能正确往返——但显式 `BLOB` 是声明意图的唯一诚实写法，`bytea` 落到 NUMERIC affinity 是运气不是设计 |
| `boolean` | `0001_init.up.sql:55` | **Go 侧零改动**。实测 `BOOLEAN`/`BOOL`/`BLOB`/`TEXT`/`INTEGER` 五种声明名下 `Scan(&bool)` 与 `Scan(&int64)` **全部成功**，且 `WHERE wechat_bound` / `= 1` / `= false` / `NOT wechat_bound` 四种谓词均可用。`store.Account.WechatBound` 是裸 `bool` 且列 NOT NULL，直接扫即可 |
| 唯一冲突错误形态 | — | `UNIQUE constraint failed: <表>.<列>`。**无 SQLSTATE、无约束名、无 Detail 字段**；extended code 能分主键（1555）与普通唯一索引（2067），详见 §4.2 |
| `$N` 占位符 | `auth.go` 16 处、`activation.go` 18 处 | 在 `modernc.org/sqlite` 下**语义正确**，`SET a=$2,b=$1 WHERE id=$3 AND a=$1` 与 PG 行为完全一致。**在 `mattn/go-sqlite3` 下则损坏**（`affected=0`），选驱动会改变这个结论 |
| pragma 装配 | 装配层 | **必须用 `_pragma=key(value)` 重复形式**。旧式裸参数（`journal_mode=WAL&busy_timeout=5000`）**全部静默失效**。未知 pragma 名同样静默忽略，拼错无任何信号 |

### 3.0 时间戳与 pragma 的两条硬结论（直接决定 DDL 与装配代码）

**时间戳列必须声明为 `TIMESTAMP`，且必须全部走 Go 绑定。**

实测：`Scan(&time.Time)` 在 `TIMESTAMP` 列成功（纳秒保留、`loc=UTC`），在 `TEXT` 列**直接报 `unsupported Scan, storing driver.Value type string into type *time.Time`**。

驱动绑定写出的字面量形如 `2026-03-01 04:34:56.123456789 +0000 UTC`——**空格分隔、带偏移、带时区缩写，且秒后小数去掉尾零**。它自身的字典序确实等于时间序（`04:34:56 ` < `04:34:56.4 ` < `04:34:56.123 ` < `04:34:57 `，单调递增实测为真），**但只要混入一条非驱动写入的值**，字典序立刻与时间序脱钩。详见 §7 的 R-new。

**pragma 分两类，装配方式不同**（实测）：

| pragma | 作用域 | 装配方式 |
| --- | --- | --- |
| `journal_mode=WAL` | **持久化到 db 文件** | 新库文件的默认值是 `delete`，首次打开必须设 → 仍写进 DSN |
| `busy_timeout` / `synchronous` / `foreign_keys` | **每连接** | 只能在 DSN 里写，**启动时 Exec 一次无效**（实测另一条连接仍读到 `foreign_keys=0`） |

> 注意 `_pragma` 写进 DSN 后对连接池的每条新连接都会重放，实测连开 3 条 `*sql.Conn` 结果一致。

### 3.1 四个被实测推翻的初判

| 初判 | 实测结果 | 教训 |
| --- | --- | --- |
| 「`ON CONFLICT ... WHERE` SQLite 不支持，被测语义不存在」 | 3.45 实测通过 | 冲突目标与 partial index 谓词的匹配关系必须实测，不能凭印象 |
| 「`$N` 在 SQLite 驱动下静默返回 0 行、条件守卫永不匹配」 | 对 `modernc` 驱动**不成立**；对 `mattn` 驱动**成立** | 该结论基于 `mattn`。**选驱动会改变这个结论** |
| 「`xmax = 0` 与 `RETURNING` 都要重设计」 | `RETURNING` 原生可用，只有 `xmax` 需要 | 把两者混为一谈会高估工作量 |
| 「布尔列实存 integer/0，Go 侧扫不回 `bool`——静默退化点」 | **不成立**。`Scan(&bool)` 与 `Scan(&int64)` 全部成功，四种谓词均可用 | 本条曾写进 §3 与 §7 R7，会直接误导 DDL 怎么写。**DDL 写 `BOOLEAN`，Go 侧零改动** |

### 3.2 占位符改写的真实陷阱

`$N` 可以保留。但**若改写成 `?`，实参必须按出现顺序重排**——`?` 按序绑定、`$N` 按名绑定，两者不等价。

实测：SQL `UPDATE t SET a=?, b=? WHERE id=? AND a=?` 配实参 `("OLD","NEW","x","OLD")` 得到 `a="OLD" b="NEW"`；而 PG 形态的 `$N` 版本得到 `a="NEW" b="OLD"`。**照抄不重排就会静默错位。**

这正是 `postgres.go:12` 那句注释「the row count is the answer」所防的故障类型：**不报错、不记日志、不变红**，只会安静地让 refresh token 永不轮换。

### 3.3 一条更隐蔽的红线：`$N` 与 `?` 不得在同一条语句里混用

实测 `WHERE id=? AND v=$1` 配实参 `(1,7)` → **`affected=0`（静默）**；而 `WHERE id=$1 AND v=?` 配同样实参 → `affected=1`。

仓库现状安全（`internal/store/postgres/` 里 `?` 出现 0 次），但**迁移改写时极可能引入**——因为一旦有人「顺手把部分 `$N` 改成 `?`」，就是静默错位。建议在改写后加一条断言：全仓 SQL 常量中不得同时出现两种占位符。

---

## 4. 三处必须改写的地方（spike 后仅其一仍属设计决策）

这是本次迁移的全部真难点。其余 96% 是机械改写。

### 4.1 `(xmax = 0) AS created` —— 需要改写 SQL，但返回值无人消费

`auth.go:41`：

```sql
RETURNING <columns>, (xmax = 0) AS created
```

依赖 PostgreSQL 的 heap tuple 系统列判定「本次是 INSERT 还是 UPDATE」。**SQLite 无等价系统列**，需要显式方案。

**但 spike 查清了一件更重要的事：这个返回值生产代码里没有任何一处消费。**

| 位置 | 代码 | 使用 |
| --- | --- | --- |
| `auth/service.go:447` | `account, _, err := s.store.UpsertAccountByOpenID(...)` | ❌ 丢弃 |
| `auth/service.go:470` | `if refreshed, _, err := s.store.UpsertAccountByOpenID(...)` | ❌ 丢弃 |
| `auth.go:215` | `acct, _, err := upsertAccount(ctx, tx, ...)` | ❌ 丢弃 |
| `auth_test.go:115,142` | `TestUpsertAccountByOpenIDReportsFirstSignIn` | ✅ 唯一断言处 |

因此本节**不是**风险项，也**不会**静默改变买家可见行为——没有任何买家可见行为依赖它。原判断「唯一一处 naive 移植会静默改变买家可见行为」不成立。

#### 顺带发现的一处文档债（与 SQLite 无关）

`store.go:158-160` 的注释写着「so the caller can tell a first sign-in from a return visit」，`auth.go:29-31` 声称 `xmax = 0` 是「让调用方在不做第二次查询的情况下知道首次登录」的机制。**两个调用点都没用它。** 签名保留了语义，实际无人消费——这是纯 PG 侧的文档债，建议借这次改写一并修正注释。

#### 方案实测

| 方案 | 首次插入 | 第二次 upsert | 额外查询 | 判定 |
| --- | --- | --- | --- | --- |
| A1 哨兵列 `created_at = ?` | ✅ true | ⚠️ **假阳性 true** | 0 | 否决 |
| **A2 哨兵列 `insert_marker = ?`（随机 nonce）** | ✅ true | ✅ false | **0** | **推荐** |
| B 先 `SELECT` 判存在再分流 | ✅ true | ✅ false | 1 | 次选 |

A1 的失效模式是**实测出来的**：第三次调用刻意复用第一次的 `created_at`，返回 `created=true`——明明是回访。PG 的 `xmax` 看的是 heap tuple 而非值，所以没这个问题。

**推荐 A2**：`insert_marker` 只在 INSERT 分支写入、`DO UPDATE` 从不碰它，回访时必然保留旧 nonce 而旧 nonce 永不等于本次新生成的 nonce。判定是**无关于时间的纯值比较，误判概率为零**，且零额外查询（与原 PG 写法同构，仍是一条语句一个往返）。判定发生在单语句原子作用域内，**并发语义与 PG 完全等价**。

改动位置：`0001_init.up.sql:47-57` 加列、`auth.go:32-41` 的 `RETURNING` 尾部改为 `(insert_marker = $7) AS created`、`auth.go:45` 签名加 nonce 参数（两个调用点 `auth.go:215`、`auth.go:243`）。

> **移植后必须补一条跑在真 SQLite 上的 `created` 断言。** 现有的 `TestUpsertAccountByOpenIDReportsFirstSignIn` 跑在 pgxmock 上，`accountRow(true, …)` 是测试自己手喂的行，它断言的是「这列被扫了」而非「数据库真算出了这个值」。照抄 fake 的话测试会全绿而语义可能已错——**它不是护栏，是自我实现的断言。**

### 4.2 `errors.go` 的约束名分类

`errors.go:79-106` 靠 PG 的 `SQLSTATE 23505` + `ConstraintName` 字段区分冲突：

```go
uniqueConflict:  pgErr.Code == "23505" && pgErr.ConstraintName == constraint
bindingConflict: switch pgErr.ConstraintName { licence_bindings_pkey, licence_bindings_live_idx, ... }
```

SQLite 只给列名。实测形态（`modernc.org/sqlite`，`errors.As(err, &*sqlite.Error)` 全部命中，**判据的前提成立**）：

| 冲突 | `Error()` 字符串 | extended code |
| --- | --- | --- |
| `licence_bindings` 主键（`license_id`） | `constraint failed: UNIQUE constraint failed: licence_bindings.license_id (1555)` | **1555** |
| `licence_bindings_live_idx`（`install_id WHERE revoked_at IS NULL`） | `… UNIQUE constraint failed: licence_bindings.install_id (2067)` | 2067 |
| `licence_bindings_live_account_idx`（`account_id WHERE revoked_at IS NULL`） | `… UNIQUE constraint failed: licence_bindings.account_id (2067)` | 2067 |

对照组验证 partial 语义原样保留：与既有行同 `install_id`+`account_id` 但既有行**已 revoked** → 插入成功；新行自己 revoked 而旧行 live → 插入成功。

> **修正一处初判**：code 并非「全都是 2067」。TEXT 主键冲突是 1555（`SQLITE_CONSTRAINT_PRIMARYKEY`），两个 partial unique index 是 2067。code 能分开「主键 vs 部分索引」，分不开两个索引之间——**区分力实际全部来自列名**。

#### 一条比 code 差异更硬的新发现

SQLite 在一次 INSERT 里**只暴露一个违反的约束**。实测构造「主键 `lic-A` 重复 **且** `acc1` 已有 live 绑定」的双重违反，结果只报 `account_id (2067)`，**主键那次违反被完全吞掉，1555 从未出现**。

含义：`uniqueConflict(op, constraint, err)` 的语义是「只有我点名的那个约束才算冲突」，**这个前提在 SQLite 下不成立**——它拿到的可能根本不是自己点名的那个约束。任何映射方案都无法还原「同时违反了两个」。

对本仓库影响有限：唯一会双撞的写路径是 `activation_requests` 的 upsert，它走 `ON CONFLICT (install_id) WHERE status='pending'`，撞 partial 索引由 upsert 自己吞掉，落到 `uniqueConflict` 的只剩主键。**但这是「恰好不撞」，不是设计保证。**

#### 推荐判据：表名+列名映射，写成封闭白名单

本表三个绑定冲突恰好落在三个不同列上（`license_id` / `install_id` / `account_id`），且 `bindingConflict` 对三者的答案都是同一个 `store.ErrConflict`，所以只需判「在不在白名单里」。

**必须写成封闭白名单，不能反向写。** PG 版判据是「等于我点名的约束」，天然封闭；SQLite 版若写成「是 `licence_bindings` 上的唯一冲突就算」，将来加一条 `UNIQUE(email)` 索引就会把未知冲突**静默误判**成 `ErrConflict`——正是 `errors.go:70-73` 注释警告的那件事。

**失效模式（迁移时必须写进注释）**：映射的键是列名，而列名不是唯一约束的标识符。今天 `licence_bindings.account_id` 上已挂着两个索引（`licence_bindings_live_account_idx` unique partial + `licence_bindings_account_idx` 非 unique），只是后者不产生 UNIQUE 错误才没出事。将来给同名列加一条 UNIQUE 索引，两个不同约束就塌成同一个错误字符串，判据**静默失去区分力，且不产生任何编译或测试失败**。

> 反直觉但重要：**改名安全，改列名 / 加索引不安全。** PG 版加索引默认落到 `default:` 安全分支；SQLite 列名版则会误入白名单。两者失效方向相反。

判据 B（查 `sqlite_master` 建「索引名→谓词」映射）**可行但不值得**：`sqlite_master.sql` 确实保留 `WHERE` 子句、`PRAGMA index_list` 给出 `partial` 标志，但映射再全也解决不了上面「只暴露一个约束」的问题；且三个约束映射到同一答案，多一次查询 + 一段 SQL 文本解析，正好撞上「简单、不引入额外查询」两条。若将来要按约束名给不同答案，也应做成「启动时查一次并缓存成 map」。

改动位置：`errors.go:20-27`（常量改为 `(表, 列)` 对）、`:30-33`（23505/23503 → 2067/1555）、`:79-88` 与 `:96-106`（匹配逻辑改白名单查表）、`:42-47,111-122`（`pgx.ErrNoRows` → `sql.ErrNoRows`）。调用点无需改动。

后果若不改：唯一冲突会退化成 500 而不是 409。这是**明确写在 DDL 注释里的产品行为**（`0001_init.up.sql:216-224` 的两个部分唯一索引），会静默丢失。

### 4.3 `BEGIN DEFERRED` → `BEGIN IMMEDIATE`

`postgres.go:118` 的 `s.db.Begin()` 是 deferred。四处事务体（`auth.go:190,369`、`activation.go:216,316`）形状都是「先 SELECT，后 UPDATE」。

这正是 SQLite 下唯一会产生 **`SQLITE_BUSY_SNAPSHOT`（517）** 的形状：事务已建立读快照，升级写锁时发现快照过期，**SQLite 立即返回 BUSY，根本不调用 busy handler**（等待有可能死锁）。**WAL 也救不了**——这是快照语义层面，不是锁竞争层面。

修法明确且成本极低：改用 `BEGIN IMMEDIATE`。改完后全库单写者，四处事务自然串行，上表的条件 UPDATE 全部照常生效。

**实测（WAL + `busy_timeout=5000ms`，用教科书顺序而非随机竞态复现）：**

```
c1 BEGIN; c1 SELECT          ← c1 取读快照
c2 BEGIN; c2 SELECT          ← c2 取读快照
c1 UPDATE; c1 COMMIT         ← 库已前进，c2 快照变陈旧
c2 UPDATE
→ err: database is locked (517)
→ elapsed: 39µs    ← busy_timeout 配的是 5000ms，完全未生效
```

A/B 对照（40 次迭代 × 2 并发，事务体复刻 `CompleteAuthSession` / `ConfirmActivationRequest` 的形状）：

| 组 | 铸证成功 | 条件 UPDATE 命中 0 行 | 错误 |
| --- | --- | --- | --- |
| A / DEFERRED / WAL | 40 | 0 | **40（全部 SQLITE_BUSY）** |
| B / IMMEDIATE / WAL | 40 | **40** | **0** |

> **一个必须记进实现的驱动细节**：并发竞态路径报的是 **5（`SQLITE_BUSY`）而不是 517**（40 次竞态全部落在 5 上），只有快照失效路径才报 517。所以若打算按错误码写重试逻辑，**必须同时覆盖 5 和 517**——而这恰恰又是「不如直接用 IMMEDIATE 从根上避免」的论据。

**实现建议**：`_txlock=immediate` 连接参数实测可用，但它是连接级全局开关，会连带改变 `inTx` 之外所有隐式事务的行为。**建议改写 `inTx` 本身发显式 `BEGIN IMMEDIATE`**，可控性更好。

> **这是必做项，不是可选优化。** 漏掉的后果：两卖家同按确认 → 500 而非幂等读回；付费客户并发刷新 → 500 而非 `RefreshTransient`；扫码回调 → 500 需重扫。
>
> 但请注意它的收益是**错误面的正确性**，不是数据面的正确性——DEFERRED 不会铸出两张证，见 §5。

---

## 5. 为什么并发不是风险

| 常见担心的点 | 本仓库实际情况 |
| --- | --- |
| 三处 `FOR UPDATE` 行级锁没了 | **冗余**。真正承重的是条件 UPDATE 的 `RowsAffected()`，SQLite 单语句隐式事务天然原子 |
| 整库写锁导致瓶颈 | 买家轮询（最高频流量）**全是只读**，WAL 下不参与写锁竞争 |
| 事务持锁跨越慢 I/O | **没有**。ECDSA 签名在 `admin.go:238` 之前完成，微信 HTTP 与 token 签发全在事务外。四个事务各 2–3 条语句，持锁 <1ms |
| `ReissueLicence` 的换机竞态 | 全程**没有一行 `FOR UPDATE`**，正确性完全来自两个 partial unique index，SQLite 原生支持 |

写路径全集只有 7 个端点，其中 2 个是只读轮询。在「个位数卖家 + 少量买家」的售卖形态下，写-写冲突罕见。

### 5.1 实测修订：不会铸出两张证，但 DEFERRED 会把 loser 降级成 500

§4.3 的 A/B 对照里，两组的成功铸证数**都是 40**——每轮恰好一个赢家、一枚许可证。**DEFERRED 并没有导致双重铸证**，条件 UPDATE 守卫（`confirmFlipSQL` 里的 `AND status='pending'`）本身有效。本节标题的判断被实测支持。

**但两组的失败面完全不同**，这才是 `BEGIN IMMEDIATE` 的真正价值：

| 组 | 失败者拿到什么 | 调用方的理解 |
| --- | --- | --- |
| DEFERRED | `SQLITE_BUSY` 驱动错误 | 「数据库出了问题」→ **HTTP 500** |
| IMMEDIATE | 条件 UPDATE 命中 0 行 | 「有人先我一步改了状态」→ **`store.ErrConflict` / 409**，卖家控制台读到赢家铸的那张票 |

即 `BEGIN IMMEDIATE` **不是修一个并发正确性 bug，是把一个 500 换回设计好的幂等答案**。§4.3 的收益必须这样表述，否则容易被读成「IMMEDIATE 是防双铸的」。

### 5.2 一条待验证项（勿据此下结论）

实测确认 **WAL 下只读轮询确实不挡写**（写事务持锁期间另一连接读同一行，617µs 返回旧值）。

但 DELETE 模式那一栏**没有验证到位**：该探针的写事务只走到 RESERVED 锁，未走到 commit 抢 EXCLUSIVE 的阶段，而回滚日志模式的读者阻塞正发生在那一刻。教科书顺序复现中 DELETE 甚至让 `c1` 的 **COMMIT 本身**就失败了。**因此「DELETE 模式也可以」这个结论不成立，WAL 的必要性在本轮仍未被完整证明**，按必做项对待即可。

---

## 6. 成本分布

| 工作项 | 人日 | 性质 |
| --- | --- | --- |
| schema 重写（类型 / 默认值 / BLOB / TIMESTAMP） | 1–1.5 | 机械 |
| `auth.go` + `activation.go` 查询改写 | 2–3 | 机械（`FOR UPDATE` 移除；占位符**可保留 `$N`**） |
| `scan.go` `pgtype.*` → `sql.Null*` | 0.5–1 | 机械，形状不变 |
| `postgres.go` + 驱动接线（含 `inTx` 改 IMMEDIATE、pragma 进 DSN） | 1–2 | 机械为主 |
| `cmd/wcb/migrate.go` 重写 | 0.5–1 | 机械 |
| **`errors.go` 分类重设计** | **1.5–2.5** | **设计** |
| `xmax = 0` 替代方案（哨兵列 A2） | **0.25** | 机械（加一列 + 改一行 RETURNING + 生成 nonce） |
| **测试重写（49 函数）** | **3–5** | **最大单块** |
| 集成验证 | 1–2 | 补偿性 |
| **合计** | **9–13** | 65% 机械 / 35% 设计 |

`xmax` 一项从 0.5–1.5 下调到 0.25：spike 查清返回值无人消费后，方案 A2 是纯机械改写，没有设计含量。**但这是本次 spike 带来的唯一实质降本项，其余不变。**

### 6.1 零改动清单

- `store.go` 309 行接口（不动）
- `auth` 1,259 行 / `admin` 501 行 / `activation` 396 行 / `license` / `token` / `httpx` / `wechat`
- **182 个业务测试**——证据：`internal/{auth,activation,admin}/` 对 pgx / pgconn / sql 的引用为 0；三个 fakestore 只依赖窄接口，且 `auth/fakestore_test.go:363` 已有编译期断言 `var _ store.AuthStore = (*fakeStore)(nil)`
- `admin-console`（与服务端同源部署的约束不受影响）
- 客户端全部代码、冻结的全部跨语言契约
- **`store.Account.WechatBound` 的扫描路径**——`Scan(&bool)` 在 `BOOLEAN` 列实测可用，Go 侧零改动（初判曾认为需要改，见 §3.1）

### 6.2 驱动选型：实测对比

推荐 **`modernc.org/sqlite`（纯 Go）**，不推荐 `mattn/go-sqlite3`（cgo，会炸 `CGO_ENABLED=0` 交叉编译）。实测差异如下：

| | `modernc.org/sqlite v1.34.5` | `mattn/go-sqlite3 v1.14.28` |
| --- | --- | --- |
| `$N` 乱序 / 复用 | ✅ `affected=1`（与 PG 等价） | ❌ **`affected=0`（静默损坏）** |
| `Scan(&time.Time)` @ `DATETIME`/`TIMESTAMP` | ✅ `loc=UTC`，纳秒保留 | ✅ 同 |
| `Scan(&time.Time)` @ `TEXT` | ❌ 报错 | ❌ 报错（规则相同） |
| `Scan(&bool)` @ boolean 列 | ✅ | ✅ |
| `Scan(&int64)` @ boolean 列 | ✅ | ❌ |
| 时间戳存储字面量 | `2026-03-01 04:34:56.123456789 +0000 UTC` | `2026-03-01 04:34:56.123456789+00:00` |

`modernc` 的推荐因此**加强**：它不仅省掉 cgo，还在占位符语义上比 `mattn` 更接近 PG。

> **一个一次性决策警告**：两驱动的**时间戳存储字面量不同**（` +0000 UTC` vs `+00:00`）。选驱动这个决定是**写进数据的**——建库之后换驱动，老行格式与新行格式混存，§7 R-new 的字典序问题会立刻复现。

推荐的 DSN 与连接池：

```go
dsn := "file:" + dbPath +
    "?_pragma=journal_mode(WAL)" +
    "&_pragma=busy_timeout(5000)" +
    "&_pragma=foreign_keys(ON)" +
    "&_pragma=synchronous(FULL)" +
    "&_pragma=wal_autocheckpoint(1000)" +
    "&_txlock=immediate"

db, err := sql.Open("sqlite", dsn)
if err != nil { return err }
if err := db.Ping(); err != nil { return err } // 必须：_txlock 拼错只到这一步才暴露
db.SetMaxOpenConns(1)
db.SetMaxIdleConns(1)
db.SetConnMaxLifetime(0)
```

`Ping()` 不可省：`_txlock=bogus` 不在 `sql.Open` 报错，要到第一条查询才抛。

`SetMaxOpenConns(1)` 的理由：`_txlock=immediate` 已把全库收敛成单写者，再开并行连接只是让它们排队抢同一把写锁、白白消耗 `busy_timeout`；且迁移连接与应用连接应当复用同一个 `*sql.DB`，新开就会撞上「另一条连接持有写锁」。

---

## 7. 风险清单

### 🔴 高

| ID | 风险 | 缓解 |
| --- | --- | --- |
| R1 | deferred 事务触发 `SQLITE_BUSY_SNAPSHOT`，`busy_timeout` 不生效 | **已实测坐实**（详见 §4.3）。改 `BEGIN IMMEDIATE`，四处必做。**注意收益是错误面的正确性，不是数据面的正确性** |
| R2 | 约束名分类失效 → 唯一冲突退化成 500 而非 409 | **已给出判据与实测形态**（详见 §4.2）。改封闭白名单 |
| R3 | `xmax = 0` 无替代 | **已解决**，方案 A2 哨兵列（详见 §4.1）。且返回值无人消费，风险等级已下调 |
| R4 | **CI 不跑 Go 服务端**（`ci.yml` 仅 macOS / Windows 两个 job） | 验证成本按「从零建立可信基线」计 |

### 🟡 中

| ID | 风险 | 缓解 |
| --- | --- | --- |
| **R-new** | **时间戳列的存储格式没有任何约束，混格式会让 `ORDER BY` 与范围查询静默返回错误结果集。** `TIMESTAMP` 列在 SQLite 里是 TEXT，驱动绑定写出的字面量是**变宽**的 `2006-01-02 15:04:05.999999999 -0700 MST`（秒后小数去尾零）。它自身字典序==时间序（实测单调递增为真），但只要出现一条非驱动写入的值——手写 RFC-3339 字符串、迁移脚本里的字面量、`VALUES('now()')`、或一条非 UTC 时刻——字典序立刻与时间序脱钩，`WHERE created_at >= ?` 会**少返回行且不报错**。activation 轮询、refresh token 过期、admin 台账排序全走这个形状 | (a) schema 统一 `TIMESTAMP`；(b) 写入侧只有一条路径，永远 `time.Time.UTC()`，**代码层禁止把字符串绑给时间戳列**；(c) 要硬保证则加 `CHECK` 形状约束，或干脆存 `INTEGER` unix 纳秒（排序与范围查询同样正确，代价是失去可读性与 `strftime`） |
| R5 | `synchronous` 在 WAL 下的默认值 | **实测修正**：modernc v1.34.5 即使 WAL 模式下默认也是 `2`/FULL，**掉电丢 commit 的风险在默认配置下不存在**。但仍应显式写 `FULL`——理由从「抵消默认值」变成「不让默认值随驱动/编译选项漂移」 |
| R6 | `foreign_keys` 默认关闭，`ON DELETE CASCADE` 静默失效 | **必须写进 DSN**，启动时 Exec 一次无效（实测另一条连接仍读到 0）。且**未知 `_pragma` 名是静默忽略的**——拼错等于没设。**建议启动后打一次 `PRAGMA foreign_keys` 断言读到 1** 作为装配自检 |
| ~~R7~~ | ~~布尔列扫不回 `bool`~~ | **撤销**。实测 `Scan(&bool)` 可用，DDL 写 `BOOLEAN` + Go 侧零改动即可 |
| R8 | 时间戳以字符串存储后，两个 CHECK 约束靠字符串比较求值 | 与 R-new 同源。按 R-new 的缓解措施一并解决 |
| R9 | **恢复点退化**：从 PG 分钟级 PITR → 「上次 `.backup`」 | 提高备份频率并写进运维文档 |
| R10 | 备份不能用 `cp`（会拿到撕裂的 WAL） | 必须 `VACUUM INTO` / `.backup` |
| R11 | `applyMigrations` / `appliedVersions` / `config.Load()` **零测试覆盖** | 迁移期补测试，否则无回归网 |
| R15 | `$N` 与 `?` 混用导致静默错位 | 见 §3.3。改写后加断言：全仓 SQL 常量不得同时出现两种占位符 |

### 🟢 低

| ID | 风险 | 说明 |
| --- | --- | --- |
| R12 | `.db` 无原生加密 | 但这是**隐私（PII）问题不是伪造问题**——许可证私钥不在库里、明文 token 不落盘、envelope 本就公开（客户端内嵌 SPKI 公钥可验）。`.db` 被篡改注入自签 envelope 会被客户端验签拒绝 |
| R13 | `.db` 绝不能放 NFS / SMB | SQLite 依赖 POSIX advisory locking，网络文件系统不可靠。便携部署路线图里要画红线 |
| R14 | `ALTER TABLE` 能力受限 | 4 张表每张至多几千行，重建代价可忽略 |

---

## 8. 收益侧

不是性能——性能上两者没差别。真正的理由是：

1. **去掉一整个服务依赖**。当前本地起一个真实服务端需要 6–8 步，其中「装 PG + createdb + 建 role」在仓库里没有 compose / Makefile / mise 任务承载。
2. **测试从 mock 假匹配转为真跑**。当前 store 包 1,555 行测试全部基于 `pgxmock` 的正则 SQL 文本匹配，而**整套测试零真实数据库依赖**。迁到 SQLite 后可用内存库真跑，这是验证强度的净提升。
3. **`server/mock/server.js` 得以退役**。该文件 README 自写「真实 handler 落地后本文件删除」，现在因为签名是占位符、envelope 过不了客户端内嵌公钥而无法退役。SQLite 恰好能提供「无 PG 机器上跑通**真实 handler**」的能力。
4. **部署形态与客户端收敛**。客户端本来就是单二进制分发。
5. **唯一的额外依赖仍是微信凭据**——`WCB_WECHAT_APP_ID/SECRET` 拿不到就无法端到端跑通 auth 全流程，这一点 SQLite 解决不了，不要误估。

---

## 9. 推荐路径

**不要直接开工。** 最大的一块成本是测试重写（3–5 人日），而 CI 不跑 Go 服务端，意味着这块工作在无反馈回路的情况下进行。

建议两步：

### 第一步：技术验证 spike —— **已完成（2026-10-19）**

四路并行验证全部完成，本文档 §3 / §4 / §5 / §6 / §7 已按实测结论修订。四项结果：

| Spike | 结论 | 对文档的影响 |
| --- | --- | --- |
| `xmax = 0` 替代方案 | **返回值无人消费**；推荐哨兵列 A2（随机 nonce，零额外查询） | §4.1 大幅降级，成本 0.5–1.5 → **0.25 人日** |
| `errors.go` 分类判据 | 表名+列名封闭白名单够用；**实测推翻「extended code 全是 2067」**（主键是 1555）；新发现「一次 INSERT 只暴露一个违反的约束」 | §4.2 实质修订 |
| 并发 | **517 实测坐实**，`busy_timeout` 39µs 即返回；DEFERRED **不会铸出两张证**，但把幂等 loser 降级成 500 | §4.3 从推导升级为实测；§5 加 5.1 / 5.2 |
| 驱动与 pragma | **实测推翻「布尔扫不回 bool」**（Go 侧零改动）；时间戳必须 `TIMESTAMP` 不能 `TEXT`；pragma 必须 `_pragma=key(value)` 形式 | §3 大改；R7 撤销；新增 R-new |

**唯一剩余的未验证项**：`scan.go` 里 13 处 `pgtype.Timestamptz` 能否零改动扫 `time.Time`（离线环境拿不到 `pgx`，未能实测）。它自带 `sql.Scanner`，**若能则零改动**，若不能则整个 `scan.go` 要重写。**建议在有网机器上优先验这一条**，它是当前唯一的二值未知。

### 第二步：按 spike 结论实施

- 先写只跑 SQLite 的 `store` 实现，不动生产装配
- 用 `errors.go` 的分类重写做验证，拿到真实测试反馈后再切装配层
- 补 `applyMigrations` / `appliedVersions` / `config.Load()` 的测试覆盖（当前为零）
- 补一条跑在真 SQLite 上的 `created` 断言（§4.1 末尾的提醒）
- 加两条自检断言：pragma 装配自检（R6）、SQL 常量不得混用占位符（R15）

---

## 10. 本文档的证据边界

诚实标注，以免下一轮误用：

| 结论类别 | 依据 |
| --- | --- |
| §3 全部表格条目 | **实测**（SQLite 3.45.1 + `modernc.org/sqlite` v1.34.5 + Go 1.22.2）。`bool`、`time.Time`@`TIMESTAMP`/`TEXT`、占位符三条**主进程独立复验过**，非仅采信子代理报告 |
| §4.1 / §4.2 / §4.3 / §5.1 | **实测**（子代理执行，主进程复验了 §4.1 的消费方调查与 §4.2 的双重违反行为） |
| §1、§2、§6.1 的规模与耦合统计 | **静态代码普查** |
| §5.2 的 DELETE 模式 | **未验证到位**，按必做项对待即可，不要反推「DELETE 也可以」 |
| `pgtype.Timestamptz` 的 13 处 | **未验证**（离线环境拿不到 `pgx`） |
| §6 人日估算 | **经验估算**，spike 已完成一轮修订（10–15 → 9–13），实施前应再校 |

### 10.1 一条方法论提醒

spike 过程中，**同一个 `$N` 占位符问题被两次得出相反结论**：早期基于 `mattn` 驱动的「静默零行」，在 `modernc` 上实测为 `affected=1`。最终结论是隔离复测后才成立的，且执行方自己报告「前三版探针里有四处测试自身是错的（fixture 行值与谓词不匹配）」。

因此：**本文档的「实测」条目，实施前建议对最关键的几条按同样标准再隔离复测一次**——特别是驱动相关的三条（`$N` 语义、`bool` 扫描、时间戳格式），它们各自都会让迁移在**静默**的方向上出错。
