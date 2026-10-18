# 三分支并行：进度同步协议

> 记录时间：2026-10-18
> 适用场景：3 台机器各自 clone 同一仓库、各自 checkout 一个分支并行开发，最后合并主干部署。
> 配套文档：`branch-work-assignment.zh-CN.md`（任务与出口标准）、`buyout-flow-convergence.zh-CN.md`（架构与 API 契约 §11.3）。

## 1. 核心原则

**fork 是唯一共享通道。** 三台机器的工作区互不可见，任何「对方知道我的进度」都依赖 push 是否发生。推论：

- 没 push = 没发生。本地 commit 堆成山，别人眼里你的分支还停在昨天。
- **用户（操作三台机器的人）是中央协调者**。本协议的所有设计都服务一个场景：用户在任何一台机器上敲一条命令，就能看到三块的进度。

## 2. 三层进度可见性

| 层 | 载体 | 更新时机 | 回答的问题 |
|---|---|---|---|
| L1 代码事实 | 各分支 commit 历史 | 每个子任务完成 | 「做到哪了」——不可伪造，build/test 状态是硬证据 |
| L2 结构化看板 | `docs/progress/<分支名>.md` | 与 L1 同一时刻 | 「什么状态、卡在哪、出口达标没」——含 commit 哈希可点击跳转 |
| L3 汇合检查点 | 出口标准 checklist（分配文档） | 达到出口时 | 「能不能合了」 |

**L2 看板规则（防冲突设计）：**

- 路径按分支隔离：`docs/progress/server-convergence.md` 只有机器 A 写，`admin-console.md` 只有机器 B 写，`client-license-gate.md` 只有机器 C 写。
- 三台机器 clone 后三个看板文件都在（基线 commit 带全部三个），但各分支只改自己那个——git 层面零冲突，合并时任取其一。
- 看板与 commit **同一时刻更新**：改完代码 → 更新看板 → 一个 commit 带上 → push。不许「先 push 代码回头补看板」，补的那次永远不来。

## 3. 提交与推送纪律

1. **子任务粒度**：每个子任务一个 commit（分配文档 §4 各任务清单即粒度），禁止把 3 个任务攒成一个「大进展」commit——大 commit 是进度黑盒。
2. **push 前过出口检查**：推自己分支前，本分支的 build/test 必须绿（A: `go build/vet/test ./...`；B/C: `dotnet build/test`）。红状态推上去 = 骗别人。
3. **完成即推**：不允许本地超过 1 个 commit 未推送。攒机越大，合并时 rebase 越疼。
4. **commit message 带任务编号**：如 `feat(server): [A-③] 删除 billing/redeem 包`——`git log --oneline` 即进度流水。

## 4. 分支独占规则

- **一个分支同一时刻只允许一台机器写。** 换机器接力（如 B 的活转给 A 干）：先确认对方 24 小时内不会回来，接手机器 `git fetch fork && git checkout <分支> && git reset --hard fork/<分支>`（先确认对方已 push，丢弃自己旧快照的代价已知会丢——接力方只应在新机器上 reset）。
- 绝不在两台机器上同时推同一分支——non-fast-forward 冲突 + 可能的 force 诱惑。
- 一分支一机器是分配文档的既定前提；要加人/换人，先在此登记。

### 4.1 隔离格局：worktree（2026-10-18 20:43 起生效）

**演进过程**：最初三会话共享同一工作区（20:35 起 `Activation/` 未提交文件即为佐证，任一会话 checkout 会切走所有人脚下）→ 20:42 机器 C 建立独立 worktree，隔离完成。

**当前格局（2026-10-18 20:45 核实）：**

| 工作树 | 分支 | 归属 |
|---|---|---|
| `D:/WeChatBridge`（主工作区） | `feat/server-convergence` | 机器 A |
| `D:/WeChatBridge-C` | `feat/client-license-gate` | 机器 C |
| （未建） | `feat/admin-console` | 机器 B，派活时 `git worktree add D:/WeChatBridge-B feat/admin-console` |

