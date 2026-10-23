# 企业微信「转发到其他应用」接入微信流 · 可行性探究

- 分支：`feat/wecom-forward`（基于 `origin/main`）
- 环境：macOS 26.2 · 企业微信 5.0.11（`com.tencent.WeWorkMac`，TeamID `88L2Q4487U`）· 微信 4.1.15 · WorkBuddy 5.6.2 · 微信流 0.1.14
- 目标：让「微信流」出现在**企业微信 Mac 的「转发到其他应用」**里，从而把企业微信的合并聊天记录直接送进 AI Agent / Obsidian。

## 1. 结论（TL;DR）

| 诉求 | 结论 |
| --- | --- |
| 直接注册进企业微信「转发到其他应用」列表 | **当前不可行**。企业微信不枚举 macOS Share Extension，没有对外扩展点；该列表是腾讯内部写死的 WorkBuddy 私有通道，且受服务端开关控制。 |
| 让「企业微信的聊天记录」进入微信流 | **可行，需要换路径**。推荐「导出 ZIP 监听」或「两跳（企业微信→微信→微信流）」。 |

一句话：不是微信流缺一个 appex，而是企业微信那头根本没有可挂载的钩子。

## 2. 关键证据（本机实测）

### 2.1 企业微信不消费系统分享扩展

- 企业微信 App 内**没有任何 `.appex`**（`Contents/PlugIns` 只有 Qt 插件：bearer / iconengines / imageformats / platforms / styles）。
- 全量搜索 `/Applications/企业微信.app/Contents`：命中 `com.apple.share-services` 的**只有 Chromium Embedded Framework**（CEF 通用代码），主二进制与各 framework 均无 `NSExtensionPointName` / `pluginkit` / 扩展枚举调用。
- 反证（用户截图）：本机微信流的 Share Extension 已注册且多个处于启用状态
  （`pluginkit -m -p com.apple.share-services -v` → `+ com.xiangming.wechatbridge.ShareCodex / ShareDoubao / ShareObsidian`），
  但企业微信的「转发到其他应用」只列出 WorkBuddy。若它走系统分享列表，这三个入口必然出现。
- 唯一的反向信号：主二进制里有一条 ObjC 类型编码
  `@"NSSharingServicePicker"40@0:8@"NSTextView"16@"NSSharingServicePicker"24@"NSArray"32`
  （可能来自通用的 NSTextView sharing 模板）。但该面板的实际代码路径全部落在 `WEWWorkbuddy*`，
  且只要它真的消费系统分享列表，本机已启用的微信流入口就不可能出现不了——两条证据互相印证。

### 2.2 企业微信的「转发到其他应用」是 WorkBuddy 私有通道

字符串表 `Contents/Resources/Base.lproj/Localizable_autogen.strings`：

```
WWLK$::MSG::FORWARD::ZFDQTYY  = "转发到其他应用";
WWLK$::MSG::FORWARD::ZDQTQY   = "转发到其他企业";
WWLK$::WORKBUDDY::COMPRESS_NOTICE = "转发到其他应用时，聊天记录将会被打包成压缩文件进行发送。";
```

主二进制 `Contents/MacOS/企业微信` 中的符号与日志：

```
src/mac/UI/Conversation/Workbuddy/WEWWorkbuddyHelper.mm
src/mac/UI/Conversation/Workbuddy/WEWWorkbuddyShareManager.mm
+[WEWWorkbuddyHelper exportMessagesToZip:progress:completion:]     // 企业微信自己把聊天记录压成 ZIP
+[WEWWorkbuddyHelper p_isForwardToWorkBuddyConfigEnabled]          // 服务端开关
+[WEWWorkbuddyHelper p_isForwardToWorkBuddyMaskEnabled]
-[WEWWorkbuddyShareManager p_isDeliverableZipAtPath:]
-[WEWWorkbuddyShareManager p_deliverZipToWorkBuddy:]
-[WEWWorkbuddyShareManager p_doDeliverZipToWorkBuddy:]
_workbuddyPendingZipPath / setWorkbuddyPendingZipPath:
workbuddy://                                                       // 用 URL Scheme 交给 WorkBuddy
[fwd2wb] workbuddy path:%@ version:%@ (from bundle / from plist)
[fwd2wb] archive begin: %lu entries, %llu bytes -> %@
[fwd2wb] attach outside wework storage: %@
```

