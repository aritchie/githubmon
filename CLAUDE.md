# GitHubShine — build & platform notes

Cross-platform **.NET MAUI Blazor Hybrid** desktop/mobile app. macOS and Linux run on the
experimental **maui-labs** backends (net-macos AppKit / GTK4), not Mac Catalyst. The sections
below are hard-won gotchas — read them before touching the build, packaging, or macOS deploy.

## Target frameworks (per build OS)
- **macOS host:** `net10.0-macos;net10.0-ios;net10.0-android`
- **Linux host:** `net10.0` (GTK4 backend, `_IsLinux`)
- **Windows host:** `net10.0-windows10.0.19041.0`

The desktop backends come from **maui-labs** packages `Microsoft.Maui.Platforms.*`
(macOS: `.MacOS[.Essentials|.BlazorWebView]`, Linux: `.Linux.Gtk4[.Essentials|.BlazorWebView]`),
currently `0.1.0-preview.11.26317.2` on nuget.org. Package IDs have flip-flopped historically
(`Microsoft.Maui.Platforms.*` → `Platform.Maui.*` → back); namespaces track the ID (plural
`Microsoft.Maui.Platforms.*`). Extension methods and the `MacOSBlazorWebView` control name are
stable across the renames — only namespaces change.

---

## Gotcha 1 — MAUI SingleProject strips `Platforms/MacOS` and `Platforms/Linux` from Compile
**Symptom:** `CS5001` "Program does not contain a static 'Main'" plus `CS0234`/`CS0103` that
`GitHubShine.Platforms.MacOS.*` "does not exist", when building `net10.0-macos` / Linux.

**Cause:** `Microsoft.Maui.Controls.SingleProject.targets` stamps
`ExcludeFromCurrentConfiguration=true` on everything under `Platforms/**` via a `<Compile Update>`
glob, then re-enables ONLY the folders it hard-codes (Android, iOS, **MacCatalyst**, Windows,
Tizen). It has no mapping for net-macos (`Platforms/MacOS`) or the GTK4 Linux backend
(`Platforms/Linux`), so `_MauiRemovePlatformCompileItems` deletes those files — including the
entry-point `Program.cs`. A manual `<Compile Include>` does NOT survive; the `<Compile Update>`
re-stamps by path. **`dotnet build -getItem:Compile` lies** (shows them included); only the real
csc source list in a `-bl` binlog reveals they were dropped.

**Fix (already in `GitHubShine.csproj`):** target `_GitHubShineKeepDesktopPlatformCompile`
`BeforeTargets="_MauiRemovePlatformCompileItems"` flips `ExcludeFromCurrentConfiguration` back to
`false` for the current desktop folder. Do not remove it.

## Gotcha 2 — `Microsoft.Maui.Platforms` namespace shadows the app's `GitHubShine.Platforms` folder
The labs packages introduce a `Microsoft.Maui.Platforms` namespace which, via the global
`using Microsoft.Maui`, shadows the app's own `Platforms/` folder namespace when referenced by the
bare name `Platforms`. **Always fully-qualify the app's own platform code** as
`GitHubShine.Platforms.MacOS.X` / `GitHubShine.Platforms.Windows.X`, never bare `Platforms.X`.

---

## Gotcha 3 — macOS Release: deploy the UNIVERSAL bundle, never the per-RID subfolder
**This is the #1 macOS deploy footgun.** A multi-RID `net10.0-macos` Release build emits THREE
`.app`s under `bin/Release/net10.0-macos/`:

| Path | Use it? |
|------|---------|
| `GitHub Shine.app` (TFM root) | ✅ **YES** — finished universal x86_64+arm64 app |
| `osx-arm64/GitHub Shine.app` | ❌ incomplete lipo intermediate |
| `osx-x64/GitHub Shine.app`   | ❌ incomplete lipo intermediate |

Only the **root universal bundle** is fully assembled. The per-RID subfolders are broken in two ways:
1. **Unsigned MonoBundle dylibs** → instant `SIGKILL (Code Signature Invalid)` on launch. Crash
   report (`~/Library/Logs/DiagnosticReports/GitHubShine-*.ips`) shows `namespace: CODESIGNING` +
   `EXC_BAD_ACCESS` in `dyld … loadDependents`, before any managed stdout.
2. **Static web assets stripped** → `Contents/Resources` has ~2 files (AppIcon.icns + scoped
   `*.styles.css`) instead of the full `wwwroot/` (index.html, `_framework/blazor.webview.js`,
   `_content/Shiny.Blazor.Controls/*`, css). Blazor then renders components in-process (logs look
   fine) but the WebView is blank/unstyled.

The root universal bundle from a plain `dotnet build -c Release -f net10.0-macos` has **all assets
AND validly ad-hoc-signed dylibs** — no `publish`, no manual re-signing needed.
**Do NOT use `dotnet publish` to "fix signing" — it strips the static web assets down to ~2 files too.**

### Deploy recipe (macOS)
```bash
dotnet build src/GitHubShine/GitHubShine.csproj -c Release -f net10.0-macos
cp -R "src/GitHubShine/bin/Release/net10.0-macos/GitHub Shine.app" ~/Desktop/   # root, NOT osx-arm64/
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f ~/Desktop/"GitHub Shine.app"
```

### Verify a macOS bundle is healthy
```bash
find "<app>/Contents/Resources" -type f | wc -l                 # expect ~33, NOT 2
codesign -dvv "<app>/Contents/MonoBundle/libcoreclr.dylib"      # want flags=0x2(adhoc); 0x10000(runtime)=broken intermediate
codesign --verify --deep --verbose=2 "<app>"                    # want "valid on disk"
```
`cp -R` preserves the ad-hoc signature.

## Gotcha 4 — "app icon missing" on macOS is a cache/wrong-bundle issue, not a bad icon
The `.icns` generated from `<MauiIcon>` svg is complete and valid (all sizes 16→1024px). A missing
icon means either the crashing/incomplete intermediate bundle was deployed (Gotcha 3), or Finder's
LaunchServices icon cache is stale from repeated redeploys — fix with `lsregister -f "<app>"`.
`LSUIElement` is deliberately NOT set; the menu-bar/tray-only behaviour is done in code via
`NSApplication.ActivationPolicy`, so it IS a normal Dock app and shows a Dock icon.

## Debug vs Release (macOS) quick reference
- **Debug** (`dotnet build -c Debug`) produces a single, complete, ad-hoc-signed bundle at
  `bin/Debug/net10.0-macos/osx-arm64/GitHub Shine.app` — good for local testing.
- **Release** — always take the **universal** bundle at the TFM root (Gotcha 3).
- To capture a startup crash: run the inner binary directly
  (`"<app>/Contents/MacOS/GitHubShine"`) and read stdout; a code-signing kill produces no stdout —
  check the `.ips` crash report instead.
