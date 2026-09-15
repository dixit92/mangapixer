#!/bin/sh
# MangaPlex container entrypoint
# Ensures volume/bind directories are writable by the runtime user, then drops
# privileges and starts the .NET server.
#
# PUID/PGID (default 1000/1000) select the runtime UID/GID. This matches the
# linuxserver.io/Unraid convention: set PUID/PGID to the host user that owns
# the appdata bind so files on the host are owned by you, not by an arbitrary
# in-image UID. When unset, behavior is unchanged from the original 1000:1000
# image user.

set -e

PUID="${PUID:-1000}"
PGID="${PGID:-1000}"

# Fix ownership of state directories if running as root.
# Covers both the volume layout (/data, /cache, /scratch) and the Unraid
# single-bind layout (/config with data/cache/scratch subfolders).
if [ "$(id -u)" = "0" ]; then
    for dir in /data /cache /scratch /config /config/data /config/cache /config/scratch; do
        if [ -d "$dir" ]; then
            chown -R "$PUID:$PGID" "$dir" 2>/dev/null || true
        fi
    done
    # Restrict Data Protection key directory to owner-only access.
    # Linux has no DPAPI, so keys are stored unencrypted inside the private
    # data root; owner-only permissions are the at-rest protection boundary
    # (audit defect D16).
    for keys_dir in /data/keys /config/data/keys; do
        if [ -d "$keys_dir" ]; then
            chmod 700 "$keys_dir" 2>/dev/null || true
            chown "$PUID:$PGID" "$keys_dir" 2>/dev/null || true
        fi
    done
    # Ensure the runtime group and user exist with the requested IDs.
    # If PUID/PGID match the image's built-in 1000:1000, these are no-ops.
    if ! getent group "$PGID" > /dev/null 2>&1; then
        groupadd --gid "$PGID" mangaplex 2>/dev/null || true
    fi
    if ! id -u "$PUID" > /dev/null 2>&1; then
        useradd --uid "$PUID" --gid "$PGID" --shell /bin/bash --no-create-home mangaplex 2>/dev/null || true
    fi
    # Drop to non-root user using gosu
    exec gosu "$PUID:$PGID" dotnet server/MangaPixer.Server.dll
else
    # Already running as non-root (PUID/PGID ignored in this path)
    exec dotnet server/MangaPixer.Server.dll
fi
