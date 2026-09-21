#!/usr/bin/env bash
# Assemble and sign 微信流.app, with its share extensions nested inside it.
#
# Local development stays fast by building the host architecture and signing
# ad-hoc. A distribution build passes both architectures, a Developer ID hash,
# and DISTRIBUTION=1; the extension is then signed before the app, because a
# signature over a bundle has to cover code that is already final.
set -euo pipefail

CONFIG="${CONFIG:-release}"

# TCC remembers a permission by code signature, so an ad-hoc signature — which
# changes on every build — makes the user re-grant Accessibility after every
# rebuild. A stable Developer ID or Apple Development identity is picked
# automatically when the keychain has one; ad-hoc stays the fallback so a clone
# without any certificate still builds.
select_identity() {
	local found
	for PREFIX in "Developer ID Application:" "Apple Development:"; do
		found="$(security find-identity -v -p codesigning 2>/dev/null \
			| awk -v prefix="$PREFIX" 'index($0, prefix) { print $2; exit }')"
		if [ -n "$found" ]; then
			printf '%s' "$found"
			return
		fi
	done
	if security find-identity -v -p codesigning 2>/dev/null | grep -Fq '"WeChatBridge Dev"'; then
		printf '%s' 'WeChatBridge Dev'
		return
	fi
	printf '%s' '-'
}
IDENTITY="${IDENTITY:-$(select_identity)}"
DISTRIBUTION="${DISTRIBUTION:-0}"
ARCHS="${ARCHS:-$(uname -m)}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$ROOT/Scripts/share-slots.sh"
APP="${APP_PATH:-$ROOT/dist/$APP_BUNDLE_NAME}"
BUILD_ROOT="${BUILD_ROOT:-$ROOT/.build/wechatbridge-bundle}"
APP_INFO="$ROOT/Resources/Info.plist"
SHARE_INFO="$ROOT/Resources/Share-Info.plist"
APP_ENTITLEMENTS="$ROOT/Resources/WeChatBridge.entitlements"
SHARE_ENTITLEMENTS="$ROOT/Resources/WeChatBridgeShare.entitlements"
VERSION="${VERSION:-$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$APP_INFO")}"
BUILD_NUMBER="${BUILD_NUMBER:-$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$APP_INFO")}"
# Overriding this retargets both bundles and both entitlement files at once.
# The checked-in default is the only value the source falls back to.
APP_GROUP="${APP_GROUP:-$(/usr/libexec/PlistBuddy -c "Print :DKAppGroupIdentifier" "$APP_INFO")}"
if [ "$DISTRIBUTION" = "1" ]; then
	STORAGE_MODE="${STORAGE_MODE:-app-group}"
else
	STORAGE_MODE="${STORAGE_MODE:-shared}"
fi
case "$STORAGE_MODE" in
	app-group|shared) ;;
	*) echo "unsupported storage mode: $STORAGE_MODE" >&2; exit 1 ;;
esac

if [ "$DISTRIBUTION" = "1" ] && [ "$IDENTITY" = "-" ]; then
	echo "DISTRIBUTION=1 requires a Developer ID Application identity" >&2
	exit 1
fi
read -r -a ARCH_LIST <<< "$ARCHS"
if [ "${#ARCH_LIST[@]}" -eq 0 ]; then
	echo "ARCHS must contain at least one architecture" >&2
	exit 1
fi

APP_BINARIES=()
SHARE_BINARIES=()
BIN_DIRS=()
for ARCH in "${ARCH_LIST[@]}"; do
	case "$ARCH" in
		arm64|x86_64) ;;
		*) echo "unsupported architecture: $ARCH" >&2; exit 1 ;;
	esac
	SCRATCH="$BUILD_ROOT/$ARCH"
	swift build -c "$CONFIG" --triple "$ARCH-apple-macosx" --scratch-path "$SCRATCH" --product WeChatBridge
	swift build -c "$CONFIG" --triple "$ARCH-apple-macosx" --scratch-path "$SCRATCH" --product WeChatBridgeShare
	BIN_DIR="$(swift build -c "$CONFIG" --triple "$ARCH-apple-macosx" --scratch-path "$SCRATCH" --show-bin-path)"
	for PRODUCT in WeChatBridge WeChatBridgeShare; do
		if [ ! -x "$BIN_DIR/$PRODUCT" ]; then
			echo "missing $PRODUCT executable for $ARCH: $BIN_DIR/$PRODUCT" >&2
			exit 1
		fi
		if ! lipo -archs "$BIN_DIR/$PRODUCT" | tr ' ' '\n' | grep -Fxq "$ARCH"; then
			echo "$PRODUCT does not contain requested architecture $ARCH" >&2
			exit 1
		fi
	done
	APP_BINARIES+=("$BIN_DIR/WeChatBridge")
	SHARE_BINARIES+=("$BIN_DIR/WeChatBridgeShare")
	BIN_DIRS+=("$BIN_DIR")
