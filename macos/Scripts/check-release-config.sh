#!/usr/bin/env bash
# Fast, secret-free checks on the metadata that ties the app, its extensions,
# the shared container and the Sparkle feed together.
#
# The four plists have to agree or WeChatBridge fails in the one way that produces no
# error anywhere: the extension writes into a container the app cannot read, and
# every share silently disappears. This belongs in normal CI; signing and
# notarization stay explicit release operations.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Repo root is one level further up since the macOS package moved under macos/;
# assets both platforms consume live in shared/, not Resources/.
REPO_ROOT="$(cd "$ROOT/.." && pwd)"
SHARED="$REPO_ROOT/shared"
source "$ROOT/Scripts/share-slots.sh"
APP_INFO="$ROOT/Resources/Info.plist"
SHARE_INFO="$ROOT/Resources/Share-Info.plist"
APP_ENTITLEMENTS="$ROOT/Resources/WeChatBridge.entitlements"
SHARE_ENTITLEMENTS="$ROOT/Resources/WeChatBridgeShare.entitlements"

plutil -lint "$APP_INFO" "$SHARE_INFO" "$APP_ENTITLEMENTS" "$SHARE_ENTITLEMENTS" >/dev/null
plutil -lint "$ROOT"/Resources/Localizations/*/*.strings "$ROOT"/Resources/ShareLocalizations/*/*/*.strings >/dev/null
python3 -m json.tool "$SHARED/Skills/catalog.json" >/dev/null

if [ "${#SHARE_SLOTS[@]}" -ne 12 ]; then
	echo "expected 12 Share-menu entries, found ${#SHARE_SLOTS[@]}" >&2
	exit 1
fi

read_plist() { /usr/libexec/PlistBuddy -c "Print :$2" "$1"; }

APP_ID="$(read_plist "$APP_INFO" CFBundleIdentifier)"
APP_GROUP="$(read_plist "$APP_INFO" DKAppGroupIdentifier)"
STORAGE_MODE="$(read_plist "$APP_INFO" DKStorageMode)"
SHARE_STORAGE_MODE="$(read_plist "$SHARE_INFO" DKStorageMode)"

case "$STORAGE_MODE" in
	app-group|shared) ;;
	*) echo "unsupported storage mode in Info.plist: $STORAGE_MODE" >&2; exit 1 ;;
esac
if [ "$SHARE_STORAGE_MODE" != "$STORAGE_MODE" ]; then
	echo "storage mode mismatch: $SHARE_STORAGE_MODE != $STORAGE_MODE" >&2
	exit 1
fi

# 1. One app group, declared in four places.
for FILE_AND_KEY in \
	"$SHARE_INFO:DKAppGroupIdentifier" \
	"$APP_ENTITLEMENTS:com.apple.security.application-groups:0" \
	"$SHARE_ENTITLEMENTS:com.apple.security.application-groups:0"; do
	FILE="${FILE_AND_KEY%%:*}"
	KEY="${FILE_AND_KEY#*:}"
	VALUE="$(read_plist "$FILE" "$KEY")"
	if [ "$VALUE" != "$APP_GROUP" ]; then
		echo "app group mismatch in $(basename "$FILE"): $VALUE != $APP_GROUP" >&2
		exit 1
	fi
done

# The compiled-in fallback is what `swift run` and the tests use. If it drifts
# from the shipped value, a development build quietly uses another container.
if ! grep -Fq "\"$APP_GROUP\"" "$ROOT/Sources/WeChatBridgeCore/AppGroup.swift"; then
	echo "AppGroup.fallbackIdentifier does not match $APP_GROUP" >&2
	exit 1
fi

# 2. Every Share-menu entry: a unique identifier under the app's, a known
#    action, and a localized display name in both languages. A slot that is
#    declared but has no name silently ships an entry called "WeChatBridgeShare".
KNOWN_ACTIONS="$(sed -nE 's/^[[:space:]]*case ([A-Za-z][A-Za-z0-9]*).*/\1/p' "$ROOT/Sources/WeChatBridgeCore/ShareAction.swift" | sort)"
SEEN_IDS=""
SEEN_NAMES=""
for SLOT_ROW in "${SHARE_SLOTS[@]}"; do
	IFS='|' read -r SLOT APPEX_NAME ID_SUFFIX SLOT_ACTION DISPLAY_NAME <<< "$SLOT_ROW"
	SLOT_ID="$APP_ID.$ID_SUFFIX"

	case "$SEEN_IDS" in *" $SLOT_ID "*) echo "duplicate extension identifier $SLOT_ID" >&2; exit 1 ;; esac
	case "$SEEN_NAMES" in *" $APPEX_NAME "*) echo "duplicate extension bundle name $APPEX_NAME" >&2; exit 1 ;; esac
	SEEN_IDS="$SEEN_IDS $SLOT_ID "
	SEEN_NAMES="$SEEN_NAMES $APPEX_NAME "

	if ! printf '%s\n' "$KNOWN_ACTIONS" | grep -Fxq "$SLOT_ACTION"; then
		echo "share slot $SLOT declares unknown action $SLOT_ACTION" >&2
		exit 1
	fi
	if [ -z "$DISPLAY_NAME" ]; then
		echo "share slot $SLOT has no default display name" >&2
		exit 1
	fi
	LOGO_NAME="$(share_logo_name "$SLOT")"
	if [ -n "$LOGO_NAME" ] && [ ! -s "$SHARED/AppLogos/$LOGO_NAME" ]; then
		echo "share slot $SLOT is missing artwork $LOGO_NAME" >&2
		exit 1
	fi
	for LANG in zh-Hans en; do
		STRINGS="$ROOT/Resources/ShareLocalizations/$SLOT/$LANG.lproj/InfoPlist.strings"
		if [ ! -s "$STRINGS" ]; then
			echo "missing $STRINGS" >&2
			exit 1
		fi
		if ! grep -Fq "CFBundleDisplayName" "$STRINGS"; then
			echo "$STRINGS does not name the Share-menu entry" >&2
			exit 1
		fi
	done
