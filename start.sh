#!/usr/bin/env sh
# Runs the tracker on this machine against Postgres in Docker — a quicker edit-run loop than
# rebuilding the image. Leave this shell open; the sync worker runs inside it.
# For everything in Docker instead, use start-docker.sh.
#
# docker-compose.yml is the only configuration file, so this script reads the "Tracker__*" keys
# out of it and exports them, which is exactly what compose hands the container. The container
# plumbing keys (ConnectionStrings__, Kestrel__) are deliberately named without that prefix and
# are skipped here: on the host, Postgres is reached on 127.0.0.1:5433 and Kestrel binds loopback.
set -e
cd "$(dirname "$0")"

if ! docker compose up -d db; then
  echo "Could not start Postgres. Is Docker running?" >&2
  exit 1
fi

# Both cannot own port 5199, and running it here is what was asked for.
if docker compose ps --status running --services 2>/dev/null | grep -qx tracker; then
  echo "Stopping the tracker container so this one can take port 5199..."
  docker compose stop tracker
fi

# Only uncommented `Key: "value"` lines match: a leading '#' defeats the anchor, so commented
# settings stay commented.
settings=$(sed -n 's/^[[:space:]][[:space:]]*\(Tracker__[A-Za-z0-9_]*\)[[:space:]]*:[[:space:]]*"\(.*\)"[[:space:]]*$/\1=\2/p' docker-compose.yml)

count=0
while IFS='=' read -r key value; do
  [ -n "$key" ] || continue
  export "$key=$value"
  count=$((count + 1))
done <<EOF
$settings
EOF

echo "Loaded $count settings from docker-compose.yml"
echo
echo "Status page:  http://127.0.0.1:5199/"
echo "Dashboard:    http://127.0.0.1:5199/dashboard"
echo "MCP endpoint: http://127.0.0.1:5199/mcp"
echo

dotnet run --project src/TrackMeBaby -c Release
