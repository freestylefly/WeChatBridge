#!/usr/bin/env bash
# Point the Homebrew cask at a release that is already published. The cask
# downloads from the GitHub Release, so this runs after `gh release create`,
# not as part of building the DMG. Nothing else watches this: Sparkle carries
# its own appcast, but a stale cask just 404s at the user.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${VERSION:-$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$ROOT/Resources/Info.plist")}"
TAG="${TAG:-v$VERSION}"
REPOSITORY_SLUG="${REPOSITORY_SLUG:-freestylefly/WeChatBridge}"
TAP="${TAP:-freestylefly/homebrew-tap}"
CASK="${CASK:-wechatbridge}"
DMG_NAME="WeChatBridge-$VERSION.dmg"
URL="https://github.com/$REPOSITORY_SLUG/releases/download/$TAG/$DMG_NAME"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Take the checksum from the published asset, not from dist/. A cask is only
# correct if the file it names is the file that downloads, and this ordering
# makes running too early fail instead of publishing a wrong digest.
if ! curl -fsSL -o "$WORK/$DMG_NAME" "$URL"; then
	echo "no downloadable asset at $URL — create the GitHub Release first" >&2
	exit 1
fi
SHA="$(shasum -a 256 "$WORK/$DMG_NAME" | awk '{print $1}')"

LOCAL_SUM="$ROOT/dist/$DMG_NAME.sha256"
if [ -f "$LOCAL_SUM" ]; then
	LOCAL="$(awk '{print $1}' "$LOCAL_SUM")"
	if [ "$SHA" != "$LOCAL" ]; then
		echo "published DMG does not match dist/: $SHA vs $LOCAL" >&2
		exit 1
	fi
fi

gh repo clone "$TAP" "$WORK/tap" -- -q
FILE="$WORK/tap/Casks/$CASK.rb"
[ -f "$FILE" ] || { echo "no cask at Casks/$CASK.rb in $TAP" >&2; exit 1; }
/usr/bin/sed -i '' \
	-e "s/^  version \".*\"$/  version \"$VERSION\"/" \
	-e "s/^  sha256 \".*\"$/  sha256 \"$SHA\"/" \
	"$FILE"

if git -C "$WORK/tap" diff --quiet; then
	echo "cask $CASK already at $VERSION"
	exit 0
fi
git -C "$WORK/tap" commit -q -am "$CASK $VERSION"
git -C "$WORK/tap" push -q origin HEAD
echo "cask $CASK updated to $VERSION ($SHA)"
