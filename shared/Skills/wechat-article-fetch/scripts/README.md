# scripts/

`wechat-article.ts` 是从上游仓库**原样引入的副本（vendored）**，不要在本仓库内修改它——
改动会让它和上游分叉，并且下次同步时被覆盖。

- 上游仓库：<https://github.com/2217173240/wechat-article-scraping-skill>
- 引入版本：`1.0.0`（与 `shared/Skills/catalog.json` 中本技能的 `version` 对应）
- 许可证：MIT，版权归上游作者所有（见上游仓库的 `LICENSE`）
- 文件摘要：`sha256:60dc7b13b1d6796620a818d3aef7b98e6ce6a505fc53648df1fc2a26bcd423a4`

## 运行时要求

需要目标机器上有 **Node ≥ 22.18**（或 ≥ 23.6）：脚本是单个 `.ts` 文件，靠 Node 原生
type stripping 直接执行，**不需要** `npm install`，不需要任何依赖，也不需要浏览器。
本仓库不会、也不应代为安装 Node 或其他外部依赖；缺少 Node 时技能按 `SKILL.md` 里的
降级写法如实说明，而不是失败退出整个流程。

## 同步上游更新

1. 把上游新的 `wechat-article.ts` 覆盖到这里；
2. 同步更新本文件的「引入版本」和文件摘要；
3. **把 `shared/Skills/catalog.json` 中 `wechat-article-fetch` 的 `version` 调大**——
   `SkillStore.syncOfficial` 只在版本变大时才替换用户机器上的技能库副本，
   不调版本号已安装的用户永远拿不到更新。
