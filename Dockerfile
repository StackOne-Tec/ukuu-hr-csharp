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

# Install PostgreSQL 16 + ICU + curl
RUN apt-get update && apt-get install -y --no-install-recommends \
    libicu-dev \
    curl \
    ca-certificates \
    gnupg \
    && install -d /usr/share/postgresql-common/pgdg \
    && curl -o /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc --fail https://www.postgresql.org/media/keys/ACCC4CF8.asc \
    && sh -c 'echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt $(cat /etc/os-release | grep VERSION_CODENAME | cut -d= -f2)-pgdg main" > /etc/apt/sources.list.d/pgdg.list' \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        postgresql-16 \
        postgresql-client-16 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .
COPY entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

# Render sets $PORT — bind to it
ENV ASPNETCORE_URLS=http://+:${PORT:-8080}
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV PATH=$PATH:/usr/lib/postgresql/16/bin

EXPOSE 8080

ENTRYPOINT ["/app/entrypoint.sh"]