done

# Every action the code can handle should be reachable from the Share menu; an
# unreachable one is either dead code or a slot someone forgot to add.
for ACTION in $KNOWN_ACTIONS; do
	if ! printf '%s\n' "${SHARE_SLOTS[@]}" | grep -q "|$ACTION|"; then
		echo "ShareAction .$ACTION has no Share-menu entry in Scripts/share-slots.sh" >&2
		exit 1
	fi
done

# 2a. The `wechatbridge://` scheme the app routes to a settings pane. Written for the
#     「发送到自定义」 share panel, which is gone with the rest of the extension's
#     UI; the scheme stays registered and this keeps Info.plist and AppLink from
#     drifting apart, because an unregistered scheme fails by doing nothing at
#     all — no app answers and nothing says why.
SCHEME="$(read_plist "$APP_INFO" CFBundleURLTypes:0:CFBundleURLSchemes:0)"
if ! grep -Fq "scheme = \"$SCHEME\"" "$ROOT/Sources/WeChatBridgeCore/AppLink.swift"; then
	echo "Info.plist registers the URL scheme $SCHEME, which AppLink does not use" >&2
	exit 1
fi

# 3. Extension point and principal class, both of which fail silently when wrong:
#    the extension simply never appears in the Share menu.
POINT="$(read_plist "$SHARE_INFO" NSExtension:NSExtensionPointIdentifier)"
if [ "$POINT" != "com.apple.share-services" ]; then
	echo "unexpected extension point: $POINT" >&2
	exit 1
fi
PRINCIPAL="$(read_plist "$SHARE_INFO" NSExtension:NSExtensionPrincipalClass)"
if ! grep -Fqr "@objc($PRINCIPAL)" "$ROOT/Sources/WeChatBridgeShare"; then
	echo "NSExtensionPrincipalClass $PRINCIPAL has no matching @objc class" >&2
	exit 1
fi

# 4. TRUEPREDICATE is a development-only activation rule; Apple rejects a
#    submission that ships one, and it would offer WeChatBridge for content it cannot
#    save.
if grep -Fq "TRUEPREDICATE" "$SHARE_INFO"; then
	echo "Share-Info.plist still contains TRUEPREDICATE" >&2
	exit 1
fi

# 5. No entitlement WeChatBridge has no use for. The extensions handle the user's
#    files and must not be able to send them anywhere; the app is not sandboxed,
#    so this keeps its file free of anything it does not need rather than
#    granting or denying it the network — the one thing it uses the network
#    for is Sparkle, checked in section 8.
for FILE in "$APP_ENTITLEMENTS" "$SHARE_ENTITLEMENTS"; do
	for FORBIDDEN in \
		com.apple.security.network.client \
		com.apple.security.network.server \
		com.apple.security.files.all; do
		if /usr/libexec/PlistBuddy -c "Print :$FORBIDDEN" "$FILE" >/dev/null 2>&1; then
			echo "$(basename "$FILE") declares $FORBIDDEN, which WeChatBridge does not use" >&2
			exit 1
		fi
	done
