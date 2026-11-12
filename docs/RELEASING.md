# Releasing GitHub Shine

`.github/workflows/release.yml` builds the desktop heads and publishes a GitHub release with:

| Asset | Built on | Signing |
|---|---|---|
| `GitHubShine-<v>-osx-universal.dmg` | `xcode-27` | **Developer ID + hardened runtime + notarized + stapled** |
| `GitHubShine-<v>-windows-x64-setup.exe` | `windows-latest` | unsigned (Inno Setup, per-user) |
| `GitHubShine-<v>-windows-arm64-setup.exe` | `windows-latest` | unsigned (Inno Setup, per-user) |
| `GitHubShine-<v>-linux-x64.deb` / `.rpm` | `ubuntu-24.04` | unsigned (nfpm) |
| `GitHubShine-<v>-linux-arm64.deb` / `.rpm` | `ubuntu-24.04` | unsigned (nfpm) |
| `SHA256SUMS.txt` | — | covers every asset above |

`TargetFrameworks` in `GitHubShine.csproj` is conditioned on the build OS, so each head builds in
its own job (`macos`, `windows`, `linux`) and uploads an artifact; a final `release` job
checksums the lot and creates the release. If any head fails, nothing is published. Android and
iOS are not released (an earlier version of this workflow built them; it is in git history).

### The app updates itself from these releases

On startup the desktop app reads this repository's releases (anonymously, `Features/Updates/ReleaseFeed.cs`),
takes the highest-versioned non-draft one — **prereleases included** — and offers it if it's newer
than the running `ApplicationDisplayVersion`. On macOS, Install downloads the `.dmg`, checks it
against `SHA256SUMS.txt`, verifies the app inside is validly signed by the same Developer ID team,
swaps it over the running bundle and relaunches. Windows/Linux just open the release page.

That makes the asset names part of a contract with every installed copy:

- exactly one asset ending in **`.dmg`**, containing a single `*.app` at its root — the updater
  takes the first `.dmg` it finds, and the `release` job fails if there isn't exactly one;
- **`SHA256SUMS.txt`** in `shasum -a 256` format (`<hex>  <name>` per line — `sha256sum`, which
  the workflow uses, writes the same thing), listing that DMG by file name. The updater looks up
  the DMG's line by exact name and ignores the others, so the Windows/Linux lines are harmless;
- the app signed with the **same Team ID** as previous releases — a change of signing team makes
  every existing install refuse the update (deliberately), and users must reinstall by hand.

Tag versions are compared numerically after the `-suffix` is stripped, matching how the app is
stamped, so `v1.2.0-beta2` is not offered to someone on `v1.2.0-beta1` (both report 1.2.0) —
bump the patch number between prereleases you want installed copies to pick up.

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

### macOS

The job runs on the `xcode-27` runner, pins **Xcode 27.1** and installs the `macos` + `maui`
workloads from SDK `10.0.4xx` at workload set **`WORKLOAD_VERSION`** (`10.0.404`) — every job
installs its workloads at that pinned set, so a release can't silently pick up a newer one. Bump
it deliberately, within `DOTNET_VERSION`'s feature band.

The csproj sets `ValidateXcodeVersion=false`, so the workload no longer refuses an Xcode other
than the exact one its macOS SDK was cut against (which is what used to break local builds the
moment Xcode moved ahead). The Xcode step still pins 27.1 so CI builds with a known toolchain;
bump its path when you move the workload to a new macOS SDK.

`maui` is installed (not just `macos`) because restore evaluates every Apple-host TFM in the
csproj — `net10.0-ios` and `net10.0-android` too — even for a `-f net10.0-macos` build.

### Windows

`windows-latest` with the `maui-windows` workload. `dotnet publish` (the static-web-asset trap is
specific to the macOS bundle; the job still asserts on the `wwwroot/` file count) self-contained
with `WindowsAppSDKSelfContained=true`, once per `win-x64` / `win-arm64`, then Inno Setup
(`eng/installers/windows/GitHubShine.iss`) wraps each into a setup exe. Both architectures
build on the x64 runner — `LibGit2Sharp.NativeBinaries` ships `win-arm64` natives.

