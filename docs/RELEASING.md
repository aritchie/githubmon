# Releasing GitHub Shine

`.github/workflows/release.yml` builds every platform head and publishes a GitHub release with:

| Asset | Built on | Signing |
|---|---|---|
| `GitHubShine-<v>-osx-universal.dmg` | macOS runner | **Developer ID + notarized + stapled** |
| `GitHubShine-<v>-win-x64.zip` | Windows runner | unsigned |
| `GitHubShine-<v>-linux-x64.tar.gz` | Ubuntu runner | n/a |
| `GitHubShine-<v>-android.aab` / `.apk` | macOS runner | release keystore |
| `GitHubShine-<v>-ios-unsigned.ipa` | macOS runner | **unsigned** |
| `SHA256SUMS.txt` | — | — |

Each head is built on the OS that `GitHubShine.csproj` targets it from — `TargetFrameworks`
there is conditioned on the build OS, so Android and iOS have to come off the macOS runner
alongside `net10.0-macos`, and Linux only ever sees the plain `net10.0` GTK4 head.

---

## Cutting a release

```bash
git tag v1.2.3
git push origin v1.2.3
```

Or run **Actions → Release → Run workflow** and type a version; manual runs default to a draft
release. A version containing a hyphen (`1.2.3-beta1`) is marked as a pre-release, and the
suffix is stripped before it reaches `ApplicationDisplayVersion` because Apple platforms reject
a non-numeric `CFBundleShortVersionString`.

The build number (`ApplicationVersion` → Android `versionCode`, Apple `CFBundleVersion`) comes
from the workflow run number so it always increases. Never re-run an old workflow run to
re-publish — cut a new tag instead, or Android will see a `versionCode` that went backwards.

---

## Required secrets

### macOS signing + notarization

| Secret | What it is |
|---|---|
| `MACOS_CERT_P12_B64` | base64 of a **Developer ID Application** certificate exported as `.p12` (cert **and** private key) |
| `MACOS_CERT_PASSWORD` | the password set during that `.p12` export |
| `MACOS_SIGN_IDENTITY` | the full identity string, e.g. `Developer ID Application: Allan Ritchie (ABCDE12345)` |
| `AC_API_KEY_P8_B64` | base64 of the App Store Connect API key `AuthKey_XXXXXXXXXX.p8` |
| `AC_API_KEY_ID` | the 10-character Key ID |
| `AC_API_ISSUER_ID` | the Issuer ID (a UUID) |

**Exporting the certificate.** In Keychain Access, find *Developer ID Application: …*, expand
it so the private key is included in the selection, right-click → Export as `.p12`. Then:

```bash
base64 -i DeveloperID.p12 | pbcopy      # -> MACOS_CERT_P12_B64
security find-identity -v -p codesigning # -> the string for MACOS_SIGN_IDENTITY
```

If you don't have the certificate yet: Apple Developer → Certificates → **+** → *Developer ID
Application*. This needs a paid Apple Developer Program membership; notarization is not
available on a free account.

**Creating the API key.** App Store Connect → Users and Access → Integrations → App Store
Connect API → **+**. Role **Developer** is sufficient for notarization. The `.p8` downloads
exactly once.

```bash
base64 -i AuthKey_XXXXXXXXXX.p8 | pbcopy   # -> AC_API_KEY_P8_B64
```

### Android signing

| Secret | What it is |
|---|---|
| `ANDROID_KEYSTORE_B64` | base64 of the release `.keystore` / `.jks` |
| `ANDROID_KEYSTORE_PASSWORD` | keystore password |
| `ANDROID_KEY_ALIAS` | key alias inside the keystore |
| `ANDROID_KEY_PASSWORD` | password for that key |

Creating one, if needed:

```bash
keytool -genkeypair -v -keystore release.keystore -alias githubshine \
        -keyalg RSA -keysize 2048 -validity 10000
base64 -i release.keystore | pbcopy      # -> ANDROID_KEYSTORE_B64
```

**Back this keystore up somewhere durable.** Losing it means never being able to ship an
update to an existing Play listing under the same signing key.

### Optional repository variable

| Variable | Purpose |
|---|---|
| `MACOS_RUNNER` | Overrides the macOS runner image (default `macos-26`). Set this if the default image's Xcode doesn't match what the .NET `macos` workload expects — the workload tracks the current Xcode major, and a mismatch shows up as `SDK MacOSX.sdk cannot be located` or missing-framework errors. |

---

## What the macOS job does, and why

`dotnet build`, **not** `dotnet publish` — publish strips the Blazor static web assets out of
`Contents/Resources` and the WebView renders blank. The job then takes the `.app` at the **TFM
root**, not the `osx-arm64/` or `osx-x64/` subfolders, which are incomplete lipo intermediates.
Both traps are documented at length in `CLAUDE.md` (gotcha 3); the job asserts on the wwwroot
file count so a regression fails the build rather than shipping a blank app.

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

export AC_API_KEY_P8_B64="$(base64 -i AuthKey_XXXXXXXXXX.p8)"
export AC_API_KEY_ID=XXXXXXXXXX
export AC_API_ISSUER_ID=xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
eng/ci/notarize.sh artifacts/GitHubShine.dmg
```

`eng/package-macos.sh` remains the fast ad-hoc-signed loop for local testing; it is not part of
the release path.

---

## Notes on the other heads

**Windows** publishes self-contained with `WindowsAppSDKSelfContained=true` so users need
neither .NET nor the Windows App SDK runtime. The `.exe` is unsigned, so SmartScreen warns on
first run — fixing that needs an EV or Azure Trusted Signing certificate and is a separate
piece of work.

**Linux** publishes self-contained `linux-x64`. GTK4 and WebKitGTK 6.0 remain host
dependencies and cannot be bundled; the tarball carries a `README.txt` with the install command
for the common distros.

**iOS** is built unsigned with `-p:EnableCodeSigning=false` and wrapped into `Payload/` as an
`.ipa`. That property is the one that actually matters: it gates both `_DetectSigningIdentity`
and `_CodesignAppBundleCondition` in `Xamarin.Shared.targets`, so the build never looks for a
certificate or provisioning profile. Without it the SDK falls back to automatic provisioning and
quietly signs with whatever identity is in the local keychain — which looks fine on a dev
machine and fails on a bare runner. Passing a non-empty `CodesignEntitlements` re-enables signing
on its own, so don't.

The IPA cannot be installed as-is — a sideloading tool has to re-sign it with the installer's
own Apple account. Because that asset is the least useful one in the release, its job is marked
`continue-on-error`, so an iOS build failure omits the IPA rather than blocking the artifacts
people can actually install. If the IPA goes missing from a release, that job is where to look.
