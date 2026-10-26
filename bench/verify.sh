#!/bin/bash
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 The SharpIsoTerm contributors
#
# N runs without the capture; per run the verdict, upload attempts and ETP batches the relay saw
# with a frame missing.
#   verify.sh CLIENT IMPLEMENT N
. "$(dirname "$0")/env.sh"
for i in $(seq 1 $3); do
  NOSNIFF=1 bash "$BENCH/run.sh" "$1" "verify-$i" "$2" > /dev/null 2>&1
  f=$OUT/verify-$i.txt
  v=$(grep -oE "PASS in [0-9]+ ms|FAIL" $f | head -1)
  a=$(grep -oE "upload attempts: .*" $f)
  bad=$(gw "cd $GW_DIR; touch empty.log; python3 etpcheck.py empty.log dump-verify-$i.log | grep -c BAD")
  echo "run $i: $v | $a | bad batches in relay dump: $bad"
done
