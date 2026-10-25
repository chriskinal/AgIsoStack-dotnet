#!/bin/bash
# On the gateway: the CAN link gateway up | implement start | stop all. Logs in ~/vtbench (GW_DIR).
#   gateway.sh relay | gateway.sh implement [BINARY] | gateway.sh stop | gateway.sh check
cd ~/"${GW_DIR:-vtbench}"
RELAY_IF=${RELAY_IF:-hatcan2} IMPL_IF=${IMPL_IF:-hatcan3}
case "$1" in
  relay)  pkill -f canlink_gateway.py; nohup python3 -u canlink_gateway.py $RELAY_IF ${RELAY_PORT:-2323} > relay.log 2>&1 & sleep 0.5; tail -1 relay.log ;;
  check)  python3 canlink_check.py 127.0.0.1 ${RELAY_PORT:-2323} ;;
  implement) pkill -f "^./VT3Example"; nohup stdbuf -oL ./${2:-VT3ExampleTarget} --interface $IMPL_IF --driver socketcan > implement.log 2>&1 & echo started ;;
  stop)   pkill -f "^./VT3Example"; sleep 0.5; pkill -f canlink_gateway.py; echo stopped ;;
esac
