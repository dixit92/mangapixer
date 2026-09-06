#!/bin/sh
# MangaPlex container entrypoint
# Ensures volume directories are writable by the non-root runtime user (UID 1000),
# then drops privileges and starts the .NET server.

set -e

# Fix ownership of volume mount points if running as root
if [ "$(id -u)" = "0" ]; then
    for dir in /data /cache /scratch; do
        if [ -d "$dir" ]; then
            chown -R 1000:1000 "$dir" 2>/dev/null || true
        fi
    done
    # Drop to non-root user using gosu
    exec gosu 1000:1000 dotnet server/MangaPlex.Server.dll
else
    # Already running as non-root
    exec dotnet server/MangaPlex.Server.dll
fi
