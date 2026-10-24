#!/bin/bash
# On the gateway: relay up | implement start | stop all. Logs in ~/vtbench (GW_DIR).
#   gateway.sh relay | gateway.sh implement [BINARY] | gateway.sh stop
cd ~/"${GW_DIR:-vtbench}"
RELAY_IF=${RELAY_IF:-hatcan2} IMPL_IF=${IMPL_IF:-hatcan3}
case "$1" in
  relay)  pkill -f can_tcp_relay.py; nohup python3 -u can_tcp_relay.py $RELAY_IF ${RELAY_PORT:-2323} > relay.log 2>&1 & sleep 0.5; tail -1 relay.log ;;
  implement) pkill -f "^./VT3Example"; nohup stdbuf -oL ./${2:-VT3ExampleTarget} --interface $IMPL_IF --driver socketcan > implement.log 2>&1 & echo started ;;
  stop)   pkill -f "^./VT3Example"; sleep 0.5; pkill -f can_tcp_relay.py; echo stopped ;;
esac
