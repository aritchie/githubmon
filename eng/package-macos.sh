#!/usr/bin/env bash
#
# Build, package, sign and deploy the macOS Release .app for LOCAL use.
#
# Why this exists: the maui-labs macOS Release build (a) ships an INVALID code
# signature (app SIGKILLs on launch with CODESIGNING / Invalid Page) and (b) emits
# two bundles — only the non-RID one contains the BlazorWebView assets (wwwroot +
# _framework). This script grabs the complete bundle, re-signs it correctly, clears
# quarantine, and drops it on the Desktop (override with --dest DIR).
#
# Usage:
#   eng/package-macos.sh                # build + deploy to ~/Desktop, then launch
#   eng/package-macos.sh --no-build     # repackage the existing build output
#   eng/package-macos.sh --dest ./dist  # deploy somewhere else
#   eng/package-macos.sh --no-open      # don't launch afterwards
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_ROOT/src/GitHubShine/GitHubShine.csproj"
APP_NAME="GitHub Shine.app"
EXE_NAME="GitHubShine"
ENTITLEMENTS="$REPO_ROOT/eng/macos-resign.entitlements"
TFM="net10.0-macos"

DEST="$HOME/Desktop"
DO_BUILD=1
DO_OPEN=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --no-build) DO_BUILD=0; shift ;;
    --no-open)  DO_OPEN=0; shift ;;
    --dest)     DEST="$2"; shift 2 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

# Point at a valid Xcode (a stale /Applications/Xcode.app can shadow the real one).
if [[ -z "${DEVELOPER_DIR:-}" ]]; then
  DEVELOPER_DIR="$(xcode-select -p 2>/dev/null || true)"
  export DEVELOPER_DIR
  export MD_APPLE_SDK_ROOT="${DEVELOPER_DIR%/Contents/Developer}"
fi

if [[ $DO_BUILD -eq 1 ]]; then
  echo "==> Building Release ($TFM) ..."
  dotnet build "$PROJECT" -c Release -f "$TFM" -clp:NoSummary -v:q -nologo
fi

# The COMPLETE bundle is the non-RID one (the <RID>/ subfolder is missing wwwroot/_framework).
SRC="$REPO_ROOT/src/GitHubShine/bin/Release/$TFM/$APP_NAME"
if [[ ! -d "$SRC" ]]; then
  echo "error: complete (non-RID) bundle not found at: $SRC" >&2
  exit 1
fi
WWW_COUNT="$(find "$SRC" -path '*wwwroot*' -type f | wc -l | tr -d ' ')"
if [[ "$WWW_COUNT" -lt 5 ]]; then
  echo "error: bundle looks incomplete ($WWW_COUNT wwwroot files) — wrong bundle?" >&2
  exit 1
fi

mkdir -p "$DEST"
TARGET="$DEST/$APP_NAME"
echo "==> Deploying to: $TARGET  ($WWW_COUNT web assets)"
rm -rf "$TARGET"
cp -R "$SRC" "$TARGET"

echo "==> Re-signing (ad-hoc) ..."
# Nested Mach-O first (bottom-up), then the main executable, then the bundle.
find "$TARGET" \( -name "*.dylib" -o -name "*.so" \) -print0 \
  | xargs -0 -n1 codesign --force --timestamp=none -s - >/dev/null 2>&1 || true
codesign --force --timestamp=none --entitlements "$ENTITLEMENTS" -s - "$TARGET/Contents/MacOS/$EXE_NAME" >/dev/null
codesign --force --timestamp=none --entitlements "$ENTITLEMENTS" -s - "$TARGET" >/dev/null

echo "==> Clearing quarantine + refreshing icon cache ..."
xattr -cr "$TARGET"
touch "$TARGET"
LSREG=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister
[[ -x "$LSREG" ]] && "$LSREG" -f "$TARGET" >/dev/null 2>&1 || true

echo "==> Verifying signature ..."
codesign -vvv "$TARGET" 2>&1 | sed 's/^/    /'

echo "==> Done: $TARGET"
if [[ $DO_OPEN -eq 1 ]]; then
  open "$TARGET"
fi