即：选中消息 → 企业微信导出 ZIP 到 `workbuddyPendingZipPath` → 定位并打开 WorkBuddy（`workbuddy://`）。
目标 App 是由企业微信按 bundle/plist **主动定位**的，第三方无法注册、也无法插入。

WorkBuddy 侧对应两个接入点：
- 微信：`/Applications/WorkBuddy.app/Contents/PlugIns/WechatShare.appex`（`com.tencent.workbuddy.mac.WechatShare`，激活规则要求恰好 1 个 `public.zip-archive`）。
- 企业微信：`workbuddy://` URL Scheme（`CFBundleURLSchemes = [workbuddy]`）。

### 2.3 对照：个人微信走的是系统分享扩展

`grep -ral "com.apple.share-services" /Applications/WeChat.app/Contents` 命中
`WeChatAppEx Framework` 与 `WeChatMacShare.appex/Info.plist`——个人微信会枚举系统 Share Extension，
这正是微信流九个入口能出现在微信「转发到其他应用」的原因。企业微信没有这一层。

## 3. 可行路径

### A. 蹭 WorkBuddy 的导出 ZIP（最接近原生，推荐先验证）
企业微信点「WorkBuddy」时会先把聊天记录导出成 ZIP（`[fwd2wb] archive ... -> %@`，并可能 `attach outside wework storage`）。
若该 ZIP 有稳定的落盘目录且交付后不被立刻删除，微信流可新增一个「企业微信导出」入口：
用 FSEvents 监听该目录，发现新 ZIP 即创建批次，复用现有 Inbox / 场景 / 投递 / Obsidian 全链路。

- 待验证：ZIP 落盘路径（`workbuddyPendingZipPath` 的构造规则）、是否可读、生命周期（是否交付后删除）、并发多批次如何区分。
- 体验代价：触发器仍是企业微信里的「WorkBuddy」按钮，语义上要解释为「触发导出」。

### B. 两跳：企业微信 → 微信 → 微信流（零改动，今天可用）
企业微信转发面板本身有「微信」入口，可把聊天记录转给自己的微信（文件传输助手/自己），
再在微信里转发给微信流的九个入口。

- 待验证：企业微信→微信是否支持**合并聊天记录**（而非逐条文本）。
- 代价：多两步，且内容会经过微信。

### C. 拖拽出文件
企业微信有拖出文件能力（`WEWChattingViewDragFileDelegate`、`wew_addDragItemAsFile:imageAsFile:`），
但这是文件/图片卡片，合并聊天记录能否拖出成 ZIP 未验证。
若能，微信流可加 Dock 图标拖放 / 拖拽收集窗口入口。

### D. 推动企业微信开放扩展点
只有腾讯侧决策才能让第三方 App 出现在该列表（例如改为枚举 `com.apple.share-services`）。不可控。

### E. 读取企业微信本地数据库
**排除**。违反微信流隐私红线（不读数据库、不解密、不注入）。

## 4. 建议的下一步实验

1. 起一个 FSEvents / `fs_usage` 监听（覆盖 `~/Library/Containers/com.tencent.WeWorkMac`、`~/Library/Application Support/com.tencent.workbuddy.mac`、`/var/folders`），
   请用户在企业微信里对一条合并记录点一次「WorkBuddy」，抓取 ZIP 的真实落盘路径与生命周期。
2. 验证 B 路径：企业微信合并记录 → 转发到微信，确认到微信后仍是可再转发的聊天记录。
3. 若 1 成立，在微信流侧做最小 PoC：新增「企业微信导出」入口 + 目录监听 + 复用现有投递。
4. 若 1/3 均不成立，则对外结论为「企业微信侧暂无接入可能」，只保留 B 作为人工兜底路径。

## 5. 复现命令

```bash
# 系统分享扩展注册情况
pluginkit -m -p com.apple.share-services -v

# 企业微信是否枚举分享扩展
grep -ral "com.apple.share-services" /Applications/企业微信.app/Contents
find /Applications/企业微信.app -name "*.appex" -maxdepth 5

# 企业微信 WorkBuddy 私有通道
strings -a "/Applications/企业微信.app/Contents/MacOS/企业微信" | grep -iE "Workbuddy|fwd2wb|workbuddy://"

# 个人微信用的是系统分享扩展
grep -ral "com.apple.share-services" /Applications/WeChat.app/Contents
```
