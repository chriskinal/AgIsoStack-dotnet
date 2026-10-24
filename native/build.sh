#!/bin/bash
# Builds the native library into native/build-TARGET/.
#   native/build.sh mac|ios|android|linux [cmake args...]
#     mac      libisoshim.dylib (host arch)
#     ios      libisoshim-all.a: the shim and the stack's libraries in one static archive, arm64
#     android  libisoshim.so, arm64-v8a; the NDK from $ANDROID_NDK, else the newest under
#              ~/Library/Android/sdk/ndk
#     linux    libisoshim.so (host arch)
# -DISOSHIM_STACK_DIR=/path/to/AgIsoStack-plus-plus builds against a local checkout.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
TARGET=${1:?usage: build.sh mac|ios|android|linux [cmake args...]}
shift
B=$HERE/build-$TARGET
case $TARGET in
  mac|linux)
    cmake -S "$HERE" -B "$B" -G Ninja -DCMAKE_BUILD_TYPE=RelWithDebInfo "$@" ;;
  ios)
    cmake -S "$HERE" -B "$B" -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_SYSTEM_NAME=iOS \
      -DCMAKE_OSX_SYSROOT=iphoneos -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=15.0 "$@" ;;
  android)
    NDK=${ANDROID_NDK:-$(ls -d "$HOME"/Library/Android/sdk/ndk/* 2>/dev/null | sort -V | tail -1)}
    [ -d "$NDK" ] || { echo "no Android NDK (set ANDROID_NDK)" >&2; exit 1; }
    cmake -S "$HERE" -B "$B" -G Ninja -DCMAKE_BUILD_TYPE=Release \
      -DCMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake" -DANDROID_ABI=arm64-v8a \
      -DANDROID_PLATFORM=android-23 "$@" ;;
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
