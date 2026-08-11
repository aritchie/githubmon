#!/usr/bin/env bash
#
# Builds libgit2 as a shared library for Android, one .so per ABI, and drops them where the
# csproj picks them up (src/GitHubShine/Platforms/Android/native/<abi>/).
#
# Why this exists: LibGit2Sharp.NativeBinaries ships desktop RIDs only — there is no published
# libgit2 native for Android or iOS anywhere on nuget.org. Everything below is about producing a
# binary that the LibGit2Sharp managed assembly can actually bind to, which pins two things:
#
#   1. The libgit2 COMMIT must be the one LibGit2Sharp was built against, because the managed
#      P/Invoke layer mirrors that build's struct layouts. It's printed at runtime by
#      GlobalSettings.Version ("0.32.0+libgit2-<short sha>.<LibGit2Sharp sha>") and pinned in
#      LIBGIT2_COMMIT below.
#   2. The OUTPUT NAME must match the DllImport name, which LibGit2Sharp derives from that same
#      short sha: git2-5853918 -> libgit2-5853918.so. -DLIBGIT2_FILENAME does that for us.
#
# Bumping the LibGit2Sharp package invalidates both. See README.md in this folder.
#
# TLS: Android has no system OpenSSL to link against, so HTTPS goes through mbedTLS, built here
# as a static library and linked in. The CA certificates come from the device at runtime (see
# AndroidGitCerts in Platforms/Android) rather than being baked in at build time, because the
# system trust store moved between Android versions.
#
# Usage: eng/libgit2/build-android.sh [abi ...]     (default: arm64-v8a x86_64)

set -euo pipefail

LIBGIT2_COMMIT=5853918c4c6a7b12f8becf4bd11ff4362ebb9020   # v1.8.6 — LibGit2Sharp 0.32.0
LIBGIT2_FILENAME=git2-5853918                             # => libgit2-5853918.so
MBEDTLS_TAG=v3.6.4

# API 24 (Android 7.0) — the floor MAUI itself targets.
ANDROID_API=24

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK="${LIBGIT2_WORK_DIR:-$ROOT/artifacts/libgit2}"
OUT="$ROOT/src/GitHubShine/Platforms/Android/native"

