# libgit2 natives for the mobile heads

`LibGit2Sharp.NativeBinaries` ships Windows, Linux and macOS RIDs only — there is no published
libgit2 native for iOS or Android on nuget.org, from anyone. The desktop heads get theirs from the
package; the mobile heads get theirs from `build-android.sh` here.

## The two things that must match

1. **The libgit2 commit.** LibGit2Sharp's P/Invoke layer mirrors the struct layouts of the exact
   libgit2 it was built against. That commit is pinned as `LIBGIT2_COMMIT` in the build script —
   currently `5853918` (libgit2 v1.8.6), which is what LibGit2Sharp 0.32.0 uses.
2. **The file name.** LibGit2Sharp DllImports `git2-<short sha>` — `git2-5853918` — so the built
   library has to be `libgit2-5853918.so`. `-DLIBGIT2_FILENAME` handles that.

Both come off the same short sha, which you can read at runtime:

```
GlobalSettings.Version.InformationalVersion
=> 0.32.0+libgit2-5853918.eaa698d078941fd5e3cc82b59b885cd35d8cc0f8
             ^^^^^^^ libgit2 commit    ^^^ LibGit2Sharp commit
```

**Bumping the LibGit2Sharp package almost certainly changes both.** After a bump: read the new
informational version, update `LIBGIT2_COMMIT`/`LIBGIT2_FILENAME` here, rebuild, and update the
`AndroidNativeLibrary` file names in `GitHubShine.csproj`. A mismatch does not fail the build — it
fails at runtime, either as "Git support isn't available" (name wrong, nothing to load) or, worse,
as memory corruption (name right, layouts wrong).

## Building

```bash
eng/libgit2/build-android.sh                 # arm64-v8a and x86_64
eng/libgit2/build-android.sh arm64-v8a       # just one
```

Needs cmake, ninja and an Android NDK (found automatically under `~/Library/Android/sdk/ndk`, or
set `ANDROID_NDK_HOME`). Sources are cloned into `artifacts/libgit2/` and the finished `.so` files
are written to `src/GitHubShine/Platforms/Android/native/<abi>/`, which is where the csproj picks
them up. Those binaries **are committed**, so an ordinary `dotnet build` needs none of this.

## Choices worth knowing about

- **mbedTLS, not OpenSSL.** Android has no system TLS library to link against. mbedTLS is built
  statically into the `.so`, so the only runtime dependencies are `libc`, `libm` and `libdl`.
- **CA certificates are set at runtime.** mbedTLS trusts nothing until it's handed a directory of
  certificates. The build bakes in `/system/etc/security/cacerts`, and `GitRuntime` also sets it at
  startup (preferring `/apex/com.android.conscrypt/cacerts`, which is where the trust store moved
  in Android 14).
- **`-Dinline=__inline__`.** libgit2 compiles with a strict `-std=c90` set per-target (so it can't
  be overridden), and bionic's `linux/swab.h` declares `inline` functions. Without this the build
  fails on the very first source file.
- **`SONAME=ON`.** Counter-intuitive, but libgit2 only honours `LIBGIT2_FILENAME` inside that
  branch, and the rename is the whole point. The script copies the plain `.so` symlink's target,
  because an APK only extracts files whose names end in `.so`.
- **The package's own natives are excluded on Android** (`ExcludeAssets="all"` on
  `LibGit2Sharp.NativeBinaries` — see the csproj). The RID graph falls `android-arm64` →
  `linux-arm64`, so without that, the package's glibc build is packaged under the same file name
  and wins; it then fails to load on device looking for `libc.so.6`.

## iOS

```bash
eng/libgit2/build-ios.sh        # device + simulator, no arguments
```

Produces `src/GitHubShine/Platforms/iOS/native/device/git2-5853918.dylib` (arm64) and
`.../simulator/git2-5853918.dylib` (arm64 + x86_64). They stay separate files rather than one
xcframework because the csproj picks between them by `RuntimeIdentifier`, and device and simulator
slices can't be lipo'd together anyway — both are arm64, differing only in platform.

Choices worth knowing about:

- **SecureTransport, not mbedTLS.** iOS supplies the trust store, so there is no CA certificate
  wiring at all. Note the `git_libgit2_opts` P/Invoke in `GitRuntime` is deliberately Android-only:
  it's a variadic C function, and Apple's arm64 ABI passes variadic arguments on the stack rather
  than in registers, so that declaration would be wrong here.
- **The SDK's zlib, not the bundled copy**, which still reads `TARGET_OS_MAC` (defined on every
  Apple platform) as Classic Mac OS and `#define`s `fdopen` to `NULL`, breaking `stdio.h`.
- **CoreFoundation/Security are passed in by hand.** libgit2 only looks for them when
  `CMAKE_SYSTEM_NAME` is `Darwin`, and cross-compiling sets it to `iOS`, so the SecureTransport
  backend refuses to configure until the variables those finders would have set are supplied.
- **No "lib" prefix.** LibGit2Sharp probes exactly `git2-5853918.dylib` next to the managed
  assemblies, so cmake's `libgit2-5853918.dylib` is renamed and its install_name rewritten.
- **`@(BundleResource)`, not `@(NativeReference Kind="Dynamic")`.** The latter does not work on
  iOS: it links the dylib into the app binary and rewrites the path to
  `@executable_path/Contents/MonoBundle/…` — the *macOS* bundle layout, hardcoded as
  `_CustomBundleName` in `Xamarin.Shared.targets` — while dropping the file at the root of the flat
  iOS bundle, so dyld can't find it and the app dies before `Main`. As a bundle resource there is
  no load command at all and the DllImport resolves it lazily. The trade-off is that nothing else
  signs it, so the csproj has a `_SignLibGit2Native` target that codesigns it for device builds
  before the bundle is sealed.

**If the iOS app crashes on launch in `ObjCRuntime.Class.ResolveTokenReference`** after a package
or native change, it's stale registrar state, not your code: delete `obj/Debug/net10.0-ios` and
`bin/Debug/net10.0-ios` and rebuild.
