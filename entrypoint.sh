#!/bin/bash
# Entrypoint script — Phase 7: simplified to just run the .NET app.
#
# Previous versions started a local PostgreSQL instance inside the container.
# We now use Prisma Postgres (db.prisma.io) — a managed PostgreSQL service —
# so there's no local DB to start. The connection string is read from the
# POSTGRES_CONNECTION_STRING env var (set in Render dashboard or locally).
#
# The app's Program.cs reads:
#   1. POSTGRES_CONNECTION_STRING env var (preferred — production)
#   2. DATABASE_URL env var (Render-style postgres:// URL — auto-converted to Npgsql)
#   3. SQLite fallback (ukuuhr.db) — local dev only

set -e

if [ -z "$POSTGRES_CONNECTION_STRING" ] && [ -z "$DATABASE_URL" ]; then
    echo "[entrypoint] WARNING: POSTGRES_CONNECTION_STRING and DATABASE_URL are both unset."
    echo "[entrypoint] The app will fall back to SQLite (ukuuhr.db). This is fine for local dev"
    echo "[entrypoint] but NOT suitable for production. Set POSTGRES_CONNECTION_STRING in your"
    echo "[entrypoint] environment to use Prisma Postgres or any other managed PostgreSQL."
else
    if [ -n "$POSTGRES_CONNECTION_STRING" ]; then
        echo "[entrypoint] Using PostgreSQL from POSTGRES_CONNECTION_STRING (host: $(echo $POSTGRES_CONNECTION_STRING | grep -oP 'Host=\K[^;]+'))"
    else
        echo "[entrypoint] Using PostgreSQL from DATABASE_URL"
    fi
fi

echo "[entrypoint] Starting .NET application..."
exec dotnet UkuuHr.Web.dll