done

# Sparkle ships as a universal binary framework, so the copy SwiftPM placed
# beside the first architecture's build serves every architecture requested.
SPARKLE_FRAMEWORK="${BIN_DIRS[0]}/Sparkle.framework"
if [ ! -d "$SPARKLE_FRAMEWORK" ]; then
	echo "missing embedded framework: $SPARKLE_FRAMEWORK" >&2
	exit 1
fi

# An app extension is entered at NSExtensionMain, not main. If the linker flag
# in Package.swift is ever dropped the appex still builds, still installs, and
# silently never activates — so the entry symbol is a build invariant.
if ! nm -u "${SHARE_BINARIES[0]}" | grep -Fq '_NSExtensionMain'; then
	echo "WeChatBridgeShare is not linked against _NSExtensionMain; check the -e linker flag" >&2
	exit 1
fi

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Frameworks" "$APP/Contents/Resources" "$APP/Contents/PlugIns"

lipo -create "${APP_BINARIES[@]}" -output "$APP/Contents/MacOS/WeChatBridge"
ditto "$SPARKLE_FRAMEWORK" "$APP/Contents/Frameworks/Sparkle.framework"
for ARCH in "${ARCH_LIST[@]}"; do
	if ! lipo -archs "$APP/Contents/Frameworks/Sparkle.framework/Versions/Current/Sparkle" | tr ' ' '\n' | grep -Fxq "$ARCH"; then
		echo "Sparkle.framework is missing requested architecture $ARCH" >&2
		exit 1
	fi
done
# SwiftPM links the framework through @rpath. A shell-assembled app does not
# inherit Xcode's LD_RUNPATH_SEARCH_PATHS, so the conventional location is
# added before any signature is created. Only the app: the extensions never
# link Sparkle.
if ! otool -l "$APP/Contents/MacOS/WeChatBridge" | grep -Fq '@executable_path/../Frameworks'; then
	install_name_tool -add_rpath '@executable_path/../Frameworks' "$APP/Contents/MacOS/WeChatBridge"
fi
SHARE_BINARY="$BUILD_ROOT/WeChatBridgeShare"
lipo -create "${SHARE_BINARIES[@]}" -output "$SHARE_BINARY"

cp "$APP_INFO" "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $BUILD_NUMBER" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :DKAppGroupIdentifier $APP_GROUP" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :DKStorageMode $STORAGE_MODE" "$APP/Contents/Info.plist"

APP_ID="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$APP_INFO")"
MINIMUM_OS="$(/usr/libexec/PlistBuddy -c 'Print :LSMinimumSystemVersion' "$SHARE_INFO")"
APPEX_PATHS=()