### Linux

`ubuntu-24.04` with the `maui-android` workload — not because anything Android is built, but
because `UseMaui=true` makes the SDK import `Microsoft.Maui.Sdk.net10` even for the plain
`net10.0` GTK head, and `maui-android` is the workload that carries it and installs on Linux.
`dotnet publish -f net10.0` self-contained once per `linux-x64` / `linux-arm64` (managed code
over system GTK; libgit2 and e_sqlite3 ship natives for both), then [nfpm](https://nfpm.goreleaser.com)
(pinned by `NFPM_VERSION`, checksum-verified) turns each into a `.deb` and an `.rpm` from
`eng/installers/linux/nfpm.yaml`.

### Version stamping

The update check compares releases against `AppInfo.VersionString`. macOS reads it from
`CFBundleShortVersionString` and Windows from the assembly, which `Microsoft.Maui.Core`'s
`WinUI.targets` stamps from `ApplicationDisplayVersion` — so both just take
`ApplicationDisplayVersion`. The labs GTK head also reads the assembly attributes, but nothing maps
`ApplicationDisplayVersion` onto them there, so the Linux job passes `-p:Version=<tag version>`
too. Without it every Linux build reports 1.0.0 and the update check always thinks it is out of date.

---

## Required secrets

All for the macOS job — the Windows and Linux builds are unsigned and need none. Same names as
ShinyNotch, so the same values can be reused.

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

## What the macOS job does, and why

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

---

## The Windows installer

Per-user (`PrivilegesRequired=lowest`) into `%LOCALAPPDATA%\Programs\GitHub Shine`, so it needs
no admin rights and the exe path that "Start with the computer" writes into the HKCU Run key
stays put across upgrades. **Never change the `AppId` GUID** in the `.iss` — it is how an upgrade
finds the copy it replaces.

Uninstall kills the running tray app, then removes the `GitHubShine` value from
`HKCU\…\CurrentVersion\Run` (and `…\Explorer\StartupApproved\Run`) — the value name is
`StartupServiceOptions.Identifier` in `MauiProgram.cs`, so the two have to change together (they
shouldn't change at all: existing autostart entries would be orphaned). The data folder,
`%LOCALAPPDATA%\GitHubShine`, is left alone.

The exe is unsigned, so SmartScreen warns on first run; fixing that needs an EV or Azure Trusted
Signing certificate. WebView2 is not bundled — Windows 11 and current Windows 10 ship it.

## The Linux packages

Installed to `/opt/githubshine`, with `/usr/bin/githubshine`, a `.desktop` file and the
scalable icon named after `ApplicationId` (`net.shiny.githubshine`, which the GTK backend uses as
its GApplication id). `nfpm.yaml` lists every runtime dependency with the reason for it — they
are the system libraries the managed code loads by soname: GTK4 + WebKitGTK 6 (UI and
BlazorWebView), GTK3 + ayatana-appindicator + libnotify + libX11 (the tray, from
`Shiny.Maui.Controls.Desktop`), OpenSSL, ICU, `ca-certificates` and `xdg-utils`. Notifications
go over D-Bus and need no library; libsecret isn't needed because tokens live in SQLite.

Removing the package can't touch `~/.config/autostart/GitHubShine.desktop` (it's per-user); it
just points at a binary that is gone, which desktop sessions skip.

### Rehearsing the Linux packaging on a Mac

The csproj only offers `net10.0` on a Linux host, but global properties override that, so the
Linux publish cross-builds from macOS:

```bash
dotnet publish src/GitHubShine/GitHubShine.csproj -c Release \
  -p:TargetFrameworks=net10.0 -p:TargetFramework=net10.0 -p:_IsLinux=true \
  -r linux-x64 --self-contained -p:Version=1.2.3 -o publish/linux-x64
VERSION=1.2.3 ARCH=amd64 PUBLISH_DIR=publish/linux-x64 envsubst < eng/installers/linux/nfpm.yaml > /tmp/nfpm.yaml
nfpm package -f /tmp/nfpm.yaml -p deb -t artifacts/
```
