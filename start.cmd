@echo off
REM Runs the tracker on this machine against Postgres in Docker - a quicker edit-run loop than
REM rebuilding the image. Leave this window open; the sync worker runs inside it.
REM For everything in Docker instead, use start-docker.cmd.
REM
REM docker-compose.yml is the only configuration file, so this script reads the "Tracker__*" keys
REM out of it and sets them, which is exactly what compose hands the container. The container
REM plumbing keys (ConnectionStrings__, Kestrel__) are deliberately named without that prefix and
REM are skipped here: on the host, Postgres is reached on 127.0.0.1:5433 and Kestrel binds loopback.
setlocal
cd /d "%~dp0"

echo Starting Postgres...
docker compose up -d db
if errorlevel 1 (
  echo Could not start Postgres. Is Docker Desktop running?
  exit /b 1
)

REM Both cannot own port 5199, and running it here is what was asked for.
docker compose ps --status running --services 2>nul | findstr /x tracker >nul
if not errorlevel 1 (
  echo Stopping the tracker container so this one can take port 5199...
  docker compose stop tracker
)

REM Only uncommented lines match: findstr's "^ *Tracker__" anchor is defeated by a leading '#',
REM so commented settings stay commented. Space and colon are both delimiters, which trims the
REM indentation for free, and %%~B strips the surrounding quotes off the value.
set /a _tmbCount=0
for /f "usebackq tokens=1,* delims=: " %%A in (`findstr /r /c:"^ *Tracker__[A-Za-z0-9_]* *:" docker-compose.yml`) do (
  set "%%A=%%~B"
  set /a _tmbCount+=1
)

echo Loaded %_tmbCount% settings from docker-compose.yml
echo.
echo Status page:  http://127.0.0.1:5199/
echo Dashboard:    http://127.0.0.1:5199/dashboard
echo MCP endpoint: http://127.0.0.1:5199/mcp
echo.

dotnet run --project src\TrackMeBaby -c Release