# One executable, one bundle per share entry. The extensions differ only in identity, so
# copying the same binary keeps them honestly identical and keeps the build to a
# single compile.
for SLOT_ROW in "${SHARE_SLOTS[@]}"; do
	IFS='|' read -r SLOT APPEX_NAME ID_SUFFIX SLOT_ACTION DISPLAY_NAME <<< "$SLOT_ROW"
	SLOT_APPEX="$APP/Contents/PlugIns/$APPEX_NAME.appex"
	mkdir -p "$SLOT_APPEX/Contents/MacOS" "$SLOT_APPEX/Contents/Resources"
	cp "$SHARE_BINARY" "$SLOT_APPEX/Contents/MacOS/$APPEX_NAME"
	cp "$SHARE_INFO" "$SLOT_APPEX/Contents/Info.plist"

	PLIST="$SLOT_APPEX/Contents/Info.plist"
	/usr/libexec/PlistBuddy -c "Set :CFBundleExecutable $APPEX_NAME" "$PLIST"
	/usr/libexec/PlistBuddy -c "Set :CFBundleIdentifier $APP_ID.$ID_SUFFIX" "$PLIST"
	/usr/libexec/PlistBuddy -c "Set :CFBundleDisplayName $DISPLAY_NAME" "$PLIST"
	/usr/libexec/PlistBuddy -c "Set :DKShareAction $SLOT_ACTION" "$PLIST"
	/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" "$PLIST"
	/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $BUILD_NUMBER" "$PLIST"
	/usr/libexec/PlistBuddy -c "Set :DKAppGroupIdentifier $APP_GROUP" "$PLIST"
	/usr/libexec/PlistBuddy -c "Set :DKStorageMode $STORAGE_MODE" "$PLIST"

	LOGO_NAME="$(share_logo_name "$SLOT")"
	if [ -n "$LOGO_NAME" ]; then
		LOGO="$ROOT/Resources/AppLogos/$LOGO_NAME"
		ASSET_CATALOG="$BUILD_ROOT/icon-$SLOT.xcassets"
		APP_ICONSET="$ASSET_CATALOG/AppIcon.appiconset"
		LEGACY_ICONSET="$BUILD_ROOT/icon-$SLOT.iconset"
		PARTIAL_INFO="$BUILD_ROOT/icon-$SLOT.plist"
		if [ ! -s "$LOGO" ]; then
			echo "missing Share-menu artwork for $SLOT: $LOGO" >&2
			exit 1
		fi
		rm -rf "$ASSET_CATALOG" "$LEGACY_ICONSET"
		mkdir -p "$APP_ICONSET" "$LEGACY_ICONSET"
		cp "$ROOT/Resources/ShareIconContents.json" "$APP_ICONSET/Contents.json"
		for SPEC in "16 16" "32 16" "32 32" "64 32" "128 128" "256 128" "256 256" "512 256" "512 512" "1024 512"; do
			read -r PIXELS LOGICAL <<< "$SPEC"
			SUFFIX=""
			if [ "$PIXELS" = "$((LOGICAL * 2))" ]; then SUFFIX="@2x"; fi
			ICON_PNG="icon_${LOGICAL}x${LOGICAL}${SUFFIX}.png"
			sips -z "$PIXELS" "$PIXELS" "$LOGO" \
				--out "$APP_ICONSET/$ICON_PNG" >/dev/null
			cp "$APP_ICONSET/$ICON_PNG" "$LEGACY_ICONSET/$ICON_PNG"
		done
		# Full Xcode provides actool (Assets.car). Command Line Tools alone
		# only have iconutil, which is enough for a local share-menu icon.
		if xcrun --find actool >/dev/null 2>&1; then
			xcrun actool \
				--compile "$SLOT_APPEX/Contents/Resources" \
				--platform macosx \
				--minimum-deployment-target "$MINIMUM_OS" \
				--app-icon AppIcon \
				--output-partial-info-plist "$PARTIAL_INFO" \
				--output-format human-readable-text \
				"$ASSET_CATALOG"
			/usr/libexec/PlistBuddy -c "Add :CFBundleIconFile string AppIcon" "$PLIST"
			/usr/libexec/PlistBuddy -c "Add :CFBundleIconName string AppIcon" "$PLIST"
		else
			echo "actool unavailable; compiling $SLOT share icon with iconutil" >&2
			iconutil --convert icns --output "$SLOT_APPEX/Contents/Resources/AppIcon.icns" "$LEGACY_ICONSET"
			/usr/libexec/PlistBuddy -c "Add :CFBundleIconFile string AppIcon" "$PLIST"
		fi
	fi

	SLOT_LOCALIZATIONS="$ROOT/Resources/ShareLocalizations/$SLOT"
	if [ ! -d "$SLOT_LOCALIZATIONS" ]; then
		echo "missing localizations for share slot $SLOT: $SLOT_LOCALIZATIONS" >&2
		exit 1
	fi
	APPEX_PATHS+=("$SLOT_APPEX")
done

