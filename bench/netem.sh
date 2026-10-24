#!/bin/bash
# On the CM4: impair the relay's TCP traffic (port 2323 only) in both directions with netem, on
# NETEM_DEV (default wlan0; eth0 works too, since ssh is left alone).
#   netem.sh off | netem.sh set "NETEM ARGS"   (same args each way, e.g. "delay 25ms 10ms loss 1%")
#   netem.sh stall AFTER_S FOR_S               (background: 100% loss both ways, then back to before)
set -u
D=${NETEM_DEV:-wlan0}
P=2323
tc=/usr/sbin/tc
setup() {  # prio root on DEV and ifb0, band 3 holds netem, port 2323 is filtered into it
  sudo modprobe ifb numifbs=1; sudo modprobe sch_netem; sudo modprobe act_mirred
  sudo ip link set ifb0 up
  for i in $D ifb0; do
    $tc qdisc show dev $i | grep -q "prio 1:" && continue
    sudo $tc qdisc replace dev $i root handle 1: prio bands 3 priomap 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0
    sudo $tc qdisc add dev $i parent 1:3 handle 30: netem limit 10000 delay 0ms
  done
  $tc qdisc show dev $D | grep -q ingress || {
    sudo $tc qdisc add dev $D handle ffff: ingress
    sudo $tc filter add dev $D parent ffff: protocol ip u32 match ip protocol 6 0xff match ip dport $P 0xffff action mirred egress redirect dev ifb0
  }
  $tc filter show dev $D parent 1: | grep -q flowid || \
    sudo $tc filter add dev $D parent 1: protocol ip u32 match ip protocol 6 0xff match ip sport $P 0xffff flowid 1:3
  $tc filter show dev ifb0 parent 1: | grep -q flowid || \
    sudo $tc filter add dev ifb0 parent 1: protocol ip u32 match ip protocol 6 0xff match ip dport $P 0xffff flowid 1:3
}
apply() { for i in $D ifb0; do sudo $tc qdisc change dev $i parent 1:3 handle 30: netem limit 10000 $1; done; }
case "$1" in
  off)
    for i in wlan0 eth0; do sudo $tc qdisc del dev $i root 2>/dev/null; sudo $tc qdisc del dev $i ingress 2>/dev/null; done
    sudo $tc qdisc del dev ifb0 root 2>/dev/null; rm -f /tmp/netem.cur; echo off ;;
  set)
    setup; apply "$2"; echo "$2" > /tmp/netem.cur; echo "set on $D: $2" ;;
  stall)
    setup; cur=$(cat /tmp/netem.cur 2>/dev/null || echo "delay 0ms"); apply "$cur"
    nohup bash -c "sleep $2; for i in $D ifb0; do sudo $tc qdisc change dev \$i parent 1:3 handle 30: netem limit 10000 loss 100%; done
      echo \"\$(date +%T.%N) stall on\" >> ~/vtbench/stall.log; sleep $3
      for i in $D ifb0; do sudo $tc qdisc change dev \$i parent 1:3 handle 30: netem limit 10000 $cur; done
      echo \"\$(date +%T.%N) stall off\" >> ~/vtbench/stall.log" > /dev/null 2>&1 < /dev/null &
    echo "stall in $2 s for $3 s on $D" ;;
  show) for i in $D ifb0; do $tc -s qdisc show dev $i; $tc filter show dev $i parent 1:; done ;;
esac
