# ─────────────────────────────────────────────────────────────────────────────
# Ukuu HR — Multi-arch Docker Image (linux/amd64 + linux/arm64)
# ─────────────────────────────────────────────────────────────────────────────
# Builds and runs on:
#   • Windows 10/11  (Docker Desktop with WSL2)
#   • macOS Intel    (Docker Desktop, linux/amd64 via emulation or native)
#   • macOS Apple Silicon (Docker Desktop, linux/arm64 native)
#   • Any Linux x64 / ARM64 host
#
# Build:
#   docker build -t ukuu-hr:latest .
#
# Multi-arch build (pushes to registry):
#   docker buildx build --platform linux/amd64,linux/arm64 -t ukuu-hr:latest .
#
# Run:
#   docker run -p 8080:8080 -e POSTGRES_CONNECTION_STRING="..." ukuu-hr:latest
# ─────────────────────────────────────────────────────────────────────────────

# ───────────── Build stage ─────────────
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
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
# They are placed in wwwroot/downloads/ so users can download them from the web UI.
WORKDIR /src
COPY UkuuHr.Desktop/ ./UkuuHr.Desktop/

# Ensure the downloads directory exists
RUN mkdir -p /app/publish/wwwroot/downloads

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

# Install ICU for full Unicode/globalization support + curl for health checks.
RUN apt-get update && apt-get install -y --no-install-recommends \
    libicu-dev \
    curl \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .
COPY entrypoint.sh .
RUN chmod +x entrypoint.sh

# ── Run as a non-root user with its own UID ──
# Docker hosts cap inotify instances at 128 per USER. The .NET runtime
# registers FileSystemWatchers for config reload; when the container runs as
# root it shares root's budget with every other root container on the host,
# which exhausts the limit and crashes the app. A dedicated UID gets its own budget.
# /app is made writable so the SQLite dev fallback (ukuuhr.db) still works.
RUN chown -R $APP_UID:$APP_UID /app
USER $APP_UID

# ── Environment defaults ──
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

EXPOSE 8080

# Health check — curl /health endpoint every 30s
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["./entrypoint.sh"]
