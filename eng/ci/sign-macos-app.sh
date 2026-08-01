#!/usr/bin/env bash
#
# Deep-sign a macOS .app with a Developer ID Application identity, hardened runtime and a
# secure timestamp — i.e. the exact shape the notary service accepts.
#
# Differs from eng/package-macos.sh (which ad-hoc signs for LOCAL use) in three ways that
# matter to notarization:
#   --options runtime   hardened runtime, mandatory for notarization
#   --timestamp         secure timestamp from Apple's TSA, mandatory (the local script uses
#                       --timestamp=none because ad-hoc signatures can't be timestamped)
#   real identity       instead of "-"
#
# Usage: sign-macos-app.sh <app-path> <signing-identity> [entitlements-plist]
set -euo pipefail

APP="${1:?usage: sign-macos-app.sh <app-path> <identity> [entitlements]}"
IDENTITY="${2:?missing signing identity}"
ENTITLEMENTS="${3:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/macos-dist.entitlements}"

[[ -d "$APP" ]] || { echo "error: no such app bundle: $APP" >&2; exit 1; }
[[ -f "$ENTITLEMENTS" ]] || { echo "error: no such entitlements: $ENTITLEMENTS" >&2; exit 1; }

# A Release build can carry stale signatures and Finder xattrs; both make codesign fail with
# "resource fork, Finder information, or similar detritus not allowed".
xattr -cr "$APP"

sign() {
    codesign --force --options runtime --timestamp --sign "$IDENTITY" "$@"
}

# Bottom-up: nested Mach-O first, then any nested bundles, then the outer bundle last. Apple
# deprecates --deep for distribution precisely because it signs in an unspecified order and
# applies the outer entitlements to inner code.
echo "==> Signing nested Mach-O ..."
NESTED=0
while IFS= read -r -d '' f; do
    sign "$f"
    NESTED=$((NESTED + 1))
done < <(find "$APP/Contents" -type f \( -name '*.dylib' -o -name '*.so' -o -name '*.a' \) -print0)
echo "    $NESTED nested binaries"

echo "==> Signing nested bundles ..."
while IFS= read -r -d '' b; do
    sign "$b"
done < <(find "$APP/Contents" -mindepth 1 \( -name '*.framework' -o -name '*.app' -o -name '*.bundle' \) -print0)

# The outer bundle signature seals Contents/MacOS/<exe>, so the main executable does not get a
# separate codesign pass. Entitlements are applied here and here only.
echo "==> Signing bundle ..."
sign --entitlements "$ENTITLEMENTS" "$APP"

echo "==> Verifying ..."
codesign --verify --deep --strict --verbose=2 "$APP"
codesign --display --entitlements - --verbose=4 "$APP" 2>&1 | sed 's/^/    /'

# get-task-allow is an automatic notarization rejection; catch it here rather than after a
# multi-minute notary round trip.
if codesign --display --entitlements - "$APP" 2>/dev/null | grep -q 'get-task-allow'; then
    echo "error: bundle carries com.apple.security.get-task-allow — notarization will reject it" >&2
    exit 1
fi

echo "==> Signed: $APP"
