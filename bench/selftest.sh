#!/bin/bash
# The in-process self-test (IsoShimSpike.RunAll: no bus, no gateway) on the iPad and the Android
# tablet; `dotnet run` in dotnet/console does the same on the desktop.
#   selftest.sh [ios] [android]
. "$(dirname "$0")/env.sh"
F='^---|isoshim|PASS|FAIL|stopped|callback'
for t in ${@:-ios android}; do
  if [ $t = ios ]; then
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
