#!/data/data/com.termux/files/usr/bin/bash
# Start the compute collector in the background (log: logs/compute.log).
set -u
cd "$(dirname "$0")" || exit 1
mkdir -p logs
if [ -f .pid ] && kill -0 "$(cat .pid)" 2>/dev/null; then
  echo "already running: $(cat .pid)"
  exit 0
fi
nohup ./run.sh "$@" >/dev/null 2>&1 &
echo $! > .pid
sleep 1
if kill -0 "$(cat .pid)" 2>/dev/null; then
  echo "started: $(cat .pid)"
  echo "log:     $PWD/logs/compute.log"
else
  rm -f .pid
  echo "failed to start; see $PWD/logs/compute.log" >&2
  exit 1
fi
