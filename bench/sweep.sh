#!/bin/bash
# N runs under one link impairment, one summary line each (out/net-TAG-i.txt for the full logs).
#   sweep.sh CLIENT IMPLEMENT N TAG "NETEM ARGS"|none [STALL_AFTER STALL_FOR]
. "$(dirname "$0")/env.sh"
SHAPE="NETEM_DEV=${NETEM_DEV:-wlan0} $GW_DIR/netem.sh"
if [ "$5" = none ]; then gw "$SHAPE off" >/dev/null; else gw "$SHAPE set '$5'" >/dev/null; fi
for i in $(seq 1 $3); do
  n=net-$4-$i f=$OUT/net-$4-$i.txt
  if [ -n "${6:-}" ]; then STALL="$6 $7" NOSNIFF=1 bash "$BENCH/run.sh" "$1" $n "$2" > /dev/null 2>&1
  else NOSNIFF=1 bash "$BENCH/run.sh" "$1" $n "$2" > /dev/null 2>&1; fi
  v=$(grep -oE "PASS in [0-9]+ ms|FAIL" $f | head -1)
  a=$(grep -oE "upload attempts: .*" $f)
  r=$(grep -oE "p50 [0-9.]+ +p90 [0-9.]+ +p99 [0-9.]+ +max [0-9.]+" $f | tr -s ' ')
  echo "$4 run $i: ${v:-no verdict} | $a | rtt $r | stall $(grep -c 'stall o' $f)/2 | stopped $(grep -c '^stopped' $f) | crash $(cat $f.raw 2>/dev/null | grep -cE 'libc..abi|signal 6')"
done
gw "$SHAPE off" >/dev/null
