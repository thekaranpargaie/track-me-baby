# The tracker: MCP endpoint, sync worker and status page, all one process.
# Built here so `docker compose up` is the whole system and the host only needs Docker.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore before copying the rest, so a source-only change does not re-download packages.
COPY src/TrackMeBaby/TrackMeBaby.csproj src/TrackMeBaby/
RUN dotnet restore src/TrackMeBaby/TrackMeBaby.csproj

COPY src/ src/
RUN dotnet publish src/TrackMeBaby/TrackMeBaby.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# Npgsql probes for GSSAPI at startup and prints a scary "Error: libgssapi_krb5.so.2" line when
# it is absent. Nothing here uses Kerberos auth; this just keeps the log honest.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

# Not root: nothing here needs it. The image carries no configuration — the token and your
# settings arrive at runtime (see docker-compose.yml), so the image never holds a secret.
USER $APP_UID
EXPOSE 5199

ENTRYPOINT ["dotnet", "TrackMeBaby.dll"]
