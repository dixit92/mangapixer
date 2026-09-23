# Documentation

These guides are for people who run MangaPixer on their own hardware. They describe the current release; the version you are running is shown in the app footer and returned by `GET /api/v1/system/info`.

## Install

| Page | What it covers |
|---|---|
| [Install with Docker](install-docker.md) | The canonical Compose setup: volumes, media mounts, ports, first-run setup, upgrades, where backups land. |
| [Install on Unraid](install-unraid.md) | The Unraid template or Compose file: single `/config` folder, `PUID`/`PGID`, media shares, upgrades. |
| [Install on Windows](install-windows.md) | The native package since 1.13.0: the MSI installer, the tray app, ports and LAN access, where data lives, upgrades and uninstall. |

## Set up and run

| Page | What it covers |
|---|---|
| [How MangaPixer works](how-it-works.md) | The parts that run, where your data goes, what happens when you scan and read, memory use, and privacy. |
| [Configuration reference](configuration.md) | Every environment variable and settings key you can set, with defaults, plus the settings admins change in the app. |
| [Library layout](library-layout.md) | How your folders and archives appear in the app, supported formats, sorting, filters, favorites, rescans and moves, and the read-only guarantee. |
| [Users and access](users-and-access.md) | First-run setup, admins and readers, activation links, library access, analytics, Private libraries, Incognito, sessions. |
| [Backup and restore](backup-and-restore.md) | Automatic database backups, backup settings and a custom backup folder, restoring a backup, what a backup contains, importing YACReader progress. |
| [Reverse proxy and HTTPS](reverse-proxy-and-https.md) | Putting MangaPixer behind Caddy or nginx for TLS, the forwarded headers it trusts, and exposing it to the internet. |

## Use

| Page | What it covers |
|---|---|
| [Reader](reader.md) | Page modes, fit, image quality (Page quality, Downscale filter, Enhance), reading direction, page-turn animation, keyboard/touch controls, prefetch, resume position, read state. |

## Help

| Page | What it covers |
|---|---|
| [Troubleshooting](troubleshooting.md) | Health checks, logs, scans that miss files, missing thumbnails, backups that stopped, permission errors, port conflicts. |
| [FAQ](faq.md) | Short answers to common questions. |
