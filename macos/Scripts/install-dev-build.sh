#!/usr/bin/env bash
# Install dist/微信流.app where Launch Services will index it, then report what
# the system thinks of the share extension.
#
# An extension is only discovered inside an installed app: running the bundle
# from ./dist leaves pluginkit with nothing to register, which looks exactly
# like a broken Info.plist. ~/Applications keeps this out of the way of a real
# installation in /Applications.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=share-slots.sh
source "$ROOT/Scripts/share-slots.sh"
SOURCE="${SOURCE:-$ROOT/dist/$APP_BUNDLE_NAME}"
DESTINATION="${DESTINATION:-$HOME/Applications/$APP_BUNDLE_NAME}"
APP_ID="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$ROOT/Resources/Info.plist")"
LSREGISTER=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister

if [ ! -d "$SOURCE" ]; then
	echo "no build at $SOURCE — run Scripts/make-app.sh first" >&2
	exit 1
fi

# A running copy holds its own bundle open, and replacing it underneath would
# leave a half-updated app registered.
xcrun swift "$ROOT/Scripts/stop-dev-app.swift" "$APP_ID"

mkdir -p "$(dirname "$DESTINATION")"
rm -rf "$DESTINATION"
ditto "$SOURCE" "$DESTINATION"
"$LSREGISTER" -f "$DESTINATION"

echo "installed $DESTINATION"
echo
echo "share extension registration:"
pluginkit -m -p com.apple.share-services -vvv 2>/dev/null | grep -i wechatbridge || {
	echo "  not registered yet — open the app once, then re-run this script"
	exit 0
}
echo
# The switches live in 微信流 now — the app gave up its sandbox for them — so
# that is the path this prints first. Every entry is listed by its real
# identifier: the template plist holds only the first slot's, and make-app.sh
# rewrites it per slot, so printing that one alone left four entries for the
# reader to guess the suffix of.
echo "If an entry is registered but disabled, open 微信流 → 设置 → 入口 and flip it,"
echo "or manage the same setting in System Settings → Login Items & Extensions → Sharing."
echo "Per entry, from a terminal:"
for SLOT_ROW in "${SHARE_SLOTS[@]}"; do
	IFS='|' read -r _ _ ID_SUFFIX _ DISPLAY_NAME <<< "$SLOT_ROW"
	echo "  pluginkit -e use -i $APP_ID.$ID_SUFFIX   # $DISPLAY_NAME"
done
