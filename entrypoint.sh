#!/bin/bash
# Entrypoint script — starts PostgreSQL then the .NET app
# PostgreSQL runs locally inside the container, giving us a real Postgres instance on Render
# without needing an external database link (which Render's API doesn't expose).

set -e

# ───── PostgreSQL data directory ─────
PG_DATA="/var/lib/postgresql/data"
PG_RUN="/var/run/postgresql"
PG_USER="postgres"
PG_DB="ukuuhr"

# Wipe data dir on each container start (free-tier has no persistent disk anyway)
mkdir -p "$PG_DATA" "$PG_RUN"
chown -R postgres:postgres "$PG_DATA" "$PG_RUN"
chmod 0777 "$PG_RUN"

echo "[entrypoint] Initializing PostgreSQL database cluster..."
su postgres -c "initdb -D $PG_DATA -U $PG_USER --auth-local=trust --auth-host=trust --encoding=UTF8" >/dev/null 2>&1 || true

# Configure PostgreSQL for local connections
cat > "$PG_DATA/postgresql.conf" <<EOF
listen_addresses = 'localhost'
port = 5432
max_connections = 100
shared_buffers = 32MB
effective_cache_size = 128MB
log_min_messages = warning
EOF

cat > "$PG_DATA/pg_hba.conf" <<EOF
local   all   all               trust
host    all   all   127.0.0.1/32 trust
host    all   all   ::1/128      trust
EOF

chown postgres:postgres "$PG_DATA/postgresql.conf" "$PG_DATA/pg_hba.conf"

echo "[entrypoint] Starting PostgreSQL..."
su postgres -c "pg_ctl -D $PG_DATA -l $PG_DATA/postgres.log start -w -t 30"

# Wait for PostgreSQL to be ready
echo "[entrypoint] Waiting for PostgreSQL to accept connections..."
for i in $(seq 1 30); do
    if su postgres -c "psql -tAc 'SELECT 1' $PG_DB" >/dev/null 2>&1; then
        echo "[entrypoint] PostgreSQL is ready!"
        break
    fi
    # Try creating the database (it might not exist yet)
    su postgres -c "psql -tAc 'SELECT 1 FROM pg_database WHERE datname = '\''$PG_DB'\'''" | grep -q 1 || \
        su postgres -c "createdb $PG_DB" 2>/dev/null || true
    sleep 1
done

# Verify connection
su postgres -c "psql -tAc 'SELECT version();' $PG_DB" | head -1

# Export DATABASE_URL for the .NET app (Npgsql format)
export DATABASE_URL="postgresql://$PG_USER@localhost:5432/$PG_DB"
export POSTGRES_CONNECTION_STRING="Host=localhost;Port=5432;Database=$PG_DB;Username=$PG_USER;Password=;TrustServerCertificate=true;SSL Mode=Prefer;Timeout=15;CommandTimeout=60"

echo "[entrypoint] DATABASE_URL=$DATABASE_URL"
echo "[entrypoint] Starting .NET application..."

# Hand off to the .NET app
exec dotnet UkuuHr.Web.dll
