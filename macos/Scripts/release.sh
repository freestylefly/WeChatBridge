#!/usr/bin/env bash
# Build the one canonical WeChatBridge release DMG used by GitHub Releases, Sparkle
# and Homebrew. The source commit and tag must already exist; secrets stay in
# the login keychain and are referenced only by identity hash / profile name.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# site/ (the Sparkle feed + release notes GitHub Pages serves) lives at the
# repository root, one level above this macOS package.
REPO_ROOT="$(cd "$ROOT/.." && pwd)"
# shellcheck source=share-slots.sh
source "$ROOT/Scripts/share-slots.sh"
VERSION="${VERSION:-$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$ROOT/Resources/Info.plist")}"
BUILD_NUMBER="${BUILD_NUMBER:-$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$ROOT/Resources/Info.plist")}"
TAG="${TAG:-v$VERSION}"
ARCHS="${ARCHS:-arm64 x86_64}"
NOTARIZE="${NOTARIZE:-1}"
GENERATE_APPCAST="${GENERATE_APPCAST:-1}"
REQUIRE_TAG="${REQUIRE_TAG:-1}"
REQUIRE_CLEAN="${REQUIRE_CLEAN:-1}"
REPOSITORY_SLUG="${REPOSITORY_SLUG:-freestylefly/WeChatBridge}"
PAGES_URL="${PAGES_URL:-https://freestylefly.github.io/WeChatBridge}"
# The `notarytool store-credentials` profile to submit with. Apple issues
# notarization credentials per Apple ID and team, not per app, so one stored
# profile covers everything team DLKMC3ZRZQ signs. Keep a project-specific name
# so the release prerequisite and the Keychain entry are easy to identify.
NOTARY_PROFILE="${NOTARY_PROFILE:-WeChatBridge-Notary}"
APP="$ROOT/dist/$APP_BUNDLE_NAME"
APP_ZIP="$ROOT/dist/WeChatBridge-$VERSION.app.zip"
DMG="$ROOT/dist/WeChatBridge-$VERSION.dmg"
UPDATES_DIR="$ROOT/dist/updates"

if [ "$REQUIRE_CLEAN" = "1" ] && [ -n "$(git -C "$ROOT" status --porcelain)" ]; then
	echo "release requires a clean Git working tree" >&2
	exit 1
fi
if [ "$REQUIRE_TAG" = "1" ]; then
	HEAD_TAG="$(git -C "$ROOT" describe --tags --exact-match HEAD 2>/dev/null || true)"
	if [ "$HEAD_TAG" != "$TAG" ]; then
		echo "HEAD must be tagged $TAG before creating a release" >&2
		exit 1
	fi
fi

IDENTITY="${IDENTITY:-$(security find-identity -v -p codesigning 2>/dev/null | awk '/Developer ID Application:/ {print $2; exit}')}"
if [ -z "$IDENTITY" ]; then
	echo "no valid Developer ID Application identity was found" >&2
	exit 1
fi

notarize_artifact() {
	local artifact="$1"
	local label="$2"
	local result="$ROOT/dist/notary-$label-result.json"
	local log="$ROOT/dist/notary-$label-log.json"
	local submission_id status

	xcrun notarytool submit "$artifact" \
		--keychain-profile "$NOTARY_PROFILE" \
		--wait --output-format json > "$result"
	submission_id="$(plutil -extract id raw -o - "$result")"
	status="$(plutil -extract status raw -o - "$result")"
	xcrun notarytool log "$submission_id" \
		--keychain-profile "$NOTARY_PROFILE" > "$log"
	if [ "$status" != "Accepted" ]; then
		echo "Apple notarization status for $label: $status" >&2
		echo "see $log" >&2
		exit 1
	fi
}

if [ "$NOTARIZE" = "1" ]; then
	# Fail before the expensive Universal 2 build if the Keychain profile is
	# missing, expired or tied to the wrong Developer team.
	xcrun notarytool history --keychain-profile "$NOTARY_PROFILE" >/dev/null
fi

VERSION="$VERSION" BUILD_NUMBER="$BUILD_NUMBER" ARCHS="$ARCHS" \
	IDENTITY="$IDENTITY" DISTRIBUTION=1 CONFIG=release \
	"$ROOT/Scripts/make-app.sh"

# The app needs its own stapled ticket before it is sealed inside the DMG.
# That preserves offline Gatekeeper validation after Finder, Homebrew or
# Sparkle copies the bundle out of the mounted image.
if [ "$NOTARIZE" = "1" ]; then
	rm -f "$APP_ZIP"
	ditto -c -k --keepParent "$APP" "$APP_ZIP"
	notarize_artifact "$APP_ZIP" app
	xcrun stapler staple "$APP"
	xcrun stapler validate "$APP"
fi

"$ROOT/Scripts/make-dmg.sh" \
	"$APP" "$DMG" "$VERSION" "$ROOT/Resources/DMG/background.png"
codesign --force --sign "$IDENTITY" --timestamp "$DMG"
hdiutil verify "$DMG"

if [ "$NOTARIZE" = "1" ]; then
	notarize_artifact "$DMG" dmg
	xcrun stapler staple "$DMG"
	xcrun stapler validate "$DMG"
else
	echo "warning: NOTARIZE=0; this DMG is not publishable" >&2
fi

(cd "$(dirname "$DMG")" && shasum -a 256 "$(basename "$DMG")" > "$(basename "$DMG").sha256")

if [ "$GENERATE_APPCAST" = "1" ]; then
	if [ "$NOTARIZE" != "1" ]; then
		echo "refusing to generate a production appcast from an unstapled DMG" >&2
		exit 1
	fi
	SPARKLE_TOOLS="$ROOT/.build/artifacts/sparkle/Sparkle/bin"
	if [ ! -x "$SPARKLE_TOOLS/generate_appcast" ]; then
		echo "missing Sparkle generate_appcast tool; run swift package resolve" >&2
		exit 1
	fi
	rm -rf "$UPDATES_DIR"
	mkdir -p "$UPDATES_DIR"
	# The previous feed is the input, so the new appcast keeps every older
	# item and only appends this release.
	if [ -f "$REPO_ROOT/site/appcast.xml" ]; then
		cp "$REPO_ROOT/site/appcast.xml" "$UPDATES_DIR/appcast.xml"
	fi
	cp "$DMG" "$UPDATES_DIR/"
	# Release notes ride along under the archive's own name: generate_appcast
	# signs them and links the copy GitHub Pages serves from site/.
	if [ -f "$REPO_ROOT/site/WeChatBridge-$VERSION.md" ]; then
		cp "$REPO_ROOT/site/WeChatBridge-$VERSION.md" "$UPDATES_DIR/"
	fi
	"$SPARKLE_TOOLS/generate_appcast" \
		--download-url-prefix "https://github.com/$REPOSITORY_SLUG/releases/download/$TAG/" \
		--release-notes-url-prefix "$PAGES_URL/" \
		--link "https://github.com/$REPOSITORY_SLUG" \
		"$UPDATES_DIR"
fi

echo "release artifact: $DMG"
echo "checksum: $DMG.sha256"
if [ -f "$UPDATES_DIR/appcast.xml" ]; then
	echo "signed appcast: $UPDATES_DIR/appcast.xml — copy it to site/ and commit to publish"
fi
echo "homebrew cask: run Scripts/update-tap.sh once the GitHub Release exists"
