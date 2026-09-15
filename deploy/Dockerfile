# MangaPlex production Dockerfile
# Multi-stage build: Angular SPA + .NET server + MediaWorker
# Uses the official Microsoft .NET images (Ubuntu-based for .NET 10; no
# Alpine/musl in release one).
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
COPY global.json Directory.Build.props Directory.Packages.props Version.props MangaPixer.slnx ./
COPY src/ ./src/
COPY tests/ ./tests/
RUN dotnet restore MangaPixer.slnx --locked-mode 2>/dev/null || dotnet restore MangaPixer.slnx

# Magick.NET's Notice.txt carries the license texts for ImageMagick and the
# native libraries bundled in Magick.Native. dotnet publish does not copy it,
# so take it from the restored package (version-independent lookup) and fail
# the build if it is missing rather than ship an image without it.
RUN notice="$(find "${NUGET_PACKAGES:-$HOME/.nuget/packages}/magick.net-q8-anycpu" \
        -mindepth 2 -maxdepth 2 -iname notice.txt | sort -V | tail -n 1)" && \
    test -n "$notice" && \
    mkdir -p /build/licenses && \
    cp "$notice" /build/licenses/Magick.NET-Notice.txt

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
RUN dotnet publish src/MangaPixer.Server/MangaPixer.Server.csproj -c Release -o /build/publish/server --no-restore
RUN dotnet publish src/MangaPixer.MediaWorker/MangaPixer.MediaWorker.csproj -c Release -o /build/publish/worker --no-restore

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
COPY --from=web-build /build/web/dist/mangapixer-web/browser ./wwwroot/

# License notices shipped with the binaries (see THIRD-PARTY-NOTICES.md):
# MangaPlex's own LICENSE, the third-party inventory, the Angular build's
# bundled-package licenses, and Magick.NET's native-library notice.
COPY LICENSE THIRD-PARTY-NOTICES.md ./licenses/
COPY --from=web-build /build/web/dist/mangapixer-web/3rdpartylicenses.txt ./licenses/
COPY --from=dotnet-build /build/licenses/Magick.NET-Notice.txt ./licenses/
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
ENV MangaPixer__Storage__DataRoot=/data
ENV MangaPixer__Storage__CacheRoot=/cache
ENV MangaPixer__Storage__ScratchRoot=/scratch
# Worker discovery: container layout is /app/server + /app/worker.
# The server's ResolveWorkerLaunch checks ../worker/MangaPixer.MediaWorker.dll
# automatically, so this env var is belt-and-braces.
ENV Media__WorkerExecutablePath=/app/worker/MangaPixer.MediaWorker.dll
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

# Run as root so entrypoint can fix volume permissions, then drops to UID 1000
ENTRYPOINT ["./entrypoint.sh"]
