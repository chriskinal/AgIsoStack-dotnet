#!/bin/bash
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 The SharpIsoTerm contributors
#
# One bench run: relay up on the gateway, the terminal started on CLIENT (mac|android|ios), the
# implement started on the gateway's other CAN interface, then wait for the terminal's verdict
# and collect every log into out/NAME.txt.
#   run.sh CLIENT NAME [IMPLEMENT]     NOSNIFF=1 skips the capture; STALL="AFTER FOR" cuts the link
# The capture (sniff.py: every frame on both CAN channels) and a monitor client's record of what the
# gateway sent out (aowlink_monitor.py) come back as out/frames-NAME.log and out/monitor-NAME.log.
set -u
. "$(dirname "$0")/env.sh"
CLIENT=$1 NAME=$2 IMPL=${3:-VT3ExampleTarget}
: "${RELAY_HOST:?set RELAY_HOST (the gateway address the terminal connects to) in bench/devices.env}"
F=$OUT/$NAME.txt
GWENV="GW_DIR=$GW_DIR RELAY_PORT=$RELAY_PORT ${RELAY_IF:+RELAY_IF=$RELAY_IF} ${IMPL_IF:+IMPL_IF=$IMPL_IF}"
g() { gw "$GWENV $GW_DIR/gateway.sh $1"; }

gw "rm -f $GW_DIR/implement.log $GW_DIR/sniff.out $GW_DIR/stall.log $GW_DIR/frames.log $GW_DIR/monitor.log"
g relay >/dev/null
[ -n "${NOSNIFF:-}" ] || gw "cd $GW_DIR; nohup python3 sniff.py 130 > sniff.out 2>&1 < /dev/null &"
[ -n "${NOSNIFF:-}" ] || gw "cd $GW_DIR; nohup python3 aowlink_monitor.py 127.0.0.1 $RELAY_PORT monitor.log > monitor.out 2>&1 < /dev/null &"
case $CLIENT in
  mac)
    (cd "$BENCH/../dotnet/console" && dotnet bin/Release/net10.0/IsoShimConsole.dll bench $RELAY_HOST $RELAY_PORT > "$F" 2>&1) &
    WAIT=$! ;;
  android)
    adb logcat -c
    adb shell am start -S -n $ANDROID_ACTIVITY --es host $RELAY_HOST --ei port $RELAY_PORT >/dev/null
    WAIT= ;;
  ios)
    xcrun devicectl device process launch --device "${IPAD_UDID:?set IPAD_UDID}" --console --terminate-existing $IOS_APP bench $RELAY_HOST $RELAY_PORT > "$F.raw" 2>&1 &
    WAIT=$! ;;
esac
pause 4
g "implement $IMPL" >/dev/null
[ -n "${STALL:-}" ] && gw "NETEM_DEV=${NETEM_DEV:-wlan0} $GW_DIR/netem.sh stall $STALL" >/dev/null
for i in $(seq 1 150); do
  case $CLIENT in
    mac) kill -0 $WAIT 2>/dev/null || break ;;
    android) adb logcat -d -s ISOSHIM:I | grep -qE "PASS in|FAIL" && break ;;
    ios) grep -qE "PASS in|FAIL" "$F.raw" && break ;;
  esac
  pause 1
done
pause 1
case $CLIENT in
  android) adb logcat -d -s ISOSHIM:I | sed -E 's/^.*ISOSHIM *: //' | grep -v '^-----' > "$F"; adb shell am force-stop $ANDROID_APP ;;
  ios) kill $WAIT 2>/dev/null; grep ISOSHIM "$F.raw" | sed 's/^.*ISOSHIM //' > "$F" ;;
esac
g stop >/dev/null
{
  echo "--- relay"
  g clients | tail -2
  echo "--- sniff (frames, first seen s)"
  gw "pkill -TERM -f '[a]owlink_monitor.py'; pkill -TERM -f '[s]niff.py'; sleep 1.5; cat $GW_DIR/sniff.out 2>/dev/null"
  echo "--- implement"
  gw "grep -E 'VT\]|ETP|TP\]|rror|arn' $GW_DIR/implement.log | head -15"
  echo "--- stall"
  gw "cat $GW_DIR/stall.log 2>/dev/null"
} >> "$F"
gw "cp $GW_DIR/dump.log $GW_DIR/dump-$NAME.log 2>/dev/null"
[ -n "${NOSNIFF:-}" ] || for f in frames monitor; do scp -q "$GATEWAY:$GW_DIR/$f.log" "$OUT/$f-$NAME.log" 2>/dev/null; done
cat "$F"
