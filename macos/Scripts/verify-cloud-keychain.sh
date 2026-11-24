#!/bin/bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WCB_PROBE_DIR="$(mktemp -d /tmp/wcb-keychain-check.XXXXXX)"
WCB_PROBE_IDENTITY="${WCB_PROBE_IDENTITY:-WeChatBridge Dev}"
cleanup() { security delete-keychain "$WCB_PROBE_DIR/probe.keychain" >/dev/null 2>&1 || true; rm -rf "$WCB_PROBE_DIR"; }
trap cleanup EXIT
swiftc "$ROOT/Scripts/Fixtures/CloudKeychainProbe.swift" -o "$WCB_PROBE_DIR/probe" 2>"$WCB_PROBE_DIR/build.log"
mkdir -p "$WCB_PROBE_DIR/Probe.app/Contents/MacOS"
cp "$WCB_PROBE_DIR/probe" "$WCB_PROBE_DIR/Probe.app/Contents/MacOS/Probe"
cp "$WCB_PROBE_DIR/probe" "$WCB_PROBE_DIR/wcb"
codesign --force --sign "$WCB_PROBE_IDENTITY" --identifier cn.wechatbridge.spike.app "$WCB_PROBE_DIR/Probe.app/Contents/MacOS/Probe"
codesign --force --sign "$WCB_PROBE_IDENTITY" --identifier cn.wechatbridge.spike.cli "$WCB_PROBE_DIR/wcb"
"$WCB_PROBE_DIR/Probe.app/Contents/MacOS/Probe" create "$WCB_PROBE_DIR/probe.keychain" "$WCB_PROBE_DIR/Probe.app/Contents/MacOS/Probe" "$WCB_PROBE_DIR/wcb"
"$WCB_PROBE_DIR/Probe.app/Contents/MacOS/Probe" allow "$WCB_PROBE_DIR/probe.keychain" master
"$WCB_PROBE_DIR/wcb" allow "$WCB_PROBE_DIR/probe.keychain" agent
"$WCB_PROBE_DIR/wcb" deny "$WCB_PROBE_DIR/probe.keychain" master
"$WCB_PROBE_DIR/probe" deny "$WCB_PROBE_DIR/probe.keychain" agent
mv "$WCB_PROBE_DIR/Probe.app" "$WCB_PROBE_DIR/Moved.app"
mv "$WCB_PROBE_DIR/wcb" "$WCB_PROBE_DIR/moved-wcb"
"$WCB_PROBE_DIR/Moved.app/Contents/MacOS/Probe" allow "$WCB_PROBE_DIR/probe.keychain" master
"$WCB_PROBE_DIR/moved-wcb" allow "$WCB_PROBE_DIR/probe.keychain" agent
"$WCB_PROBE_DIR/moved-wcb" deny "$WCB_PROBE_DIR/probe.keychain" master
printf 'Signed keychain isolation and relocation checks passed. Identity: %s\n' "$WCB_PROBE_IDENTITY"
