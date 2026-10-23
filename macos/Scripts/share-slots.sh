# The Share-menu entries 微信流 ships.
#
# macOS builds that menu from signed extension bundles: one entry is one
# `.appex`, and the list is therefore fixed when the app is built. What the user
# chooses is which of these to keep — in 微信流's own 设置 → 入口, which writes the
# same pkd election System Settings → General → Login Items & Extensions →
# Sharing does.
#
# All twelve run the same executable and tell themselves apart by DKShareAction,
# so adding an entry costs a row here and a pair of InfoPlist.strings — not a
# second copy of the import code.
#
# There is no extra row per app the user installs: an entry is a signed bundle
# inside 微信流.app and cannot be created at runtime. 「发送到自定义」 is the
# answer to that — one entry that asks which app, from a list the settings
# window writes into the app group.
#
# slot | appex name | bundle id suffix | DKShareAction | default display name
APP_BUNDLE_NAME="${APP_BUNDLE_NAME:-微信流.app}"
SHARE_SLOTS=(
	"Codex|WeChatBridgeShareCodex|ShareCodex|codex|发给 Codex"
	"Claude|WeChatBridgeShareClaude|ShareClaude|claude|发给 Claude"
	"Doubao|WeChatBridgeShareDoubao|ShareDoubao|doubao|发给豆包"
	"QwenWork|WeChatBridgeShareQwenWork|ShareQwenWork|qwen|发给千问办公"
	"WorkBuddy|WeChatBridgeShareWorkBuddy|ShareWorkBuddy|workBuddy|发给 WorkBuddy"
	"WeSight|WeChatBridgeShareWeSight|ShareWeSight|weSight|发给 WeSight"
	"DeepSeekHarness|WeChatBridgeShareDeepSeekHarness|ShareDeepSeekHarness|deepSeekHarness|发给 DeepSeek Harness"
	"Obsidian|WeChatBridgeShareObsidian|ShareObsidian|obsidian|沉淀到 Obsidian"
	"Collection|WeChatBridgeShareCollection|ShareCollection|collect|分批收集到微信流"
	"Folder|WeChatBridgeShareFolder|ShareFolder|folder|沉淀到文件夹"
	"Clipboard|WeChatBridgeShareClipboard|ShareClipboard|clipboard|复制到剪贴板"
	"Custom|WeChatBridgeShareCustom|ShareCustom|custom|发送到自定义"
)

# Artwork copied into each extension that targets another app. Clipboard,
# Custom and Folder have no destination app logo to borrow.
share_logo_name() {
	case "$1" in
		Codex) printf '%s' "04-chatgpt.png" ;;
		Claude) printf '%s' "03-claude.png" ;;
		Doubao) printf '%s' "01-doubao.png" ;;
		QwenWork) printf '%s' "02-qwen.png" ;;
		WorkBuddy) printf '%s' "06-workbuddy.png" ;;
		WeSight) printf '%s' "07-wesight.png" ;;
		Obsidian) printf '%s' "05-obsidian.png" ;;
	esac
}