# Entitlements are copied and rewritten rather than edited in place: a build
# with APP_GROUP set must not leave the repository's files modified.
BUILT_ENTITLEMENTS="$BUILD_ROOT/entitlements"
mkdir -p "$BUILT_ENTITLEMENTS"
for PAIR in "WeChatBridge:$APP_ENTITLEMENTS" "WeChatBridgeShare:$SHARE_ENTITLEMENTS"; do
	NAME="${PAIR%%:*}"
	SOURCE="${PAIR#*:}"
	cp "$SOURCE" "$BUILT_ENTITLEMENTS/$NAME.entitlements"
	/usr/libexec/PlistBuddy -c \
		"Set :com.apple.security.application-groups:0 $APP_GROUP" \
		"$BUILT_ENTITLEMENTS/$NAME.entitlements"
	if [ "$STORAGE_MODE" = "app-group" ]; then
		if [ "$NAME" = "WeChatBridgeShare" ]; then
			/usr/libexec/PlistBuddy -c \
				"Delete :com.apple.security.temporary-exception.files.home-relative-path.read-write" \
				"$BUILT_ENTITLEMENTS/$NAME.entitlements"
		fi
	else
		/usr/libexec/PlistBuddy -c \
			"Delete :com.apple.security.application-groups" \
			"$BUILT_ENTITLEMENTS/$NAME.entitlements"
	fi
done

