# WeChatBridge 0.1.16

## 更新内容

- 支持 macOS 13（Ventura）：此前版本只能在 macOS 14 及以上打开。
- 新增「公众号文章下载」和「视频号视频下载」两个官方技能。
- 新增可选的云端账号与付费技能（开发中；在设置里登录云端账号之前不会联网），以及随 App 一起签名分发的 `wcb` 命令行桥。
- 修复首次设置向导在启动时因高度计算反复跳动的问题。
- 修复 WorkBuddy 两种 Bundle ID 只识别其中一种的问题。
- 默认构建 Universal 2（Apple Silicon + Intel），每次打包自动递增构建号。

## 安装说明

- 支持 Apple Silicon 和 Intel Mac，系统要求 macOS 13 或更高版本。
- 安装包经过 Developer ID 签名和 Apple 公证。
- 打开 DMG，将“微信流”拖入“应用程序”。
