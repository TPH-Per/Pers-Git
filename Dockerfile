# ==============================================================================
# Per's Git (Gitea.Net) — Production Multi-Stage Dockerfile
# Stage 1: Build & Publish (.NET 8.0 SDK on Alpine)
# Stage 2: Minimal Runtime (aspnet:8.0-alpine + Git >= 2.38 + non-root UID 10001)
# ==============================================================================
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

COPY ["Gitea.Net.sln", "./"]
COPY ["src/Gitea.Net/Gitea.Net.csproj", "src/Gitea.Net/"]
COPY ["tests/Gitea.Net.Tests/Gitea.Net.Tests.csproj", "tests/Gitea.Net.Tests/"]
RUN dotnet restore "src/Gitea.Net/Gitea.Net.csproj"

COPY . .
WORKDIR "/src/src/Gitea.Net"
RUN dotnet publish "Gitea.Net.csproj" -c Release -o /app/publish /p:UseAppHost=false

# ==============================================================================
# Stage 2: Hardened Production Runtime
# ==============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime
WORKDIR /app

# Install git (>= 2.38 required for git merge-tree --write-tree), sqlite, and curl for healthcheck
RUN apk add --no-cache git sqlite sqlite-libs curl bash tzdata icu-libs ca-certificates \
    && adduser -D -u 10001 -g "" appuser \
    && mkdir -p /app/App_Data/repositories /app/App_Data/temp_worktrees /backups \
    && chown -R appuser:appuser /app /backups

COPY --from=build --chown=appuser:appuser /app/publish .
COPY --chown=appuser:appuser scripts/sqlite_hot_backup.sh /app/scripts/sqlite_hot_backup.sh
RUN chmod +x /app/scripts/sqlite_hot_backup.sh

USER 10001
ENV ASPNETCORE_HTTP_PORTS=5000 \
    ASPNETCORE_URLS="http://+:5000" \
    ASPNETCORE_ENVIRONMENT="Production" \
    Database__Provider="Sqlite" \
    Database__SqliteConnectionString="Data Source=/app/App_Data/gitea_net.db;Mode=ReadWriteCreate;" \
    Git__FallbackBinaryPath="/usr/bin/git" \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

EXPOSE 5000

HEALTHCHECK --interval=15s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -fsS http://127.0.0.1:5000/api/v1/health || exit 1

ENTRYPOINT ["dotnet", "Gitea.Net.dll"]
