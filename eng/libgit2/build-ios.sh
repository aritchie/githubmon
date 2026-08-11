#!/usr/bin/env bash
#
# Builds libgit2 as a dynamic library for iOS — one for the device, one fat one for the simulator —
# and drops them where the csproj picks them up (src/GitHubShine/Platforms/iOS/native/<platform>/).
#
# Read eng/libgit2/README.md first: the libgit2 commit and the output file name are both pinned to
# whatever LibGit2Sharp version the app references, and getting either wrong fails at runtime
# rather than at build time.
#
# Two things differ from the Android build:
#
#   * TLS is SecureTransport, i.e. the system's own. So there is no mbedTLS to build and no CA
#     certificate wiring at all — iOS supplies the trust store.
#   * The file is named git2-5853918.dylib, with NO "lib" prefix. That is not cosmetic:
#     LibGit2Sharp probes exactly Path.Combine(GlobalSettings.NativeLibraryPath, "git2-5853918" +
#     ".dylib"), and GitRuntime points that at the app bundle on iOS. cmake insists on the prefix,
#     so the file is renamed and its install_name rewritten afterwards.
#
# Device and simulator slices cannot be lipo'd together (same arm64 arch, different platform), so
# they stay separate files and the csproj picks one by RuntimeIdentifier.
#
# Usage: eng/libgit2/build-ios.sh

set -euo pipefail

LIBGIT2_COMMIT=5853918c4c6a7b12f8becf4bd11ff4362ebb9020   # v1.8.6 — LibGit2Sharp 0.32.0
LIBGIT2_FILENAME=git2-5853918
IOS_DEPLOYMENT_TARGET=15.0

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK="${LIBGIT2_WORK_DIR:-$ROOT/artifacts/libgit2}"
OUT="$ROOT/src/GitHubShine/Platforms/iOS/native"

CMAKE="${CMAKE:-$(command -v cmake || echo /opt/homebrew/bin/cmake)}"
NINJA="${NINJA:-$(command -v ninja || echo /opt/homebrew/bin/ninja)}"

mkdir -p "$WORK"

if [ ! -d "$WORK/libgit2/.git" ]; then
    echo "==> fetching libgit2"
    git clone -q --filter=blob:none https://github.com/libgit2/libgit2.git "$WORK/libgit2"
fi
git -C "$WORK/libgit2" checkout -q "$LIBGIT2_COMMIT"
git -C "$WORK/libgit2" checkout -q -- .

# $1 = label (device|simulator), $2 = sysroot, $3 = archs
build_slice() {
    local LABEL="$1" SYSROOT="$2" ARCHS="$3"
    local BUILD="$WORK/build/ios-$LABEL"
    local SDKPATH
    SDKPATH="$(xcrun --sdk "$SYSROOT" --show-sdk-path)"

    echo
    echo "=============== iOS $LABEL ($ARCHS) ==============="

    # libgit2 only looks for CoreFoundation and Security when CMAKE_SYSTEM_NAME is "Darwin", and
    # cross-compiling to iOS sets it to "iOS" — so the finders never run and the SecureTransport
    # backend refuses to configure ("CoreFoundation.framework not found") even though both
    # frameworks are right there in the SDK. Supplying the handful of variables those finders would
    # have produced is enough; they only feed hardcoded "-framework X" link flags.
    local FRAMEWORKS="$SDKPATH/System/Library/Frameworks"

    "$CMAKE" -S "$WORK/libgit2" -B "$BUILD" -G Ninja \
        -DCMAKE_MAKE_PROGRAM="$NINJA" \
        -DCMAKE_SYSTEM_NAME=iOS \
        -DCMAKE_OSX_SYSROOT="$SYSROOT" \
        -DCMAKE_OSX_ARCHITECTURES="$ARCHS" \
        -DCMAKE_OSX_DEPLOYMENT_TARGET="$IOS_DEPLOYMENT_TARGET" \
        -DCMAKE_BUILD_TYPE=Release \
        `# @rpath, so the dylib resolves from wherever the app bundle puts it.` \
        -DCMAKE_INSTALL_NAME_DIR="@rpath" \
        -DCMAKE_MACOSX_RPATH=ON \
        -DBUILD_SHARED_LIBS=ON \
        -DLIBGIT2_FILENAME="$LIBGIT2_FILENAME" \
        -DSONAME=ON \
        -DBUILD_TESTS=OFF \
        -DBUILD_CLI=OFF \
        -DBUILD_EXAMPLES=OFF \
        -DUSE_HTTPS=SecureTransport \
        -DCOREFOUNDATION_FOUND=TRUE \
        -DCOREFOUNDATION_LIBRARIES="$FRAMEWORKS/CoreFoundation.framework" \
        -DCOREFOUNDATION_LDFLAGS="-framework CoreFoundation" \
        -DSECURITY_FOUND=TRUE \
        -DSECURITY_LIBRARIES="$FRAMEWORKS/Security.framework" \
        -DSECURITY_LDFLAGS="-framework Security" \
        -DSECURITY_INCLUDE_DIR="$SDKPATH/usr/include" \
        `# SSLCreateContext is probed by try_compile, which can't link a framework by path here.` \
        `# It is present in every iOS SDK libgit2 supports — deprecated since iOS 13, still there.` \
        -DSECURITY_HAS_SSLCREATECONTEXT=1 \
        -DUSE_SHA1=CollisionDetection \
        -DUSE_SHA256=Builtin \
        -DUSE_SSH=OFF \
        -DUSE_ICONV=OFF \
        -DUSE_NTLMCLIENT=OFF \
        `# The SDK's zlib, not libgit2's bundled copy: that copy still treats TARGET_OS_MAC (which` \
        `# Apple defines on every platform, iOS included) as Classic Mac OS and #defines fdopen to` \
        `# NULL, which detonates the moment stdio.h is included. Apple ships zlib on iOS anyway.` \
        -DUSE_BUNDLED_ZLIB=OFF \
        -DZLIB_INCLUDE_DIR="$SDKPATH/usr/include" \
        -DZLIB_LIBRARY="$SDKPATH/usr/lib/libz.tbd" \
        -DREGEX_BACKEND=builtin \
        > "$BUILD.configure.log" 2>&1 || { tail -30 "$BUILD.configure.log"; exit 1; }

    "$CMAKE" --build "$BUILD" > "$BUILD.build.log" 2>&1 || { tail -30 "$BUILD.build.log"; exit 1; }

    local BUILT="$BUILD/lib$LIBGIT2_FILENAME.dylib"
    [ -f "$BUILT" ] || { echo "expected $BUILT, and it isn't there" >&2; exit 1; }

    mkdir -p "$OUT/$LABEL"
    local DEST="$OUT/$LABEL/$LIBGIT2_FILENAME.dylib"
    cp -L "$BUILT" "$DEST"

    # cmake stamped the install_name from the prefixed name it built; rewrite it to the name the
    # file actually has now, or dyld looks for a libgit2-… that isn't in the bundle.
    install_name_tool -id "@rpath/$LIBGIT2_FILENAME.dylib" "$DEST"
    strip -x -S "$DEST"
    codesign --remove-signature "$DEST" 2>/dev/null || true

    echo "==> $DEST ($(du -h "$DEST" | cut -f1))"
    lipo -info "$DEST"
}

build_slice device    iphoneos        "arm64"
build_slice simulator iphonesimulator "arm64;x86_64"

echo
echo "Done."
