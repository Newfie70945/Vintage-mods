#!/usr/bin/env bash
# Newfie's Server Suite restart loop for a directly hosted Vintage Story server.
# Edit VS_SERVER and DATA_PATH for your installation. Stop this wrapper when you
# intentionally want the server to remain offline.
set -u

VS_SERVER="/path/to/vintagestory/VintagestoryServer"
DATA_PATH="/path/to/vintagestory/data"
RESTART_DELAY_SECONDS=5

while true; do
  echo "[NSE restart loop] Starting Vintage Story server..."
  "$VS_SERVER" --dataPath "$DATA_PATH"
  exit_code=$?
  echo "[NSE restart loop] Server exited with code $exit_code. Restarting in ${RESTART_DELAY_SECONDS}s..."
  sleep "$RESTART_DELAY_SECONDS"
done
