#!/bin/bash
# ─────────────────────────────────────────────────────────────────────────────
# Ukuu HR — Docker Entrypoint
# ─────────────────────────────────────────────────────────────────────────────
# Works on all platforms: Windows (Docker Desktop/WSL2), macOS (Intel & ARM),
# and Linux.
#
# The app's Program.cs reads:
#   1. POSTGRES_CONNECTION_STRING env var (preferred — production)
#   2. DATABASE_URL env var (Render-style postgres:// URL — auto-converted to Npgsql)
#   3. SQLite fallback (ukuuhr.db) — local dev only
# ─────────────────────────────────────────────────────────────────────────────

set -e

echo ""
echo "╔══════════════════════════════════════════════════════════╗"
echo "║             Ukuu HR — Docker Container                  ║"
echo "║     Biometric Attendance & HR Management System         ║"
echo "╚══════════════════════════════════════════════════════════╝"
echo ""

# ── Print platform info ──
echo "[entrypoint] Platform: $(uname -s) $(uname -m)"
echo "[entrypoint] .NET version: $(dotnet --version 2>/dev/null || echo 'N/A')"
echo "[entrypoint] ASPNETCORE_ENVIRONMENT: ${ASPNETCORE_ENVIRONMENT:-Production}"

# ── Handle PORT env var (Render/Heroku style) ──
# Render and some cloud platforms set $PORT instead of ASPNETCORE_URLS.
if [ -n "$PORT" ]; then
    export ASPNETCORE_URLS="http://+:${PORT}"
    echo "[entrypoint] PORT=${PORT} → ASPNETCORE_URLS=${ASPNETCORE_URLS}"
fi

# ── Database connection check ──
if [ -z "$POSTGRES_CONNECTION_STRING" ] && [ -z "$DATABASE_URL" ]; then
    echo "[entrypoint] ⚠  WARNING: POSTGRES_CONNECTION_STRING and DATABASE_URL are both unset."
    echo "[entrypoint]    The app will fall back to SQLite (ukuuhr.db). This is fine for local dev"
    echo "[entrypoint]    but NOT suitable for production. Set POSTGRES_CONNECTION_STRING in your"
    echo "[entrypoint]    environment to use a managed PostgreSQL database."
else
    if [ -n "$POSTGRES_CONNECTION_STRING" ]; then
        # Extract host from connection string for logging
        DB_HOST=$(echo "$POSTGRES_CONNECTION_STRING" | sed -n 's/.*Host=\([^;]*\).*/\1/p' 2>/dev/null || echo "unknown")
        echo "[entrypoint] ✓ Using PostgreSQL from POSTGRES_CONNECTION_STRING (host: ${DB_HOST})"
    else
        echo "[entrypoint] ✓ Using PostgreSQL from DATABASE_URL"
    fi
fi

# ── Demo data seeding ──
if [ "${SEED_DEMO_DATA}" = "true" ]; then
    echo "[entrypoint] ✓ Demo data seeding ENABLED (first-run only)"
else
    echo "[entrypoint]   Demo data seeding disabled (SEED_DEMO_DATA=${SEED_DEMO_DATA:-false})"
fi

echo ""
echo "[entrypoint] Starting .NET application on ${ASPNETCORE_URLS:-http://+:8080} ..."
echo ""

exec dotnet UkuuHr.Web.dll
