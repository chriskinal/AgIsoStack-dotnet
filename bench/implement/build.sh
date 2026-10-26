#!/bin/bash
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 The SharpIsoTerm contributors
#
# Builds the bench's test implements on Linux, for the gateway's architecture, into
# bench/implement/out/: upstream's VT example (VT3ExampleTarget, 16 frames per data packet offset)
# and the same with 255 (VT3Example255, dpo255.patch). Both carry upstream PR #793
# (pr793-transmit-queue.patch): without it the example's VT client thread and the stack's thread
# both push to the single-producer transmit queue and frames are lost.
# Both take VT_INSTANCE=n from the environment (vt-instance.patch): connect only to the terminal
# with that function instance, for several terminals on one bus.
#   bench/implement/build.sh [AGISOSTACK_COMMIT]
# On a Mac, in an arm64 Linux container, for example:
#   docker run --rm -v "$PWD":/src -w /src aow-hat-dev bench/implement/build.sh
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
PIN=${1:-$(sed -n 's/.*AGISOSTACK_GIT_TAG "\([0-9a-f]*\)".*/\1/p' "$HERE/../../native/CMakeLists.txt")}
W=$HERE/out/work
mkdir -p "$HERE/out"
if [ ! -d "$W/stack/.git" ]; then git clone -q https://github.com/Open-Agriculture/AgIsoStack-plus-plus.git "$W/stack"; fi
cd "$W/stack"
git checkout -q -f "$PIN"
git apply "$HERE/pr793-transmit-queue.patch"
git apply "$HERE/vt-instance.patch"
cmake -S . -B "$W/build" -G "Unix Makefiles" -DBUILD_EXAMPLES=ON -DCAN_DRIVER=SocketCAN -DCMAKE_BUILD_TYPE=Release >/dev/null
T=examples/virtual_terminal/version3_object_pool
cmake --build "$W/build" -j"$(nproc)" --target VT3ExampleTarget
cp "$W/build/$T/VT3ExampleTarget" "$HERE/out/"
git apply "$HERE/dpo255.patch"
cmake --build "$W/build" -j"$(nproc)" --target VT3ExampleTarget
cp "$W/build/$T/VT3ExampleTarget" "$HERE/out/VT3Example255"
git checkout -q -f "$PIN"
echo "built at $PIN: $(ls "$HERE/out"/VT3Example*)"
