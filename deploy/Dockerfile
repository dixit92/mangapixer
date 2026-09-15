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

# Git build metadata for InformationalVersion (consumed by Directory.Build.props).
# Passed by the deploy script via --build-arg; empty by default so a plain
# `docker build` still works and falls back to the bare "+dirty" marker. MSBuild
# reads these environment variables as the $(GitCommitShort)/$(GitIsDirty)
# properties. Declared after restore so a new commit re-runs only the publish
# layers, not the (slow) restore.
ARG GIT_COMMIT_SHORT=""
ARG GIT_IS_DIRTY="false"
ENV GitCommitShort=${GIT_COMMIT_SHORT}
ENV GitIsDirty=${GIT_IS_DIRTY}
RUN dotnet publish src/MangaPlex.Server/MangaPlex.Server.csproj -c Release -o /build/publish/server --no-restore
RUN dotnet publish src/MangaPlex.MediaWorker/MangaPlex.MediaWorker.csproj -c Release -o /build/publish/worker --no-restore

# --- Stage 3: Runtime image ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install native dependencies:
# - libgdiplus: native backing for Magick.NET image probing/transcoding.
#   Kept until process-level tests confirm the worker runs without it; remove
#   only after that evidence exists.
# - ca-certificates for HTTPS, gosu for privilege dropping in entrypoint.
# p7zip-full was removed: SharpCompress is managed and the worker never shells
# out to 7z.
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgdiplus \
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
# Point the storage roots at the mounted volumes. Without these the roots resolve
# to AppContext.BaseDirectory/{data,cache,scratch} inside the image's writable
# layer, so the /data, /cache, /scratch volume mounts are unused and all state
# (database, keys, cache) is lost whenever the container is recreated.
# .NET maps hierarchical config keys via '__' in env vars.
ENV MangaPlex__Storage__DataRoot=/data
ENV MangaPlex__Storage__CacheRoot=/cache
ENV MangaPlex__Storage__ScratchRoot=/scratch
# Worker discovery: container layout is /app/server + /app/worker.
# The server's ResolveWorkerLaunch checks ../worker/MangaPlex.MediaWorker.dll
# automatically, so this env var is belt-and-braces.
ENV Media__WorkerExecutablePath=/app/worker/MangaPlex.MediaWorker.dll
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

# Run as root so entrypoint can fix volume permissions, then drops to UID 1000
ENTRYPOINT ["./entrypoint.sh"]
