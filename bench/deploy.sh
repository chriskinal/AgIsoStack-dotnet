#!/bin/bash
# Copies the gateway side of the bench (the AOW link gateway and its checker, capture, checks, netem, gateway.sh) to the gateway,
# and the test implements if they are built (bench/implement/build.sh) with the pool they upload.
. "$(dirname "$0")/env.sh"
gw "mkdir -p $GW_DIR"
scp -q "$BENCH"/{aowlink_gateway.py,aowlink_check.py,sniff.py,etpcheck.py,netem.sh,gateway.sh} "$BENCH/../dotnet/VT3TestPool.iop" "$GATEWAY:$GW_DIR/"
for f in "$BENCH"/implement/out/VT3Example*; do [ -f "$f" ] && scp -q "$f" "$GATEWAY:$GW_DIR/"; done
gw "ls $GW_DIR"
