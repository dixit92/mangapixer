# MangaPlex production Dockerfile
# Multi-stage build: Angular SPA + .NET server + MediaWorker
# Uses official Debian-based .NET images (no Alpine/musl in release one).

# --- Stage 1: Build Angular SPA ---
FROM node:24-bookworm-slim AS web-build
WORKDIR /build/web
COPY web/package.json web/package-lock.json* ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npm run build

# --- Stage 2: Build .NET server and worker ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /build
COPY global.json Directory.Build.props Directory.Packages.props Version.props MangaPlex.slnx ./
COPY src/ ./src/
COPY tests/ ./tests/
RUN dotnet restore MangaPlex.slnx --locked-mode 2>/dev/null || dotnet restore MangaPlex.slnx
RUN dotnet publish src/MangaPlex.Server/MangaPlex.Server.csproj -c Release -o /build/publish/server --no-restore
RUN dotnet publish src/MangaPlex.MediaWorker/MangaPlex.MediaWorker.csproj -c Release -o /build/publish/worker --no-restore

# --- Stage 3: Runtime image ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install native dependencies for Magick.NET (libgdiplus, etc.)
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgdiplus \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Copy server and worker
COPY --from=dotnet-build /build/publish/server ./server/
COPY --from=dotnet-build /build/publish/worker ./worker/
COPY --from=web-build /build/web/dist/mangaplex-web/browser ./wwwroot/

# Non-root runtime user
RUN groupadd --gid 1000 mangaplex && \
    useradd --uid 1000 --gid mangaplex --shell /bin/bash --create-home mangaplex
RUN chown -R mangaplex:mangaplex /app
USER mangaplex

ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "server/com.lifepixer.mangaplex.Server.dll"]