**worktree 下的规则（与 §4 基础规则叠加）：**

1. **各工作树 HEAD 独立，checkout 不再互踢**——这是 worktree 隔离的核心收益。但 `.git` 全局状态仍共享：`git worktree list`、reflog、gc、stash 是全仓的。**stash 禁用**（全仓可见，最容易误伤）。
2. **一个分支同时只能被一个工作树 checkout**——`git worktree add` 遇到「already used by worktree at」就是撞了并发窗口，等对方切走或换个新分支，不要强推。
3. **每个工作树各自与 fork 同步**——主工作区 `--ff-only` 前先 fetch；C 树的提交只有从 C 树 push 才作数（各工作树 push 自己分支即可，本地分支引用共享，任一棵推上都行）。
4. **§4 的代码域隔离、精确 add、push 纪律全部继续有效**——worktree 只解决「切分支互踢」，不解决「提交了别人的文件」（见 ob-vault 2026-10-08 教训：精确 `git add <自己目录>`）。
5. 新建工作树在此登记（目录/分支/归属），撤走用 `git worktree remove`（勿直接删目录，会留僵尸记录）。

## 5. 阻塞上报

被卡住时（等外部输入、等契约、决策未定），在看板「阻塞项」区登记三要素：**卡在什么 / 需要谁提供什么 / 已尝试什么**。例：

```
🔴 A-⑦ cmd/main.go 装配：等 D5 admin 鉴权模型拍板（单密码 vs 双人分管）
```

契约变更不在阻塞范围——走设计文档流程（先改 `buyout-flow-convergence.zh-CN.md` §11.3 并推送，各分支跟进），禁止私改已冻结契约（license canonical 格式、auth 端点、httpx 错误码体系）。

## 6. 合并检查单（出口 = 合并门槛）

各分支达到出口标准后，在汇合前逐项确认：

- [ ] 本分支 build/test 全绿，出口标准 checklist 全部打勾
- [ ] 看板状态全部更新到最新 commit
- [ ] 已 push fork，本地与 fork 0/0
- [ ] 合并顺序执行：分支 A 先合主干（地基）→ B/C rebase 最新主干后分别合
- [ ] 合并后删 mock 依赖：`server/mock/` 在真实 handler 落地后删除（A 的收尾）

## 7. 用户视角：一条命令看总览

在三台机器中任意一台（已 clone 本仓库的）执行。

**分支名带 `feat/` 前缀，远端是 `fork` 不是 `origin`**——三条并行分支都只存在于
用户 fork 上，`origin` 指上游 freestyleFly，`origin/feat/client-license-gate` 这类
引用不存在，会直接报 `unknown revision`：

```bash
git fetch fork --quiet && for b in feat/server-convergence feat/admin-console feat/client-license-gate; do \
  echo "=== $b ==="; git log --oneline -3 "fork/$b"; done
```

看板总览（看阻塞与出口状态，比 log 更直观）：

```bash
for b in server-convergence admin-console client-license-gate; do \
  echo "=== $b ==="; git show "fork/feat/$b:docs/progress/$b.md"; done
```

这里分支名与文件名**故意不一样**：`git show` 要的是分支路径 `feat/<b>`，对象路径却是
`docs/progress/<b>.md`，不带前缀。

看板要从 `fork/` 读，不要 `cat docs/progress/<b>.md`。本地那份是基线 commit 带下来的
快照，只有自己那一份是新的，另外两份停在空白状态——机器 A 的任务表在本地看是空的，
远端已有 10 行内容。照原命令读会得出「别人还没开工」的错误结论，而这正是本协议要
消除的盲区。

## 8. 网络注意事项（本机已验证）

- git 网络操作（push/fetch）在沙箱类环境会**静默失败或超时**（`Empty reply from server` 是特征，同时 curl 正常——别误判成凭据问题）。必须在沙箱外执行。
- 瞬时 SSL 握手失败重试一次即可，不必排查。
