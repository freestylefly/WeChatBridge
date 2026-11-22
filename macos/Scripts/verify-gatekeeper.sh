#!/usr/bin/env bash
# Check a DMG the way a user's Mac will see it after downloading, before it is
# uploaded anywhere. A DMG that fails here shows users "Apple could not verify
# 微信流 is free of malware" (or "is damaged") and will not open.
#
#   Scripts/verify-gatekeeper.sh dist/WeChatBridge-0.1.16.dmg
#
# It also reports the lowest macOS the app accepts, so a build that would show
# the 🚫 badge on an older Mac is caught too.
set -euo pipefail

DMG="${1:?usage: verify-gatekeeper.sh path/to/app.dmg}"
EXPECTED_MIN_OS="${EXPECTED_MIN_OS:-13.0}"
FAILED=0
fail() { echo "✗ $*" >&2; FAILED=1; }
pass() { echo "✓ $*"; }

if [ ! -f "$DMG" ]; then
	echo "missing DMG: $DMG" >&2
	exit 1
fi

# The disk image itself: Developer ID signature plus a stapled ticket.
if spctl -a -t open --context context:primary-signature -vv "$DMG" 2>&1 | grep -q 'source=Notarized Developer ID'; then
	pass "DMG is notarized"
else
	fail "DMG is not notarized (spctl: $(spctl -a -t open --context context:primary-signature "$DMG" 2>&1 | tr '\n' ' '))"
fi
if xcrun stapler validate "$DMG" >/dev/null 2>&1; then
	pass "DMG has a stapled ticket"
else
	fail "DMG has no stapled ticket; offline Macs cannot verify it"
fi

MOUNT_POINT="$(mktemp -d "${TMPDIR:-/tmp}/wcb-gatekeeper.XXXXXX")"
cleanup() { hdiutil detach "$MOUNT_POINT" -quiet 2>/dev/null || true; rmdir "$MOUNT_POINT" 2>/dev/null || true; }
trap cleanup EXIT
hdiutil attach "$DMG" -nobrowse -readonly -noautoopen -mountpoint "$MOUNT_POINT" >/dev/null

APP="$(find "$MOUNT_POINT" -maxdepth 1 -name '*.app' -print -quit)"
if [ -z "$APP" ]; then
	echo "no .app inside $DMG" >&2
	exit 1
fi

# The app: what Finder checks once it has been dragged to Applications.
ASSESSMENT="$(spctl -a -t exec -vv "$APP" 2>&1 || true)"
if printf '%s' "$ASSESSMENT" | grep -q 'source=Notarized Developer ID'; then
	pass "app is notarized ($(printf '%s' "$ASSESSMENT" | sed -n 's/^origin=//p'))"
else
	fail "app would be blocked by Gatekeeper: $(printf '%s' "$ASSESSMENT" | tr '\n' ' ')"
fi
if xcrun stapler validate "$APP" >/dev/null 2>&1; then
	pass "app has a stapled ticket"
else
	fail "app has no stapled ticket"
fi
if codesign --verify --deep --strict "$APP" 2>/dev/null; then
	pass "app signature is intact"
else
	fail "app signature does not verify"
fi

MIN_OS="$(/usr/libexec/PlistBuddy -c 'Print :LSMinimumSystemVersion' "$APP/Contents/Info.plist")"
if [ "$MIN_OS" = "$EXPECTED_MIN_OS" ]; then
	pass "runs on macOS $MIN_OS and later"
else
	fail "LSMinimumSystemVersion is $MIN_OS, expected $EXPECTED_MIN_OS"
fi
ARCHS="$(lipo -archs "$APP/Contents/MacOS/WeChatBridge")"
case " $ARCHS " in
	*" arm64 "*) ;;
	*) fail "app has no arm64 slice ($ARCHS)" ;;
esac
case " $ARCHS " in
	*" x86_64 "*) pass "universal binary ($ARCHS)" ;;
	*) fail "app has no x86_64 slice ($ARCHS); Intel Macs cannot run it" ;;
esac

if [ "$FAILED" = "1" ]; then
	echo "this DMG is not ready to publish" >&2
	exit 1
fi
echo "ready to publish: $DMG"
