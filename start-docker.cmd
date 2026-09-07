@echo off
REM Starts everything in Docker: Postgres and the tracker. Nothing stays attached to this window.
REM Configuration is read from the environment block in docker-compose.yml.
setlocal
cd /d "%~dp0"

docker compose up -d --build
if errorlevel 1 (
  echo Could not start. Is Docker Desktop running?
  exit /b 1
)

echo.
echo Status page:  http://127.0.0.1:5199/
echo Dashboard:    http://127.0.0.1:5199/dashboard
echo MCP endpoint: http://127.0.0.1:5199/mcp
echo.
echo Logs:  docker compose logs -f tracker
echo Stop:  docker compose down
