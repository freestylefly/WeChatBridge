---
name: wechat-article-download
description: 把微信公众号文章链接下载为本地 Markdown 归档，正文转 GFM、图片本地化、保留标题/公众号/发布时间。当用户给出 mp.weixin.qq.com 或 weixin.qq.com 文章链接并要求下载、保存、存档或转成 Markdown 时使用。聊天导出文件里的文章提取请用 wechat-article-extract。
---

# 公众号文章下载

抓取公众号文章正文，转成干净的 GitHub 风格 Markdown，并把正文图片下载到本地同目录，形成可离线归档的文章包。

## 输入

- 公众号文章链接：`https://mp.weixin.qq.com/s/...` 或 `weixin.qq.com` 域名链接。

## 步骤

1. 确认链接域名是 `mp.weixin.qq.com` 或 `weixin.qq.com`；其他域名不要跑脚本。
2. 运行（脚本位于本 SKILL.md 所在目录的 `scripts/` 下）：

   ```bash
   python3 scripts/wechat_article_pipeline.py "<文章链接>" --output-dir "<输出根目录>"
   ```

   不传 `--output-dir` 时输出到 `{当前目录}/articles`；加 `--save-html` 可同时保存
   清洗后的 `source.html`；`--timeout` 调整请求超时（默认 30 秒）。

3. 看命令输出的摘要：标题若是 `未命名文章`，说明链接无效或文章不可访问，
   删掉这次生成的输出目录并向用户说明。
4. 抽查生成的 Markdown：表格分隔行、图片链接、列表、引用、代码块是否正常。
5. 向用户报告输出目录和 Markdown 文件路径。

## 输出

脚本在输出根目录下按序号建文章目录：

```text
articles/
└── 01_文章标题/
    ├── 文章标题.md
    ├── image_01.jpg
    ├── image_02.png
    └── source.html        # 仅 --save-html 时
```

图片与 Markdown 同目录存放；不要单独移动图片，否则 Markdown 引用会失效。

## 依赖

- `python3`（≥3.9）+ `requests`（`pip install requests`），无其他第三方依赖；
- 不需要登录态；图片下载会自动带 `Referer` 绕过防盗链。

## 注意

- 文章被删除、受限或需要登录时抓取会失败，如实报告即可。
- 个别图片因防盗链或失效下载失败时，格式化器会自动删掉对应的失效图片引用，
  Markdown 保持干净，不需要手工修补。
- 流水线已内置常见公众号噪音清理（二维码提示、预览标签、阅读原文引导等）；
  遇到新的版式问题时更新格式化逻辑，并把案例记进 `references/formatting-edge-cases.md`。
- 脚本 vendored 自 GitHub `Yui-cx/wechat-article-to-md-skill`（MIT），
  升级时对齐上游 `scripts/wechat_article_pipeline.py`。
