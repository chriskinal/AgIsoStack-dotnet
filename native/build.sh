#!/bin/bash
# Builds the native library into native/build-TARGET/.
#   native/build.sh mac|ios|android|linux|windows [cmake args...]
#     mac      libisoshim.dylib, universal (arm64 and x86_64)
#     ios      libisoshim-all.a: the shim and the stack's libraries in one static archive, arm64
#     android  libisoshim.so for each ABI in $ANDROID_ABIS (default arm64-v8a armeabi-v7a x86_64,
#              the ones AgOpenWeb ships), in native/build-android-ABI/; the NDK from
#              $ANDROID_NDK, else the newest under ~/Library/Android/sdk/ndk
#     windows  isoshim.dll, x64, in native/build-windows/: cross-compiled with llvm-mingw in Docker
#              (OrbStack), the C++ runtime linked in
#     linux    libisoshim.so in native/build-linux-RID/ (x64, arm64): on Linux for this machine;
#              on a Mac for each RID in $LINUX_RIDS (default x64 arm64), in Docker (OrbStack), on
#              Debian 12 (native/docker/linux.Dockerfile) so it loads on glibc 2.36 and later
# -DISOSHIM_STACK_DIR=/path/to/AgIsoStack-plus-plus builds against a local checkout.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
TARGET=${1:?usage: build.sh mac|ios|android|linux|windows [cmake args...]}
shift
B=$HERE/build-$TARGET
case $TARGET in
  mac)
    cmake -S "$HERE" -B "$B" -G Ninja -DCMAKE_BUILD_TYPE=RelWithDebInfo "-DCMAKE_OSX_ARCHITECTURES=arm64;x86_64" \
      -DCMAKE_OSX_DEPLOYMENT_TARGET=12.0 "$@" ;;
  linux)
    if [ "$(uname)" = Darwin ]; then
      for RID in ${LINUX_RIDS:-x64 arm64}; do
        PLATFORM=linux/$([ $RID = x64 ] && echo amd64 || echo arm64)
        docker build -q --platform $PLATFORM -t isoshim-linux-build:$RID -f "$HERE/docker/linux.Dockerfile" "$HERE/docker" >/dev/null
        docker run --rm --platform $PLATFORM -v "$HERE:/native" isoshim-linux-build:$RID /native/build.sh linux "$@"
      done
      exit 0
    fi
    case $(uname -m) in x86_64) RID=x64 ;; aarch64) RID=arm64 ;; *) echo "unknown arch $(uname -m)" >&2; exit 2 ;; esac
    B=$B-$RID
    cmake -S "$HERE" -B "$B" -G Ninja -DCMAKE_BUILD_TYPE=Release "$@"
    cmake --build "$B" --target isoshim
    strip --strip-unneeded "$B/libisoshim.so"
    ls -l "$B/libisoshim.so"
    exit 0 ;;
  ios)
    cmake -S "$HERE" -B "$B" -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_SYSTEM_NAME=iOS \
      -DCMAKE_OSX_SYSROOT=iphoneos -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=15.0 "$@" ;;
  android)
    NDK=${ANDROID_NDK:-$(ls -d "$HOME"/Library/Android/sdk/ndk/* 2>/dev/null | sort -V | tail -1)}
    [ -d "$NDK" ] || { echo "no Android NDK (set ANDROID_NDK)" >&2; exit 1; }
    for ABI in ${ANDROID_ABIS:-arm64-v8a armeabi-v7a x86_64}; do
      cmake -S "$HERE" -B "$B-$ABI" -G Ninja -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake" -DANDROID_ABI=$ABI \
        -DANDROID_PLATFORM=android-23 "$@"
      cmake --build "$B-$ABI" --target isoshim
      "$(ls -d "$NDK"/toolchains/llvm/prebuilt/*/bin | head -1)/llvm-strip" --strip-unneeded "$B-$ABI"/libisoshim.so
      ls -l "$B-$ABI"/libisoshim.so
    done
    exit 0 ;;
  windows)
    if [ ! -f /.dockerenv ]; then
      docker run --rm -v "$HERE:/native" mstorsjo/llvm-mingw:20261006 /native/build.sh windows "$@"
      exit 0
    fi
    cmake -S "$HERE" -B "$B" -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_SYSTEM_NAME=Windows \
      -DCMAKE_C_COMPILER=x86_64-w64-mingw32-clang -DCMAKE_CXX_COMPILER=x86_64-w64-mingw32-clang++ \
      -DCMAKE_RC_COMPILER=x86_64-w64-mingw32-windres "$@"
    cmake --build "$B" --target isoshim
    x86_64-w64-mingw32-strip --strip-unneeded "$B/isoshim.dll"
    ls -l "$B/isoshim.dll"
    exit 0 ;;
  *) echo "unknown target $TARGET" >&2; exit 2 ;;
esac
cmake --build "$B" --target isoshim
if [ "$TARGET" = ios ]; then
  libs=$(find "$B" -name libIsobus.a -o -name libUtility.a | grep -v CMakeFiles)
  libtool -static -o "$B/libisoshim-all.a" "$B/libisoshim.a" $libs 2>&1 | grep -v "same member name" || true
  echo "$B/libisoshim-all.a"
else
  ls "$B"/libisoshim.*
fi
