#!/usr/bin/env bash
#
# Submit an artifact to Apple's notary service, wait for the verdict, then staple the ticket.
#
# Authenticates with an App Store Connect API key, which is read from the environment so the
# .p8 never appears in a process listing:
#   AC_API_KEY_P8_B64  base64 of the AuthKey_XXXXXXXXXX.p8 file
#   AC_API_KEY_ID      the Key ID   (10 chars, e.g. ABCD123456)
#   AC_API_ISSUER_ID   the Issuer ID (a UUID)
#
# Usage: notarize.sh <artifact.dmg|artifact.zip>
set -euo pipefail

ARTIFACT="${1:?usage: notarize.sh <artifact>}"
[[ -f "$ARTIFACT" ]] || { echo "error: no such artifact: $ARTIFACT" >&2; exit 1; }

: "${AC_API_KEY_P8_B64:?AC_API_KEY_P8_B64 is not set}"
: "${AC_API_KEY_ID:?AC_API_KEY_ID is not set}"
: "${AC_API_ISSUER_ID:?AC_API_ISSUER_ID is not set}"

KEYDIR="$(mktemp -d)"
trap 'rm -rf "$KEYDIR"' EXIT
KEYFILE="$KEYDIR/AuthKey.p8"
printf '%s' "$AC_API_KEY_P8_B64" | base64 --decode > "$KEYFILE"
chmod 600 "$KEYFILE"

echo "==> Submitting $(basename "$ARTIFACT") to the notary service (this takes a few minutes) ..."
set +e
SUBMIT_OUT="$(xcrun notarytool submit "$ARTIFACT" \
    --key "$KEYFILE" \
    --key-id "$AC_API_KEY_ID" \
    --issuer "$AC_API_ISSUER_ID" \
    --wait \
    --timeout 45m \
    --output-format json 2>&1)"
SUBMIT_RC=$?
set -e
echo "$SUBMIT_OUT"

SUBMISSION_ID="$(printf '%s' "$SUBMIT_OUT" | /usr/bin/python3 -c \
    'import json,sys
try:
    print(json.loads(sys.stdin.read()).get("id",""))
except Exception:
    print("")' 2>/dev/null || true)"
STATUS="$(printf '%s' "$SUBMIT_OUT" | /usr/bin/python3 -c \
    'import json,sys
try:
    print(json.loads(sys.stdin.read()).get("status",""))
except Exception:
    print("")' 2>/dev/null || true)"

if [[ "$STATUS" != "Accepted" || $SUBMIT_RC -ne 0 ]]; then
    echo "error: notarization did not succeed (status='${STATUS:-unknown}')" >&2
    if [[ -n "$SUBMISSION_ID" ]]; then
        # The submit output only says "Invalid"; the log says WHICH binary was rejected and why.
        echo "==> Notary log for $SUBMISSION_ID:" >&2
        xcrun notarytool log "$SUBMISSION_ID" \
            --key "$KEYFILE" --key-id "$AC_API_KEY_ID" --issuer "$AC_API_ISSUER_ID" >&2 || true
    fi
    exit 1
fi

echo "==> Stapling ticket ..."
xcrun stapler staple "$ARTIFACT"
xcrun stapler validate "$ARTIFACT"

echo "==> Notarized and stapled: $ARTIFACT"
