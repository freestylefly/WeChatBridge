---
name: wechat-video-download
description: 把微信视频号分享链接解析并下载为本地可播放的 mp4 文件。当用户给出 weixin.qq.com/sph/ 或 channels.weixin.qq.com 视频号链接并要求下载、保存、提取视频文件时使用。
---

# 视频号视频下载

把视频号分享链接解析成真实视频流，还原加密后保存为可播放的 mp4，无需任何账号登录态。

## 输入

- 视频号分享链接：`https://weixin.qq.com/sph/XXXX`；
- 或预览页链接：`https://channels.weixin.qq.com/finder-preview/pages/sph?id=XXXX`。

## 步骤

1. 运行（脚本位于本 SKILL.md 所在目录的 `scripts/` 下）：

   ```bash
   python3 scripts/save_video.py "<视频号链接>" [输出目录]
   ```

   不指定输出目录时默认保存到 `~/Downloads/`。脚本依次完成：提取 content_id →
   在 Node 沙箱里运行签名库生成 `_sign` → 请求解析接口取回视频流地址和 XOR 密钥 →
   下载并把文件头部按密钥还原 → 写出 mp4。

2. 运行结束输出保存路径、大小和 ffprobe 时长。需要更严格的完整性检查时，
   再运行 `ffmpeg -v error -i <文件> -f null -`，无输出即为音视频解码通过。

3. 向用户报告文件路径、大小、时长。

## 依赖

- `python3`（仅用标准库）与 `node` ≥18（用于本地生成请求签名）必须可用；
- `ffprobe` 可选，仅用于校验；
- 不需要登录态或 Cookie；签名库 `scripts/enc.js` 缺失时会自动从源站下载。

## 输出

脚本成功时最后一行形如：`已保存: /path/to/{标题}.mp4 (x.xx MB)`。

## 注意

- 解析依赖第三方公共接口 `sph.miuistore.com`。接口不可用或返回非 0 `error` 时
  如实报告；备选方案：用户提供元宝 Cookie 走 `yuanbao get_parse_result` →
  `get_feed_info` 链，或微信 PC 端播放后用 `wx_channels_download` 本地代理捕获。
- 微信官方预览接口 `get_feed_info` 对匿名请求只返回元数据、不返回 `videoUrl`，
  识别到作者信息但没有播放地址是正常行为，不要据此判定视频失效。
- 返回的视频源文件头部按 `_data` 密钥逐字节 XOR 加密，必须经脚本还原；
  直接 curl 下载地址得到的是无法播放的密文。
- `coverUrl` 只是封面图，不要当成视频下载。