done

# 5a. The two halves sit on opposite sides of the sandbox, on purpose, and both
#     sides are load-bearing.
#
#     The app must NOT be sandboxed: the 入口 pane switches Share-menu entries on
#     and off with `pluginkit -e use|ignore`, and pkd refuses a sandboxed client.
#     Measured 2026-09-05 on the signed bundle: inside the sandbox every call —
#     `-m` included — exits 1 with an empty stdout and `match: unauthorized
#     discovery flag (PKDiscoverAll)`; without the entitlement the same calls
#     exit 0 and print `+`/`-`. Re-adding app-sandbox here silently turns that
#     pane back into nine dead switches.
#
#     The extensions must stay sandboxed: they run inside another app's share
#     sheet on attachments WeChatBridge did not produce, and they never call pluginkit.
if /usr/libexec/PlistBuddy -c "Print :com.apple.security.app-sandbox" "$APP_ENTITLEMENTS" >/dev/null 2>&1; then
	echo "$(basename "$APP_ENTITLEMENTS") must not enable the App Sandbox: pluginkit election needs an unsandboxed app" >&2
	exit 1
fi
if [ "$(read_plist "$SHARE_ENTITLEMENTS" com.apple.security.app-sandbox)" != "true" ]; then
	echo "$(basename "$SHARE_ENTITLEMENTS") does not enable the App Sandbox" >&2
	exit 1
fi
SHARED_PATH="$(read_plist "$SHARE_ENTITLEMENTS" com.apple.security.temporary-exception.files.home-relative-path.read-write:0)"
if [ "$SHARED_PATH" != "/Library/Application Support/WeChatBridge/" ]; then
	echo "$(basename "$SHARE_ENTITLEMENTS") has the wrong local shared path: $SHARED_PATH" >&2
	exit 1
fi

# 6. The bundle default must stay LSUIElement so launch is quiet. The app then
#    switches to a regular activation policy and shows its Dock icon.
if [ "$(read_plist "$APP_INFO" LSUIElement)" != "true" ]; then
	echo "Info.plist must keep LSUIElement enabled" >&2
	exit 1
fi

# 7. Versions must move together: the extension reports its own version in the
#    diagnostics a field report is built from.
for KEY in CFBundleShortVersionString CFBundleVersion; do
	if [ "$(read_plist "$APP_INFO" "$KEY")" != "$(read_plist "$SHARE_INFO" "$KEY")" ]; then
		echo "$KEY differs between the app and the extension" >&2
		exit 1
	fi
done

if [ ! -x "$ROOT/Scripts/make-app.sh" ] || [ ! -x "$ROOT/Scripts/install-dev-build.sh" ]; then
	echo "build scripts must stay executable" >&2
	exit 1
fi
for REQUIRED in "$REPO_ROOT/LICENSE" "$ROOT/Resources/AppIcon.icns"; do
	if [ ! -s "$REQUIRED" ]; then
		echo "missing required distribution resource: $REQUIRED" >&2
		exit 1
	fi
done

# Skill resources ship inside the app. The catalogue may keep a package as
# null until the author supplies it, but every declared package must be a real
# directory with SKILL.md, and no symlink may escape the bundle.
SKILLS_ROOT="$SHARED/Skills"
if find "$SKILLS_ROOT" -type l | grep -q .; then
	echo "shared/Skills contains symbolic links" >&2
	exit 1
