#!/bin/bash
# Draws every screen of every test pool and says which changed since the last run.
#   bench/pools.sh [name...]   (default: all)
# Pools: ours (testpools/*.iop, and upstream's dotnet/VT3TestPool.iop), whose screens are kept in
# testpools/expected/ and committed, so a change shows in git diff; and other people's, anywhere
# under testpools/local/ (git-ignored), kept in testpools/local/expected/. Each pool's screens go
# to testpools/local/out/NAME/ and one page shows them all (printed at the end).
set -uo pipefail
ROOT=$(cd "$(dirname "$0")/.." && pwd)
OUT=$ROOT/testpools/local/out
mkdir -p "$OUT" "$ROOT/testpools/expected" "$ROOT/testpools/local/expected"
(cd "$ROOT/dotnet/console" && dotnet build -c Release -v q >/dev/null) || { echo "build failed"; exit 1; }

POOLS=("$ROOT"/testpools/*.iop "$ROOT"/dotnet/VT3TestPool.iop)
while IFS= read -r f; do POOLS+=("$f"); done < <(find "$ROOT/testpools/local" -name '*.iop' -not -path "$OUT/*" | sort)

names=() changed=0 failed=0
for pool in "${POOLS[@]}"; do
  name=$(basename "$pool" .iop)
  if [ $# -gt 0 ] && [[ ! " $* " =~ " $name " ]]; then continue; fi
  case $pool in
    "$ROOT"/testpools/local/*) expected=$ROOT/testpools/local/expected/$name.jsonl ;;
    *) expected=$ROOT/testpools/expected/$name.jsonl ;;
  esac
  rm -rf "${OUT:?}/$name"
  mkdir -p "$OUT/$name"
  if ! (cd "$ROOT/dotnet/console" && dotnet run -c Release --no-build -- screens "$OUT/$name" "$pool") > "$OUT/$name/log.txt" 2>&1; then
    echo "FAIL     $name: $(grep -m1 FAIL "$OUT/$name/log.txt")"
    failed=$((failed + 1))
    continue
  fi
  names+=("$name")
  screens=$(grep -c '"type":"screen"' "$OUT/$name/screens.jsonl" 2>/dev/null)
  if [ ! -f "$OUT/$name/screens.jsonl" ]; then
    echo "nothing  $name: $(tail -1 "$OUT/$name/log.txt")"
  elif [ ! -f "$expected" ]; then
    cp "$OUT/$name/screens.jsonl" "$expected"
    echo "new      $name ($screens screens)"
  elif cmp -s "$OUT/$name/screens.jsonl" "$expected"; then
    echo "same     $name ($screens screens)"
  else
    cp "$OUT/$name/screens.jsonl" "$expected"
    echo "CHANGED  $name ($screens screens)"
    changed=$((changed + 1))
  fi
done
list=$(IFS=,; echo "${names[*]}")
echo
echo "$changed changed, $failed failed. Changes to our pools: git diff testpools/expected"
echo "All screens: file://$ROOT/painter/sheet.html?dir=../testpools/local/out&p=$list"