# Bundle.main must own localisations. SwiftPM's Bundle.module points back into
# the build directory and does not survive a standalone .app distribution. The
# extension needs its own copy: it is a separate bundle with a separate
# Bundle.main.
shopt -s nullglob
for LPROJ in "$ROOT/Resources/Localizations"/*.lproj; do
	ditto "$LPROJ" "$APP/Contents/Resources/$(basename "$LPROJ")"
done
for SLOT_ROW in "${SHARE_SLOTS[@]}"; do
	IFS='|' read -r SLOT APPEX_NAME _ _ _ <<< "$SLOT_ROW"
	SLOT_APPEX="$APP/Contents/PlugIns/$APPEX_NAME.appex"
	for LPROJ in "$ROOT/Resources/Localizations"/*.lproj; do
		ditto "$LPROJ" "$SLOT_APPEX/Contents/Resources/$(basename "$LPROJ")"
	done
	# The entry's name in the Share menu comes from the appex's own
	# InfoPlist.strings, so this deliberately overwrites the app's copy of that
	# one table inside each extension bundle.
	for LPROJ in "$ROOT/Resources/ShareLocalizations/$SLOT"/*.lproj; do
		ditto "$LPROJ" "$SLOT_APPEX/Contents/Resources/$(basename "$LPROJ")"
	done
done
shopt -u nullglob

if [ -f "$ROOT/Resources/AppIcon.icns" ]; then
	cp "$ROOT/Resources/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"
fi
cp "$ROOT/LICENSE" "$APP/Contents/Resources/LICENSE"
cp "$ROOT/THIRD-PARTY-NOTICES.md" "$APP/Contents/Resources/THIRD-PARTY-NOTICES.md"
ditto "$ROOT/Resources/Licenses" "$APP/Contents/Resources/Licenses"
ditto "$ROOT/Resources/AppLogos" "$APP/Contents/Resources/AppLogos"
ditto "$ROOT/Resources/Skills" "$APP/Contents/Resources/Skills"

# Repository fallbacks are useful for `swift run`, but a distributable binary
# must never reveal the builder's absolute checkout path.
if [ "$CONFIG" = "release" ]; then
	RELEASE_BINARIES=("$APP/Contents/MacOS/WeChatBridge")
	for SLOT_ROW in "${SHARE_SLOTS[@]}"; do
		IFS='|' read -r _ APPEX_NAME _ _ _ <<< "$SLOT_ROW"
		RELEASE_BINARIES+=("$APP/Contents/PlugIns/$APPEX_NAME.appex/Contents/MacOS/$APPEX_NAME")
	done
	for BINARY in "${RELEASE_BINARIES[@]}"; do
		if strings "$BINARY" | grep -Fq "$ROOT"; then
			echo "release binary contains the absolute repository path: $BINARY" >&2
			exit 1
		fi
	done
fi

SIGN_FLAGS=(--force --sign "$IDENTITY")
if [ "$DISTRIBUTION" = "1" ]; then
	SIGN_FLAGS+=(--options runtime --timestamp)
fi

# Inside out. Signing the app first would seal a hash of unsigned nested code,
# and codesign --deep is not a substitute for signing it explicitly. Sparkle
# carries executable code several levels below the framework; Downloader.xpc
# keeps its upstream entitlement metadata, everything else gets a fresh
# signature.
SPARKLE="$APP/Contents/Frameworks/Sparkle.framework"
SPARKLE_VERSION="$SPARKLE/Versions/Current"
if [ -e "$SPARKLE_VERSION/XPCServices/Downloader.xpc" ]; then
	codesign "${SIGN_FLAGS[@]}" --preserve-metadata=entitlements "$SPARKLE_VERSION/XPCServices/Downloader.xpc"
fi
for COMPONENT in \
	"$SPARKLE_VERSION/XPCServices/Installer.xpc" \
	"$SPARKLE_VERSION/Autoupdate" \
	"$SPARKLE_VERSION/Updater.app"; do
	# The XCFramework can omit an optional component while leaving its
	# top-level symlink behind. Sign what is present; the framework signature
	# below still seals the complete bundle.
	if [ -e "$COMPONENT" ]; then
		codesign "${SIGN_FLAGS[@]}" "$COMPONENT"
	fi
done
codesign "${SIGN_FLAGS[@]}" "$SPARKLE"
for SLOT_APPEX in "${APPEX_PATHS[@]}"; do
	codesign "${SIGN_FLAGS[@]}" --entitlements "$BUILT_ENTITLEMENTS/WeChatBridgeShare.entitlements" "$SLOT_APPEX"
done
codesign "${SIGN_FLAGS[@]}" --entitlements "$BUILT_ENTITLEMENTS/WeChatBridge.entitlements" "$APP"

codesign --verify --deep --strict --verbose=2 "$APP"

# The build directory is often inside Spotlight's scope. Registering both the
# build output and the installed copy gives every share extension two entries.
LSREGISTER="/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"
if [ -x "$LSREGISTER" ]; then
	"$LSREGISTER" -u "$APP" >/dev/null 2>&1 || true
fi

# The single most common way this bundle breaks is the two halves drifting onto
# different app groups, which produces an app that receives nothing and no error
# anywhere. Read it back out of the signatures, not out of the source files.
# PlistBuddy rather than `plutil -extract`: its key paths are colon-separated,
# so an entitlement key full of dots does not have to be escaped.
signed_app_group() {
	local dump
	dump="$(mktemp "$BUILD_ROOT/entitlements.XXXXXX.plist")"
	codesign -d --entitlements - --xml "$1" 2>/dev/null > "$dump"
	/usr/libexec/PlistBuddy -c 'Print :com.apple.security.application-groups:0' "$dump"
	rm -f "$dump"
}
if [ "$STORAGE_MODE" = "app-group" ]; then
	APP_GROUP_SIGNED="$(signed_app_group "$APP")"
	if [ "$APP_GROUP_SIGNED" != "$APP_GROUP" ]; then
		echo "signed app group for the app is $APP_GROUP_SIGNED, expected $APP_GROUP" >&2
		exit 1
	fi
	for SLOT_APPEX in "${APPEX_PATHS[@]}"; do
		SHARE_GROUP_SIGNED="$(signed_app_group "$SLOT_APPEX")"
		if [ "$SHARE_GROUP_SIGNED" != "$APP_GROUP" ]; then
			echo "signed app group for $(basename "$SLOT_APPEX") is $SHARE_GROUP_SIGNED, expected $APP_GROUP" >&2
			exit 1
		fi
	done
else
	if codesign -d --entitlements - --xml "$APP" 2>/dev/null \
		| /usr/libexec/PlistBuddy -c 'Print :com.apple.security.application-groups:0' /dev/stdin >/dev/null 2>&1; then
		echo "shared-storage app still carries an app group entitlement" >&2
		exit 1
	fi
fi

echo "built $APP"
echo "version $VERSION ($BUILD_NUMBER)"
echo "app group $APP_GROUP"
echo "storage $STORAGE_MODE"
echo "share entries ${#APPEX_PATHS[@]}"
echo "architectures $(lipo -archs "$APP/Contents/MacOS/WeChatBridge")"
if [ "$DISTRIBUTION" = "1" ]; then
	echo "signed for Developer ID distribution"
elif [ "$IDENTITY" = "-" ]; then
	echo "signed ad-hoc — macOS will ask for Accessibility again after each rebuild"
else
	echo "signed with a stable identity, so granted permissions survive a rebuild"
fi
