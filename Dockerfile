# ───────────── Build stage ─────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# ── Restore web project ──
COPY UkuuHr.Web/UkuuHr.Web.csproj ./UkuuHr.Web/
RUN dotnet restore ./UkuuHr.Web/UkuuHr.Web.csproj

# ── Build & publish web project ──
COPY UkuuHr.Web/ ./UkuuHr.Web/
WORKDIR /src/UkuuHr.Web
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# ── Build desktop apps (cross-compile from Linux) ──
# These are self-contained single-file executables — no .NET runtime needed on the target machine.
WORKDIR /src
COPY UkuuHr.Desktop/ ./UkuuHr.Desktop/

# Windows x64
RUN dotnet publish ./UkuuHr.Desktop/UkuuHr.Desktop.csproj \
    -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
    -o /desktop/win-x64 && \
    cp /desktop/win-x64/UkuuHrSync.exe /app/publish/wwwroot/downloads/UkuuHr-Windows-x64.exe

# macOS Apple Silicon (arm64)
RUN dotnet publish ./UkuuHr.Desktop/UkuuHr.Desktop.csproj \
    -c Release -r osx-arm64 --self-contained true \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
    -o /desktop/osx-arm64 && \
    cp /desktop/osx-arm64/UkuuHrSync /app/publish/wwwroot/downloads/UkuuHr-macOS-arm64

# ───────────── Runtime stage ─────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Install ICU + curl for health checks.
RUN apt-get update && apt-get install -y --no-install-recommends \
    libicu-dev \
    curl \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# ── Run as a non-root user with its own UID ──
# Render's shared hosts cap inotify instances at 128 per USER. The .NET runtime
# registers FileSystemWatchers (1 inotify instance each) for config reload inside
# WebApplication.CreateBuilder; when the container runs as root it shares root's
# budget with every other root container on the host, which exhausts the limit
# and crashes the app. A dedicated UID gets its own budget.
# /app is made writable so the SQLite dev fallback (ukuuhr.db) still works.
RUN chown -R $APP_UID:$APP_UID /app
USER $APP_UID

# Render sets $PORT — bind to it
ENV ASPNETCORE_URLS=http://+:${PORT:-8080}
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

EXPOSE 8080

ENTRYPOINT ["dotnet", "UkuuHr.Web.dll"]
