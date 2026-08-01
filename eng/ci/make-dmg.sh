#!/usr/bin/env bash
#
# Build a drag-to-Applications DMG around an already-signed .app, then sign the DMG itself.
# The DMG is what gets notarized and stapled (stapling the DMG means the ticket travels with
# the download, so first launch works with no network round trip to Apple).
#
# Usage: make-dmg.sh <app-path> <output.dmg> [signing-identity]
set -euo pipefail

APP="${1:?usage: make-dmg.sh <app-path> <output.dmg> [identity]}"
DMG="${2:?missing output dmg path}"
IDENTITY="${3:-}"

[[ -d "$APP" ]] || { echo "error: no such app bundle: $APP" >&2; exit 1; }

VOLNAME="$(basename "$APP" .app)"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

echo "==> Staging $VOLNAME ..."
# ditto (not cp) — it preserves extended attributes and, critically, the code signature.
ditto "$APP" "$STAGE/$(basename "$APP")"
ln -s /Applications "$STAGE/Applications"

mkdir -p "$(dirname "$DMG")"
rm -f "$DMG"

echo "==> Creating DMG ..."
# UDZO (zlib) rather than ULFO/lzfse: universally mountable, and the app payload is already
# compressed enough that the size difference is marginal.
hdiutil create \
    -volname "$VOLNAME" \
    -srcfolder "$STAGE" \
    -format UDZO \
    -imagekey zlib-level=9 \
    -ov -quiet \
    "$DMG"

if [[ -n "$IDENTITY" ]]; then
    echo "==> Signing DMG ..."
    codesign --force --timestamp --sign "$IDENTITY" "$DMG"
    codesign --verify --verbose=2 "$DMG"
fi

echo "==> Created: $DMG ($(du -h "$DMG" | cut -f1))"
