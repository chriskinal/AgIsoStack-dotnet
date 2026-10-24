# Sourced by the bench scripts: the devices, from bench/devices.env (not committed; see
# devices.env.example), and where results go.
BENCH=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
[ -f "$BENCH/devices.env" ] && . "$BENCH/devices.env"
RELAY_PORT=${RELAY_PORT:-2323}
GW_DIR=${GW_DIR:-vtbench}
ADB_BIN=${ADB_BIN:-$HOME/Library/Android/sdk/platform-tools/adb}
ANDROID_APP=com.agopenweb.isostack_tests
ANDROID_ACTIVITY=$ANDROID_APP/crc649403d36281277178.MainActivity
IOS_APP=com.agopenweb.isostack-tests
OUT=$BENCH/out
mkdir -p "$OUT"
adb() { "$ADB_BIN" -s "${ANDROID_SERIAL:?set ANDROID_SERIAL in bench/devices.env}" "$@"; }
gw() { ssh "${GATEWAY:?set GATEWAY (user@host of the Linux CAN gateway, by ssh) in bench/devices.env}" "$@"; }
pause() { python3 -c "import time; time.sleep($1)"; }