fi
while IFS= read -r PACKAGE; do
	[ -n "$PACKAGE" ] || continue
	case "$PACKAGE" in
		/*|*".."*)
			echo "skill package must be a relative directory: $PACKAGE" >&2
			exit 1
			;;
	esac
	if [ ! -s "$SKILLS_ROOT/$PACKAGE/SKILL.md" ]; then
		echo "skill package $PACKAGE is missing SKILL.md" >&2
		exit 1
	fi
done < <(sed -nE 's/.*"package"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/p' "$SKILLS_ROOT/catalog.json")

# Skills now live in the shared library and are referenced from scene prompts.
if ! grep -Fq 'stateDirectory.appendingPathComponent("Skills", isDirectory: true)' "$ROOT/Sources/WeChatBridgeCore/Skills.swift"; then
    echo "shared skill library root drifted" >&2
    exit 1
fi
for LOGO in doubao qwen claude chatgpt obsidian workbuddy; do
	if ! find "$SHARED/AppLogos" -maxdepth 1 -iname "*$LOGO*" -type f | grep -q .; then
		echo "missing Agent logo: $LOGO" >&2
		exit 1
	fi
done

# 8. Sparkle. This fork keeps Sparkle linked but deliberately has no update
#    feed until 微信流 owns its own appcast and signing key. If updates are
#    switched back on, the same HTTPS/key/signature invariants as upstream
#    apply immediately.
AUTO_CHECKS="$(read_plist "$APP_INFO" SUEnableAutomaticChecks)"
if [ "$AUTO_CHECKS" = "true" ]; then
	FEED_URL="$(read_plist "$APP_INFO" SUFeedURL)"
	case "$FEED_URL" in
		https://*) ;;
		*) echo "SUFeedURL must use HTTPS: $FEED_URL" >&2; exit 1 ;;
	esac
	KEY_BYTES="$(read_plist "$APP_INFO" SUPublicEDKey | base64 --decode 2>/dev/null | wc -c | tr -d ' ')"
	if [ "$KEY_BYTES" != "32" ]; then
		echo "SUPublicEDKey must decode to a 32-byte Ed25519 public key" >&2
		exit 1
	fi
	for BOOLEAN_KEY in SURequireSignedFeed SUVerifyUpdateBeforeExtraction; do
		if [ "$(read_plist "$APP_INFO" "$BOOLEAN_KEY")" != "true" ]; then
			echo "$BOOLEAN_KEY must be enabled when automatic updates are enabled" >&2
			exit 1
		fi
	done
	SPARKLE_STATUS="enabled"
else
	if /usr/libexec/PlistBuddy -c 'Print :SUFeedURL' "$APP_INFO" >/dev/null 2>&1; then
		echo "SUFeedURL must be removed while automatic updates are disabled" >&2
		exit 1
	fi
	SPARKLE_STATUS="disabled until WeChatBridge owns its appcast"
fi
if ! grep -Fq '.package(url: "https://github.com/sparkle-project/Sparkle.git", exact: "2.9.6")' "$ROOT/Package.swift"; then
	echo "Sparkle must remain pinned to reviewed version 2.9.6" >&2
	exit 1
fi
# Only the app may link it: a framework inside an appex would be a second
# copy to sign and notarise, for extensions that never update anything.
if grep -A3 'name: "WeChatBridgeShare",$' "$ROOT/Package.swift" | grep -q Sparkle; then
	echo "WeChatBridgeShare must not depend on Sparkle" >&2
	exit 1
fi

# 9. What the DMG and the bundle carry beside the code.
if [ ! -x "$ROOT/Scripts/make-dmg.sh" ] || [ ! -x "$ROOT/Scripts/release.sh" ]; then
	echo "release scripts must stay executable" >&2
	exit 1
fi
for REQUIRED in "$REPO_ROOT/THIRD-PARTY-NOTICES.md" "$ROOT/Resources/Licenses/Sparkle-LICENSE.txt"; do
	if [ ! -s "$REQUIRED" ]; then
		echo "missing required distribution resource: $REQUIRED" >&2
		exit 1
	fi
done
DMG_BACKGROUND="$ROOT/Resources/DMG/background.png"
if [ ! -f "$DMG_BACKGROUND" ]; then
	echo "missing Finder background: $DMG_BACKGROUND" >&2
	exit 1
fi
DMG_WIDTH="$(sips -g pixelWidth "$DMG_BACKGROUND" 2>/dev/null | awk '/pixelWidth:/ {print $2}')"
DMG_HEIGHT="$(sips -g pixelHeight "$DMG_BACKGROUND" 2>/dev/null | awk '/pixelHeight:/ {print $2}')"
DMG_DPI="$(sips -g dpiWidth "$DMG_BACKGROUND" 2>/dev/null | awk '/dpiWidth:/ {print $2}')"
# A 2x asset: Finder reads the point size from the DPI, and a 1x sheet is
# visibly soft on every Retina display.
if [ "$DMG_WIDTH" != "1672" ] || [ "$DMG_HEIGHT" != "941" ] || [ "${DMG_DPI%.*}" != "144" ]; then
	echo "DMG background must be 1672x941 pixels at 144 dpi (836x470.5 pt), got ${DMG_WIDTH}x${DMG_HEIGHT} @ ${DMG_DPI:-?} dpi" >&2
	exit 1
fi

if git -C "$ROOT" ls-files 2>/dev/null | grep -qE '\.(p12|pem|key|cer|p8)$'; then
	echo "signing material must not be tracked by Git" >&2
	exit 1
fi

echo "Release configuration validation passed: $APP_ID, ${#SHARE_SLOTS[@]} share entries, app group $APP_GROUP, Sparkle 2.9.6 $SPARKLE_STATUS."
