#!/usr/bin/env sh
# Starts everything in Docker: Postgres and the tracker. Nothing stays attached to this shell.
# Configuration is read from the environment block in docker-compose.yml.
set -e
cd "$(dirname "$0")"

if ! docker compose up -d --build; then
  echo "Could not start. Is Docker running?" >&2
  exit 1
fi

echo
echo "Status page:  http://127.0.0.1:5199/"
echo "Dashboard:    http://127.0.0.1:5199/dashboard"
echo "MCP endpoint: http://127.0.0.1:5199/mcp"
echo
echo "Logs:  docker compose logs -f tracker"
echo "Stop:  docker compose down"
