#!/bin/bash
# SPDX-License-Identifier: MIT
# Copyright (c) 2026 The SharpIsoTerm contributors
#
# On the gateway: the AOW link gateway up | implement start | stop all. Logs in ~/vtbench (GW_DIR).
#   gateway.sh relay | gateway.sh implement [BINARY] | gateway.sh stop | gateway.sh check | gateway.sh clients
# With the HAT's aow-linkd service running (AgOpenWeb-HAT 0.2.24 on), relay leaves the link to it
# and clients reads its journal; otherwise relay starts the reference gateway, aowlink_gateway.py.
cd ~/"${GW_DIR:-vtbench}"
RELAY_IF=${RELAY_IF:-hatcan2} IMPL_IF=${IMPL_IF:-hatcan3}
case "$1" in
  relay)  pkill -f aowlink_gateway.py
          if systemctl is-active -q aow-linkd 2>/dev/null; then date +%s > linkd.since; echo "aow-linkd (service)"
          else rm -f linkd.since; nohup python3 -u aowlink_gateway.py $RELAY_IF ${RELAY_PORT:-2323} > relay.log 2>&1 & sleep 0.5; tail -1 relay.log; fi ;;
  clients) if [ -f linkd.since ]; then journalctl -u aow-linkd --since @$(cat linkd.since) -o cat --no-pager | grep -E client
           else grep -E client relay.log; fi ;;
  check)  python3 aowlink_check.py 127.0.0.1 ${RELAY_PORT:-2323} ;;
  implement) pkill -f "^./VT3Example"; nohup stdbuf -oL ./${2:-VT3ExampleTarget} --interface $IMPL_IF --driver socketcan > implement.log 2>&1 & echo started ;;
  stop)   pkill -f "^./VT3Example"; sleep 0.5; pkill -f aowlink_gateway.py; echo stopped ;;
esac
