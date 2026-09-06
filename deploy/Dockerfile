# MangaPlex production Dockerfile
# Multi-stage build: Angular SPA + .NET server + MediaWorker
# Uses official Debian-based .NET images (no Alpine/musl in release one).
# Produces a non-root, read-only-media container with bounded logging.

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

# Install native dependencies: libgdiplus for Magick.NET, p7zip for archive extraction,
# ca-certificates for HTTPS, gosu for privilege dropping in entrypoint
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgdiplus \
    p7zip-full \
    ca-certificates \
    gosu \
    curl \
    && rm -rf /var/lib/apt/lists/*

# Copy server and worker
COPY --from=dotnet-build /build/publish/server ./server/
COPY --from=dotnet-build /build/publish/worker ./worker/
COPY --from=web-build /build/web/dist/mangaplex-web/browser ./wwwroot/
COPY deploy/entrypoint.sh ./entrypoint.sh
RUN chmod +x entrypoint.sh

# Non-root runtime user (handle case where GID 1000 already exists in base image)
RUN (getent group 1000 > /dev/null 2>&1 || groupadd --gid 1000 mangaplex) && \
    (id -u 1000 > /dev/null 2>&1 || useradd --uid 1000 --gid 1000 --shell /bin/bash --create-home mangaplex)
RUN chown -R 1000:1000 /app

ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV DOTNET_CLI_TELEMETRY_OPTOUT=true
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

# Run as root so entrypoint can fix volume permissions, then drops to UID 1000
ENTRYPOINT ["./entrypoint.sh"]
