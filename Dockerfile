# ───────────── Build stage ─────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project file and restore
COPY UkuuHr.Web/UkuuHr.Web.csproj ./UkuuHr.Web/
RUN dotnet restore ./UkuuHr.Web/UkuuHr.Web.csproj

# Copy everything else and build
COPY UkuuHr.Web/ ./UkuuHr.Web/
WORKDIR /src/UkuuHr.Web
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# ───────────── Runtime stage ─────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime

# Install ICU + curl for health checks. No PostgreSQL needed — we use Prisma Postgres (db.prisma.io).
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
# and crashes the app with "The configured user limit (128) on the number of
# inotify instances has been reached". A dedicated UID gets its own budget.
# (Program.cs also disables config reload as defense-in-depth.)
# /app is made writable so the SQLite dev fallback (ukuuhr.db) still works.
RUN chown -R $APP_UID:$APP_UID /app
USER $APP_UID

# Render sets $PORT — bind to it
ENV ASPNETCORE_URLS=http://+:${PORT:-8080}
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

EXPOSE 8080

ENTRYPOINT ["dotnet", "UkuuHr.Web.dll"]
