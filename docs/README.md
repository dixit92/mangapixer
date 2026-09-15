# Documentation

These guides are for people who run MangaPixer on their own hardware. They
describe the current release; the version you are running is shown in the
app footer and returned by `GET /api/v1/system/info`.

## Install

| Page | What it covers |
|---|---|
| [Install with Docker](install-docker.md) | The canonical Compose setup: volumes, media mounts, ports, first-run setup, upgrades, where backups land. |
| [Install on Unraid](install-unraid.md) | The Unraid Compose file: single `/config` folder, `PUID`/`PGID`, media shares, keeping private paths in an override file. |
| [Install on Windows](install-windows.md) | Placeholder for the upcoming native Windows installer. |

## Set up and run

| Page | What it covers |
|---|---|
| [Configuration reference](configuration.md) | Every environment variable and settings key you can set, with defaults. |
| [Library layout](library-layout.md) | How your folders and archives appear in the app, supported formats, sorting, rescans and moves, and the read-only guarantee. |
| [Users and access](users-and-access.md) | First-run setup, admins and readers, activation links, library access, Private libraries, Incognito, sessions. |
| [Backup and restore](backup-and-restore.md) | Automatic database backups, restoring a backup, what a backup contains, importing YACReader progress. |
| [Reverse proxy and HTTPS](reverse-proxy-and-https.md) | Putting MangaPixer behind Caddy or nginx for TLS, and what the server does and does not know about proxies. |

## Use

| Page | What it covers |
|---|---|
| [Reader](reader.md) | Page modes, fit, reading direction, page-turn animation, keyboard/touch controls, prefetch, resume position, read state. |

## Help

| Page | What it covers |
|---|---|
| [Troubleshooting](troubleshooting.md) | Health checks, logs, scans that miss files, missing thumbnails, permission errors, port conflicts. |
| [FAQ](faq.md) | Short answers to common questions. |
