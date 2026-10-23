# 官方技能包

`catalog.json` 是随 App 发布的官方技能清单。每个技能的 `package` 指向本目录下
的包目录；包目录必须至少包含 `SKILL.md`，也可以包含 `scripts/`、`references/`
和 `assets/`。

当前已随仓库发布两个官方包：

```text
shared/Skills/wechat-article-extract/SKILL.md
shared/Skills/video-information-reading/SKILL.md
```

新增官方技能时，把目录放进本目录并把 `package` 写成目录名即可。

技能 ID 必须符合 Agent Skills 命名规范：只含小写字母、数字和单个连字符，最长 64
个字符，并且与包目录名、`SKILL.md` frontmatter 中的 `name` 完全一致。场景提示词用
`{{skill:<id>}}` 行内引用技能。旧的 `wechatbridge.*` 点号 ID 会在加载时自动迁移。

打包时 macOS 会把它复制进 `.app` 的 `Resources/Skills`，Windows 会发布到输出目录的
`Resources/Skills`——两端运行期读的都是这个名字。

微信流不会解析技能的执行结果，也不会自动安装 Homebrew、pip 等外部依赖。

## 开发约定（Windows 端已实现，macOS 待同步）

- **技能 ID**：`^[a-z0-9]+(-[a-z0-9]+)*$`，最长 64（`Core/Skills/SkillId.cs`）。`catalog.json`
  出现不合规 ID 直接报错。旧点号 ID 在加载场景、导入场景包、读取
  `SkillConfirmations.json` 时迁移。
- **行内引用**：`{{skill:<id>}}`（`Core/Skills/SkillReference.cs`）。用户场景保存时
  `requiredSkillIDs` 由引用推导；`WeChatScene.EffectiveSkillIDs()` = 行内引用 ∪ 声明。
  场景包 schema 为 3，旧版客户端导入后会把标记原样显示。
- **技能库**：`%LOCALAPPDATA%\WeChatBridge\Skills\<id>\` 是唯一权威副本，注册表
  `Config\skill-library.json`，更新前备份到 `SkillBackups\`（保留 20 份）。内置官方包在
  `SkillService.Reload` 时同步；被外部修改的副本只报冲突、不覆盖
  （`Core/Skills/SkillStore.cs`）。Agent 的 skills 目录只是投放点。
- **渲染**：`ScenePrompt.Render` + `SkillService.PromptContext(agent)`，每个引用依次判定：
  Native（目标 Agent 已安装/已确认）→ Path（技能库 `SKILL.md` 路径，要求
  `AgentSkillGuides.CanReadLocalFiles` 或目标未知）→ Missing → Unknown。
  `CanReadLocalFiles` 目前是保守默认值（Codex、千问、WorkBuddy 为 true），待真机实测。
- **用户导入（ZIP）**：`SkillArchive.ExtractToStaging` 解包用户选中的压缩包——接受
  `<id>/SKILL.md` 单层包裹（即「导出 ZIP」写出的形状）和 `SKILL.md` 平铺根目录两种形态；
  路径越界/绝对路径/多个 SKILL.md 直接拒绝。frontmatter 取 `name`（缺失时回退到包裹目录名）、
  `description`、`version` 或 `metadata.version`，首个 `# ` 标题作显示名。`SkillStore.Import`
  把包写进技能库并记 `Source="import"`，旧副本（含冲突/外来目录）先移入备份；`Remove`
  同样移入备份而非直接删除。导入技能与官方 ID 冲突会被拒绝。导入技能以
  `AgentIds.All` 呈现、从技能库目录直接安装到各 Agent（`SkillInstaller` 的
  `packageDirectory` 覆盖参数）。
- **测试**：构造 `SkillService` 必须注入 `stateDirectory`，技能库会放在它下面；
  否则会写入真实用户目录。
