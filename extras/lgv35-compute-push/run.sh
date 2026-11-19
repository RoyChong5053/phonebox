#!/data/data/com.termux/files/usr/bin/bash
# PhoneBox compute collector supervisor: keeps compute_push.py running and restarts
# it if it exits, with a wake lock so Android does not freeze Termux while charging.
set -u
cd "$(dirname "$0")" || exit 1
mkdir -p logs
termux-wake-lock 2>/dev/null || true
child=0
cleanup() {
  [ "$child" != 0 ] && kill "$child" 2>/dev/null
  termux-wake-unlock 2>/dev/null || true
  exit 0
}
trap cleanup TERM INT HUP
while :; do
  python3 compute_push.py "$@" >> logs/compute.log 2>&1 &
  child=$!
  wait "$child"
  rc=$?
  echo "$(date '+%F %T') collector exited ($rc), restarting in 10s" >> logs/compute.log
  sleep 10
done
