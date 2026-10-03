# Documentation

These guides are for people who run MangaPixer on their own hardware. They describe the current release; the version you are running is shown in the app footer and returned by `GET /api/v1/system/info`.

## Install

| Page | What it covers |
|---|---|
| [Install on Unraid](install-unraid.md) | The Unraid template or Compose file: single `/config` folder, `PUID`/`PGID`, media shares, upgrades. |
| [Install on Windows](install-windows.md) | The native package since 1.13.0: the MSI installer, the tray app, ports and LAN access, where data lives, upgrades and uninstall. |
| [Install with Docker](install-docker.md) | The canonical Compose setup: volumes, media mounts, ports, first-run setup, upgrades, where backups land. |

## Set up and run

| Page | What it covers |
|---|---|
| [How MangaPixer works](how-it-works.md) | The parts that run, where your data goes, what happens when you scan and read, memory use, and privacy. |
| [Configuration reference](configuration.md) | Every environment variable and settings key you can set, with defaults, plus the settings admins change in the app. |
| [Library layout](library-layout.md) | How your folders and archives appear in the app, supported formats, sorting, filters, favorites, rescans and moves, and the read-only guarantee. |
| [Users and access](users-and-access.md) | First-run setup, admins and readers, activation links, library access, analytics, Private libraries, Incognito, sessions. |
| [Backup and restore](backup-and-restore.md) | Automatic database backups, backup settings and a custom backup folder, restoring a backup, what a backup contains, importing YACReader progress. |
| [Scheduled jobs](scheduled-jobs.md) | What MangaPixer does on its own and when - scans, the series information refresh and its cadence, backups, cleaning - with the times you can choose (server time). |
| [Trash](trash.md) | What happens to removed files: the trash, how long it keeps reading state (Daily to Yearly - also the window in which a moved series is recognized), emptying it now or automatically, and Clean bundles. |
| [API tokens](api-tokens.md) | Tokens that let another app (MangaList) read the metadata export: what a token can do, creating one (shown once), expiry, revoking, the limits, HTTPS. |
| [Reverse proxy and HTTPS](reverse-proxy-and-https.md) | Putting MangaPixer behind Caddy or nginx for TLS, the forwarded headers it trusts, and exposing it to the internet. |

## Use

| Page | What it covers |
|---|---|
| [Features](features.md) | Everything MangaPixer does, by area, and the supported archive and image formats. |
| [Reader](reader.md) | Page modes, fit, image quality (Page quality, Downscale filter, Enhance), reading direction, page-turn animation, keyboard/touch controls, prefetch, resume position, read state. |
| [Series information](series-information.md) | Series details from ComicInfo.xml and, if an admin allows it, MangaUpdates: the panel and series page, search by alternative title, Identify and automatic matching, what is sent, the Metadata Manager page for admins. |
| [Declared facts](declared-hints.md) | For admins: state the type (manga, manhwa, webtoon, comic, ...) and the creators of a folder or a whole library, how declarations are inherited, and how a disagreement with the linked series is shown. |
| [Missing volumes and chapters](missing-report.md) | How far behind each linked series is - volumes and chapters you have against the English and original totals, holes in your numbering, and the optional AniList conversion for chapter-only series. |
| [Completion](official-releases.md) | Has each linked series ended, and do you have all of it - one answer per series (finished and held whole, finished but missing some, everything released so far, missing some, can't tell), with the edition and the upgrades; from stored data, nothing is fetched. |
| [Metadata export API](metadata-export.md) | For other programs on your network (MangaList, scripts): a read-only API that returns each linked series' information - names only, never a path - with incremental sync, removals and paging. |
| [Covers](covers.md) | How cards get their cover - the file's page 1, the front half of a jacket spread, or a saved cover from the web for linked series - and how admins choose a cover with **Choose cover...**. |
| [Volumes view](volumes.md) | How a series' chapters group into volume stacks ordered by volume, what the "8/9" mark and the missing-chapter cards mean, and the Volumes / Folders switch. |

## Help

| Page | What it covers |
|---|---|
| [Troubleshooting](troubleshooting.md) | Health checks, logs, sign-in problems, scans that miss files, archives that won't open, missing thumbnails, backups that stopped, permission errors, port conflicts, restore problems. |
| [FAQ](faq.md) | Short answers to common questions. |
| [Privacy and security](privacy-and-security.md) | No telemetry, what leaves your server when an admin turns on an internet feature, how sessions and logs are protected, reporting a vulnerability. |
| [MangaPixer compared](comparison.md) | How MangaPixer differs from Komga, Kavita and YACReader, and when another server is the better fit. |
