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

# Render sets $PORT — bind to it
ENV ASPNETCORE_URLS=http://+:${PORT:-8080}
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

EXPOSE 8080

ENTRYPOINT ["dotnet", "UkuuHr.Web.dll"]
