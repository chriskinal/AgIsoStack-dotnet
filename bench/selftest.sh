#!/bin/bash
# The in-process self-test (IsoShimSpike.RunAll: no bus, no gateway) on the iPad and the Android
# tablet, and on Linux and Windows from the Mac; `dotnet run` in dotnet/console does the same on
# the desktop.
#   selftest.sh [ios] [android] [linux] [windows]     (none: ios android)
#   linux    in .NET SDK containers (Docker, OrbStack), arm64 and x64 (x64 under Rosetta), with
#            native/build-linux-RID/ from native/build.sh linux
#   windows  win-x64, self-contained, in the Parallels VM $WIN_VM (devices.env), with
#            native/build-windows/ from native/build.sh windows; runs from %TEMP%\isoshim-test, its
#            output to bench/out/selftest-win-x64.log over the share (prlctl exec loses some of a
#            program's output)
. "$(dirname "$0")/env.sh"
REPO=$(cd "$BENCH/.." && pwd)
F='^---|isoshim|PASS|FAIL|stopped|callback'
for t in ${@:-ios android}; do
  if [ $t = linux ]; then
    for P in arm64 amd64; do
      echo "=== Linux $P"
      docker run --rm --platform linux/$P -v "$REPO:/src:ro" mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
        mkdir -p /w && cd /src && tar cf - --exclude=bin --exclude=obj dotnet testpools/*.iop | tar xf - -C /w
        for d in /src/native/build-linux-*; do mkdir -p /w/native/${d##*/}; cp $d/libisoshim.so /w/native/${d##*/}/; done
        cd /w/dotnet/console && dotnet run -c Release' 2>&1 | grep -E "$F| error |Unhandled"
    done
  elif [ $t = windows ]; then
    echo "=== Windows x64 (${WIN_VM:?set WIN_VM, the Parallels VM name, in bench/devices.env})"
    W=$OUT/selftest-win-x64
    L=$OUT/selftest-win-x64.log
    rm -rf "$W" "$L"
    dotnet publish "$REPO/dotnet/console" -c Release -r win-x64 --self-contained -p:DesktopOnly=true -o "$W" | grep -E " error "
    prlctl resume "$WIN_VM" >/dev/null 2>&1
    for i in $(seq 1 60); do prlctl exec "$WIN_VM" --current-user cmd.exe /c ver >/dev/null 2>&1 && break; sleep 1; done
    prlctl exec "$WIN_VM" --current-user cmd.exe /c "robocopy \\\\Mac\\AllFiles${W//\//\\} %TEMP%\\isoshim-test /MIR /NFL /NDL /NJH /NJS >nul & %TEMP%\\isoshim-test\\IsoShimConsole.exe > \\\\Mac\\AllFiles${L//\//\\} 2>&1" 2>&1 | tr -d '\r' | grep -vE "^\s*$"
    if [ -f "$L" ]; then tr -d '\r' < "$L" | grep -E "$F|Unhandled"; else echo "FAIL: the VM wrote no log (prlctl exec's own output, if any, is above)"; fi
  elif [ $t = ios ]; then
    echo "=== iPad"
    L=$OUT/selftest-ios.log
    xcrun devicectl device process launch --device "$IPAD_UDID" --console --terminate-existing $IOS_APP > $L 2>&1 &
    P=$!
    for i in $(seq 1 120); do grep -qE "ALL PASS|SOME FAILED|libc..abi|signal 6" $L && break; sleep 1; done
    sleep 1; kill $P 2>/dev/null
    grep ISOSHIM $L | sed 's/^.*ISOSHIM //' | grep -E "$F"; grep -m1 -E "libc..abi|signal 6" $L
  else
    echo "=== Android"
    adb logcat -c
    adb shell am start -S -n $ANDROID_ACTIVITY >/dev/null
    for i in $(seq 1 120); do adb logcat -d -s ISOSHIM:I | grep -qE "ALL PASS|SOME FAILED" && break; sleep 1; done
    adb logcat -d -s ISOSHIM:I | sed -E 's/^.*ISOSHIM *: //' | grep -E "$F"
    adb logcat -d | grep -m3 -E "libc..abi|Fatal signal"
    adb shell am force-stop $ANDROID_APP
  fi
done
