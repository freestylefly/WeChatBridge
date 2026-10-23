#!/usr/bin/env bash
# Build, install, and open the current development app on 记录.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=share-slots.sh
source "$ROOT/Scripts/share-slots.sh"

CONFIG="${CONFIG:-debug}" "$ROOT/Scripts/make-app.sh"
"$ROOT/Scripts/install-dev-build.sh"

APP="$HOME/Applications/$APP_BUNDLE_NAME"
open -a "$APP" "wechatbridge://settings/history"
