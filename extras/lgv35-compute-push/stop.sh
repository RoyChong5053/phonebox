#!/data/data/com.termux/files/usr/bin/bash
# Stop the compute collector.
set -u
cd "$(dirname "$0")" || exit 1
if [ -f .pid ] && kill -0 "$(cat .pid)" 2>/dev/null; then
  kill "$(cat .pid)" 2>/dev/null
  rm -f .pid
  echo "stopped"
else
  rm -f .pid
  echo "not running"
fi
termux-wake-unlock 2>/dev/null || true
