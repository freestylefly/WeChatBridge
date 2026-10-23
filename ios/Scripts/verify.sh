#!/bin/bash
# Every automated check the iOS work needs, in the order a failure is cheapest
# to find: Mac regression, iOS typecheck of Core, then the iOS build and tests.
# Usage: ios/Scripts/verify.sh [simulator name]   (default: iPhone 17)
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/../.." && pwd)
DEVICE=${1:-iPhone 17}

echo "== Mac swift test"
swift test --package-path "$ROOT/macos" 2>&1 | grep -E "Executed [0-9]+ tests|error:" | tail -2

echo "== iOS typecheck of WeChatBridgeCore"
SDK=$(xcrun --sdk iphonesimulator --show-sdk-path)
xcrun swiftc -typecheck -sdk "$SDK" -target arm64-apple-ios17.0-simulator -swift-version 6 \
  -application-extension -module-name WeChatBridgeCore "$ROOT"/macos/Sources/WeChatBridgeCore/*.swift

echo "== xcodegen"
(cd "$ROOT/ios" && xcodegen generate --quiet)

mkdir -p "$ROOT/ios/build"
echo "== iOS build and unit tests on $DEVICE"
xcodebuild -project "$ROOT/ios/WeChatBridge.xcodeproj" -scheme WeChatBridgeiOS \
  -destination "platform=iOS Simulator,name=$DEVICE" \
  -derivedDataPath "$ROOT/ios/build/DerivedData" \
  CODE_SIGNING_ALLOWED=NO test > "$ROOT/ios/build/test.log" 2>&1 || status=$?
grep -E "error:|Executed [0-9]+ tests|\*\* (TEST|BUILD) " "$ROOT/ios/build/test.log" | sort -u
exit "${status:-0}"
