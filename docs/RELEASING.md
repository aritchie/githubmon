# Releasing GitHub Shine

`.github/workflows/release.yml` builds the macOS app and publishes a GitHub release with:

| Asset | Signing |
|---|---|
| `GitHubShine-<v>-osx-universal.dmg` | **Developer ID + hardened runtime + notarized + stapled** |
| `SHA256SUMS.txt` | — |

Only the macOS head is released. `TargetFrameworks` in `GitHubShine.csproj` is conditioned on the
build OS, so adding Windows or Linux later means a separate job on a Windows / Ubuntu runner (the
earlier multi-platform version of this workflow is in git history).

---

## Cutting a release

```bash
git tag v1.2.3
git push origin v1.2.3
```

Or run **Actions → Release → Run workflow** and type a version; manual runs default to a draft
release. A version containing a hyphen (`1.2.3-beta1`) is marked as a pre-release, and the
suffix is stripped before it reaches `ApplicationDisplayVersion` because Apple rejects a
non-numeric `CFBundleShortVersionString`.

The build number (`ApplicationVersion` → `CFBundleVersion`) is the workflow run number, so it
always increases.

---

## Runner and toolchain

The job runs on the `xcode-27` runner, pins **Xcode 27.1** and installs the `macos` + `maui`
workloads from SDK `10.0.4xx` — the same combination ShinyNotch releases with. The workload's
macOS SDK (27.0.10742) refuses any other Xcode, including the runner's default 27.0 and its 27.2
beta. When the workload moves to a new macOS SDK, bump the Xcode path in the **Xcode** step.

`maui` is installed (not just `macos`) because restore evaluates every Apple-host TFM in the
csproj — `net10.0-ios` and `net10.0-android` too — even for a `-f net10.0-macos` build.

---

## Required secrets

Same names as ShinyNotch, so the same values can be reused.

| Secret | What it is |
|---|---|
| `APPLE_MACOS_DIRECT_CERTIFICATE_P12` | base64 of a **Developer ID Application** certificate exported as `.p12` (cert **and** private key) |
| `APPLE_MACOS_DIRECT_CERTIFICATE_PASSWORD` | the password set during that `.p12` export |
| `APPLE_MY_IDENTITY_P8_KEY` | the App Store Connect API key `AuthKey_XXXXXXXXXX.p8` — its PEM text or base64 of it |
| `APPLE_MY_IDENTITY_KEY_ID` | the 10-character Key ID |
| `APPLE_MY_IDENTITY_ISSUER_ID` | the Issuer ID (a UUID) |

The signing identity is not a secret: the workflow reads the first
`Developer ID Application: …` identity out of the imported `.p12`.

**Exporting the certificate.** In Keychain Access, find *Developer ID Application: …*, expand
it so the private key is included in the selection, right-click → Export as `.p12`. Then:

```bash
base64 -i DeveloperID.p12 | gh secret set APPLE_MACOS_DIRECT_CERTIFICATE_P12
gh secret set APPLE_MACOS_DIRECT_CERTIFICATE_PASSWORD
```

**Creating the API key.** App Store Connect → Users and Access → Integrations → App Store
Connect API → **+**. Role **Developer** is sufficient for notarization. The `.p8` downloads
exactly once.

```bash
gh secret set APPLE_MY_IDENTITY_P8_KEY < AuthKey_XXXXXXXXXX.p8
gh secret set APPLE_MY_IDENTITY_KEY_ID
gh secret set APPLE_MY_IDENTITY_ISSUER_ID
```

---

## What the job does, and why

`dotnet build`, **not** `dotnet publish` — publish strips the Blazor static web assets out of
`Contents/Resources` and the WebView renders blank. The job then takes the `.app` at the **TFM
root**, not the `osx-arm64/` or `osx-x64/` subfolders, which are incomplete lipo intermediates.
Both traps are documented in `CLAUDE.md` (gotcha 3); the job asserts on the resource file count
so a regression fails the build rather than shipping a blank app.

Signing then runs bottom-up via `eng/ci/sign-macos-app.sh`: nested Mach-O first, nested bundles
next, the outer bundle last. `--deep` is deliberately not used — Apple discourages it for
distribution because it signs in an unspecified order and applies the outer entitlements to
inner code.

Entitlements come from `eng/macos-dist.entitlements`. The three keys there
(`allow-jit`, `allow-unsigned-executable-memory`, `disable-library-validation`) are what the
.NET runtime needs under hardened runtime. App Sandbox is deliberately off — it's only required
for the Mac App Store, and it would block the SQLite store under
`~/Library/Application Support`.

Finally `eng/ci/notarize.sh` submits the DMG, waits, and staples. On rejection it dumps
`notarytool log`, which names the offending binary — the submit output alone only says
"Invalid".

### Running the macOS path locally

The scripts are standalone, so you can rehearse the whole thing without CI:

```bash
dotnet build src/GitHubShine/GitHubShine.csproj -c Release -f net10.0-macos
APP="src/GitHubShine/bin/Release/net10.0-macos/GitHub Shine.app"

eng/ci/sign-macos-app.sh "$APP" "Developer ID Application: Your Name (TEAMID)"
eng/ci/make-dmg.sh "$APP" artifacts/GitHubShine.dmg "Developer ID Application: Your Name (TEAMID)"

export AC_API_KEY_P8="$(cat AuthKey_XXXXXXXXXX.p8)"
export AC_API_KEY_ID=XXXXXXXXXX
export AC_API_ISSUER_ID=xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
eng/ci/notarize.sh artifacts/GitHubShine.dmg
```

`eng/package-macos.sh` remains the fast ad-hoc-signed loop for local testing; it is not part of
the release path.
