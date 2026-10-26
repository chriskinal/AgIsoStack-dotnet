#!/bin/bash
# The bench without hardware: in one Linux container (Docker, OrbStack, privileged for vcan and
# netem), the reference gateway on a virtual CAN bus, upstream's example implement on the same bus,
# and the terminal (the console app's bench mode: AowLink and VirtualTerminal) over loopback TCP.
#   local.sh [IMPLEMENT] [STALL...]     IMPLEMENT: VT3Example255 (default) or VT3ExampleTarget
#   STALL "AFTER FOR": cut the link (loopback, 100% loss) AFTER s into the run for FOR s; several
#   may be given. Needs native/build-linux-arm64 and bench/implement/out (build.sh for each).
# Output: bench/out/local-IMPLEMENT.txt (the terminal), local-gateway.log, local-implement.log,
# local-cuts.log. CHECK=1 also runs bench/aowlink_check.py against the gateway afterwards
# (the implement still on the bus). GATEWAY=/path/to/aow-linkd runs that gateway (gateway/linux,
# a Linux arm64 build) instead of bench/aowlink_gateway.py.
set -u
REPO=$(cd "$(dirname "$0")/.." && pwd)
IMPL=${1:-VT3Example255}
shift || true
mkdir -p "$REPO/bench/out"
GW_MOUNT=()
[ -n "${GATEWAY:-}" ] && GW_MOUNT=(-v "$(cd "$(dirname "$GATEWAY")" && pwd)/$(basename "$GATEWAY"):/gateway:ro")
docker run --rm --privileged --platform linux/arm64 -v "$REPO:/src:ro" -v "$REPO/bench/out:/out" ${GW_MOUNT[@]+"${GW_MOUNT[@]}"} \
  -e IMPL="$IMPL" -e STALLS="$*" -e CHECK="${CHECK:-}" mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  set -u
  (apt-get update -qq && apt-get install -y -qq iproute2 python3 >/dev/null) >/dev/null 2>&1
  ip link add vcan0 type vcan && ip link set vcan0 up || { echo "no vcan"; exit 1; }
  mkdir -p /w/native /w/impl && cd /src && tar cf - --exclude=bin --exclude=obj dotnet testpools/*.iop | tar xf - -C /w
  cp -r /src/native/build-linux-arm64 /w/native/ 2>/dev/null
  cp /src/bench/implement/out/VT3Example* /src/dotnet/VT3TestPool.iop /w/impl/
  (cd /w/dotnet/console && dotnet build -c Release -v q -nologo 2>&1 | grep -E " error " )
  cd /out
  rm -f local-cuts.log
  if [ -e /gateway ]; then /gateway --can vcan0 --port 2323 > local-gateway.log 2>&1 &
  else python3 -u /src/bench/aowlink_gateway.py vcan0 2323 > local-gateway.log 2>&1 &
  fi
  sleep 0.5
  (cd /w/dotnet/console && dotnet bin/Release/net10.0/IsoShimConsole.dll bench 127.0.0.1 2323 > /out/local-$IMPL.txt 2>&1) &
  T=$!
  sleep 3
  (cd /w/impl && ./$IMPL --interface vcan0 --driver socketcan > /out/local-implement.log 2>&1 &)
  set -- $STALLS
  START=$(date +%s.%N)
  while [ $# -ge 2 ]; do
    AT=$1 FOR=$2; shift 2
    while [ $(echo "$(date +%s.%N) - $START < $AT" | bc 2>/dev/null || python3 -c "import time;print(int(time.time()-$START<$AT))") = 1 ]; do sleep 0.05; done
    tc qdisc add dev lo root netem loss 100% && echo "$(date +%T.%N) cut $FOR s" >> /out/local-cuts.log
    sleep $FOR
    tc qdisc del dev lo root && echo "$(date +%T.%N) cut over" >> /out/local-cuts.log
  done
  wait $T
  if [ -n "${CHECK:-}" ]; then echo "--- conformance"; python3 /src/bench/aowlink_check.py 127.0.0.1 2323; fi
  pkill -f VT3Example; pkill -f aowlink_gateway; pkill -x gateway
  cat /out/local-$IMPL.txt
  echo "--- gateway"; grep -E "client" /out/local-gateway.log; cat /out/local-cuts.log 2>/dev/null
  echo "--- implement"; grep -E "VT\]|ETP|rror|arn" /out/local-implement.log | head -12'
