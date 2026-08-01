#!/usr/bin/env bash
#
# Wrap a device .app into an .ipa. An .ipa is just a zip with the bundle under Payload/, so
# this works for unsigned builds too — the resulting file cannot be installed through the App
# Store or TestFlight, but sideloading tools (Sideloadly, AltStore, Xcode's Devices window
# after re-signing) accept it.
#
# Usage: make-ipa.sh <app-path> <output.ipa>
set -euo pipefail

APP="${1:?usage: make-ipa.sh <app-path> <output.ipa>}"
IPA="${2:?missing output ipa path}"

[[ -d "$APP" ]] || { echo "error: no such app bundle: $APP" >&2; exit 1; }
[[ -f "$APP/Info.plist" ]] || { echo "error: $APP has no Info.plist — not an iOS bundle?" >&2; exit 1; }

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

mkdir -p "$STAGE/Payload"
ditto "$APP" "$STAGE/Payload/$(basename "$APP")"

mkdir -p "$(dirname "$IPA")"
rm -f "$IPA"
# Absolute, because the zip below runs from inside the staging dir.
IPA="$(cd "$(dirname "$IPA")" && pwd)/$(basename "$IPA")"
# -y keeps symlinks as symlinks; a resolved symlink would bloat the payload and break frameworks.
( cd "$STAGE" && zip -qry "$IPA" Payload )

echo "==> Created: $IPA ($(du -h "$IPA" | cut -f1))"
