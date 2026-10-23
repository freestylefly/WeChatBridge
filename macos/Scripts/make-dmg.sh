#!/usr/bin/env bash
# Build a Finder-styled drag-to-Applications disk image around an existing app.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=share-slots.sh
source "$ROOT/Scripts/share-slots.sh"
APP="${1:-$ROOT/dist/$APP_BUNDLE_NAME}"
APP_NAME="$(basename "$APP")"
OUTPUT="${2:-$ROOT/dist/WeChatBridge.dmg}"
VERSION="${3:-$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$APP/Contents/Info.plist")}"
BACKGROUND="${4:-$ROOT/Resources/DMG/background.png}"
VOLUME_NAME="微信流 $VERSION"
DMG_ROOT="$ROOT/dist/dmg-root"
RW_DMG="$ROOT/dist/WeChatBridge-$VERSION-rw.dmg"
MOUNT_POINT=""

if [ ! -d "$APP" ]; then
	echo "missing app bundle: $APP" >&2
	exit 1
fi
if [ ! -f "$BACKGROUND" ]; then
	echo "missing DMG background: $BACKGROUND" >&2
	exit 1
fi

MOUNTED=0
cleanup() {
	if [ "$MOUNTED" = "1" ] && [ -n "$MOUNT_POINT" ]; then
		hdiutil detach "$MOUNT_POINT" -quiet || true
	fi
}
trap cleanup EXIT

rm -rf "$DMG_ROOT"
rm -f "$RW_DMG" "$OUTPUT"
mkdir -p "$DMG_ROOT/.background"
ditto "$APP" "$DMG_ROOT/$APP_NAME"
ln -s /Applications "$DMG_ROOT/Applications"
cp "$BACKGROUND" "$DMG_ROOT/.background/background.png"

hdiutil create \
	-volname "$VOLUME_NAME" \
	-srcfolder "$DMG_ROOT" \
	-fs HFS+ \
	-format UDRW \
	-ov "$RW_DMG" >/dev/null

ATTACH_PLIST="$(hdiutil attach "$RW_DMG" -readwrite -noverify -noautoopen -plist)"
MOUNT_POINT="$(printf '%s' "$ATTACH_PLIST" | plutil -p - | \
	awk -F ' => ' '/"mount-point"/ {gsub(/"/, "", $2); print $2; exit}')"
if [ ! -d "$MOUNT_POINT" ]; then
	echo "could not resolve mounted DMG path" >&2
	exit 1
fi
MOUNTED_VOLUME_NAME="$(basename "$MOUNT_POINT")"
MOUNTED=1

# Finder is the only thing that can write the icon layout (.DS_Store) the
# image opens with, so it is driven for a moment and closed again.
osascript <<APPLESCRIPT
tell application "Finder"
    open (POSIX file "$MOUNT_POINT" as alias)
    delay 1
    tell disk "$MOUNTED_VOLUME_NAME"
        open
        set current view of container window to icon view
        set toolbar visible of container window to false
        set statusbar visible of container window to false
        set pathbar visible of container window to false
        set the bounds of container window to {100, 100, 936, 599}
        set viewOptions to the icon view options of container window
        set arrangement of viewOptions to not arranged
        set icon size of viewOptions to 170
        set text size of viewOptions to 13
        set label position of viewOptions to bottom
        set shows item info of viewOptions to false
        set shows icon preview of viewOptions to false
        set background picture of viewOptions to ¬
            (POSIX file "$MOUNT_POINT/.background/background.png" as alias)
        set position of item "$APP_NAME" to {242, 270}
        set position of item "Applications" to {602, 270}
        update without registering applications
        delay 2
        close
    end tell
end tell
APPLESCRIPT

sync
hdiutil detach "$MOUNT_POINT" -quiet
MOUNTED=0

hdiutil convert "$RW_DMG" \
	-format UDZO \
	-imagekey zlib-level=9 \
	-ov -o "$OUTPUT" >/dev/null
hdiutil verify "$OUTPUT"

echo "created styled DMG: $OUTPUT"