ABIS=("$@")
[ ${#ABIS[@]} -eq 0 ] && ABIS=(arm64-v8a x86_64)

CMAKE="${CMAKE:-$(command -v cmake || echo /opt/homebrew/bin/cmake)}"
NINJA="${NINJA:-$(command -v ninja || echo /opt/homebrew/bin/ninja)}"

# The NDK the MAUI Android workload would use, unless one is named explicitly.
if [ -z "${ANDROID_NDK_HOME:-}" ]; then
    ANDROID_NDK_HOME="$(ls -d "$HOME/Library/Android/sdk/ndk/"* 2>/dev/null | sort -V | tail -1)"
fi
[ -d "$ANDROID_NDK_HOME" ] || { echo "No Android NDK found — set ANDROID_NDK_HOME" >&2; exit 1; }

TOOLCHAIN="$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake"
echo "NDK:    $ANDROID_NDK_HOME"
echo "cmake:  $CMAKE"
echo "ABIs:   ${ABIS[*]}"

mkdir -p "$WORK"

# ---- sources -------------------------------------------------------------------------------

if [ ! -d "$WORK/libgit2/.git" ]; then
    echo "==> fetching libgit2"
    git clone -q --filter=blob:none https://github.com/libgit2/libgit2.git "$WORK/libgit2"
fi
git -C "$WORK/libgit2" checkout -q "$LIBGIT2_COMMIT"
git -C "$WORK/libgit2" checkout -q -- .   # drop the patches below from any previous run

# CERT_LOCATION is validated with EXISTS on the *build* machine, which no Android path
# can satisfy. Without it, libgit2 bakes in whatever CA bundle this Mac happens to have — a
# build-machine path in a shipped binary. Dropping the check lets the device path be baked
# instead. (GitRuntime also sets it at runtime, which is what covers Android 14+.)
perl -0pi -e 's/\t\t\tif\(NOT EXISTS \$\{CERT_LOCATION\}\)\n\t\t\t\tmessage\(FATAL_ERROR[^\n]*\n\t\t\tendif\(\)\n//' \
    "$WORK/libgit2/cmake/SelectHTTPSBackend.cmake"

if [ ! -d "$WORK/mbedtls/.git" ]; then
    echo "==> fetching mbedTLS $MBEDTLS_TAG"
    git clone -q --depth 1 --branch "$MBEDTLS_TAG" --recurse-submodules \
        https://github.com/Mbed-TLS/mbedtls.git "$WORK/mbedtls"
fi

# ---- per-ABI build -------------------------------------------------------------------------

for ABI in "${ABIS[@]}"; do
    echo
    echo "=============== $ABI ==============="

    PREFIX="$WORK/build/$ABI/prefix"
    mkdir -p "$PREFIX"

    # mbedTLS: static, no programs/tests, and no C++ or filesystem extras we don't use.
    echo "==> mbedTLS"
    "$CMAKE" -S "$WORK/mbedtls" -B "$WORK/build/$ABI/mbedtls" -G Ninja \
        -DCMAKE_MAKE_PROGRAM="$NINJA" \
        -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN" \
        -DANDROID_ABI="$ABI" \
        -DANDROID_PLATFORM="android-$ANDROID_API" \
        -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_INSTALL_PREFIX="$PREFIX" \
        -DCMAKE_POSITION_INDEPENDENT_CODE=ON \
        -DENABLE_TESTING=OFF \
        -DENABLE_PROGRAMS=OFF \
        -DUSE_SHARED_MBEDTLS_LIBRARY=OFF \
        -DUSE_STATIC_MBEDTLS_LIBRARY=ON \
        > /dev/null
    "$CMAKE" --build "$WORK/build/$ABI/mbedtls" --target install > "$WORK/build/$ABI/mbedtls.log" 2>&1 || { tail -30 "$WORK/build/$ABI/mbedtls.log"; exit 1; }
    echo "    $(ls "$PREFIX/lib" | tr '\n' ' ')"

    # libgit2: shared, mbedTLS for HTTPS, bundled zlib and regex so nothing is expected on device.
    echo "==> libgit2"
    "$CMAKE" -S "$WORK/libgit2" -B "$WORK/build/$ABI/libgit2" -G Ninja \
        -DCMAKE_MAKE_PROGRAM="$NINJA" \
        -DCMAKE_TOOLCHAIN_FILE="$TOOLCHAIN" \
        -DANDROID_ABI="$ABI" \
        -DANDROID_PLATFORM="android-$ANDROID_API" \
        -DCMAKE_BUILD_TYPE=Release \
        `# bionic's linux/swab.h declares inline functions, which libgit2's strict -std=c90 (set` \
        `# per-target, so not overridable) rejects outright. Mapping inline to the GNU spelling` \
        `# is the standard way through it and changes nothing about libgit2's own C90 sources.` \
        -DCMAKE_C_FLAGS="-Dinline=__inline__" \
        -DCERT_LOCATION="/system/etc/security/cacerts/" \
        -DCMAKE_PREFIX_PATH="$PREFIX" \
        -DMBEDTLS_INCLUDE_DIR="$PREFIX/include" \
        -DMBEDTLS_LIBRARY="$PREFIX/lib/libmbedtls.a" \
        -DMBEDX509_LIBRARY="$PREFIX/lib/libmbedx509.a" \
        -DMBEDCRYPTO_LIBRARY="$PREFIX/lib/libmbedcrypto.a" \
        -DBUILD_SHARED_LIBS=ON \
        -DLIBGIT2_FILENAME="$LIBGIT2_FILENAME" \
        `# SONAME has to stay ON: libgit2 only honours LIBGIT2_FILENAME inside that branch, and` \
        `# the rename is the whole point — the file has to be called what LibGit2Sharp DllImports.` \
        `# It also stamps a version onto the real file, so the copy below dereferences the plain` \
        `# .so symlink; an APK only extracts names ending in .so.` \
        -DSONAME=ON \
        -DBUILD_TESTS=OFF \
        -DBUILD_CLI=OFF \
        -DBUILD_EXAMPLES=OFF \
        -DUSE_HTTPS=mbedTLS \
        -DUSE_SHA1=CollisionDetection \
        -DUSE_SHA256=Builtin \
        -DUSE_SSH=OFF \
        -DUSE_ICONV=OFF \
        -DUSE_NTLMCLIENT=OFF \
        -DUSE_BUNDLED_ZLIB=ON \
        -DREGEX_BACKEND=builtin \
        > /dev/null
    "$CMAKE" --build "$WORK/build/$ABI/libgit2" > "$WORK/build/$ABI/libgit2.log" 2>&1 || { tail -30 "$WORK/build/$ABI/libgit2.log"; exit 1; }

    SO="$WORK/build/$ABI/libgit2/lib$LIBGIT2_FILENAME.so"
    [ -f "$SO" ] || { echo "expected $SO, and it isn't there" >&2; exit 1; }

    # The NDK toolchain compiles with -g whatever the build type, so the unstripped .so is ~12MB
    # of debug info per ABI. Strip it — symbols are no use in a shipped app.
    STRIP="$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/darwin-x86_64/bin/llvm-strip"
    [ -x "$STRIP" ] || STRIP="$(ls "$ANDROID_NDK_HOME"/toolchains/llvm/prebuilt/*/bin/llvm-strip | head -1)"
    "$STRIP" --strip-unneeded "$SO"

    mkdir -p "$OUT/$ABI"
    cp -L "$SO" "$OUT/$ABI/lib$LIBGIT2_FILENAME.so"
    echo "==> $OUT/$ABI/lib$LIBGIT2_FILENAME.so  ($(du -h "$SO" | cut -f1))"
done

echo
echo "Done. Built for: ${ABIS[*]}"
