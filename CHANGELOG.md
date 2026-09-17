# Changelog

All notable changes to MangaPixer are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Dates are the release tag dates. Before 1.0.0 the project used an internal `0.1.0-dev.N` pre-release series that is not listed here.

## [Unreleased]

### Changed

- Release assets now have descriptive, platform-specific names (for example `mangapixer-<version>-docker-image-linux-amd64.tar` and `MangaPixer-<version>-windows-x64.msi`).
- `/health/ready` now performs a real readiness check (the database is reachable) that is distinct from the cheap `/health` liveness check.

### Fixed

- Folders and archives now sort in natural order everywhere the catalog "Name" sort is used — browse listings, folder covers, next/previous chapter, the Continue row and the A-Z jump rail — so "Chapter 2" sorts before "Chapter 10". Existing libraries are corrected automatically the first time the upgraded server starts; no rescan is needed.
- Changing the sort, view, card size or page size no longer resets the home page's recent-time-window setting to its default.
- The library scan no longer mistakes macOS `._` sidecar files for archives or descends into `__MACOSX` folders.
- Archives compressed with 7-Zip solid mode (`.cb7`/`.7z`) are now flagged as unsupported at scan time with a clear message, instead of appearing ready and then failing to load every page.
- The reader's `M` (menu) and `F` (fullscreen) keyboard shortcuts now work when Shift or Caps Lock is active, matching the keys shown in the help overlay.
- Restoring a database backup larger than 128 MiB no longer fails before the application's own (larger) size limit applies.
- Superseded database files are removed after a successful restore instead of accumulating with each restore.
- The data-protection key directory is created with restrictive permissions on first start rather than only on the second start.
- Removed unused logging configuration keys that had no effect on log output.
- The tray "Set Port" dialog scales correctly on high-DPI/scaled displays instead of clipping its text and buttons.

## [1.14.1] - 2026-09-16

### Changed

- The Compose files pull the published image from `ghcr.io/dixit92/mangapixer` instead of building from source; the build section moved to the `deploy/compose.build.yaml` overlay for contributors. The install guides and the README quick start follow.

### Fixed

- The published container image reported its version as `+dirty`; the release build now embeds the commit (`<version>+sha.<short>`).
- The container smoke test that gates releases failed on Linux runners because its marker file starts with a dot; it also waits longer for cold starts.
- The Contracts verification tier had never passed its version-consistency stage.

## [1.14.0] - 2026-09-16

### Added

- Public repository: README, MIT license and third-party notices, contributor guide and security policy.
- User and operator documentation under `docs/`: install guides for Docker, Unraid and Windows, the configuration reference, library layout, the reader, users and access, backup and restore, reverse proxy and HTTPS, troubleshooting and an FAQ.
- Continuous integration that runs the full verification script on every push and pull request, plus a release workflow that publishes the container image to the GitHub Container Registry with an SBOM and SHA-256 checksums.
- Dependabot configuration and issue and pull request templates.
- The container image and the Windows distribution ship the license texts of their third-party components (`/app/licenses/` in the image, next to the tray executable on Windows).

### Changed

- The product is renamed from its working title, MangaPlex, to MangaPixer. All identifiers changed with it: .NET namespaces and assembly names, the Angular package, the database file name, the configuration section and environment variable prefix, the sign-in and CSRF cookie and header names, container image, service and volume names, the Windows data folder, registry keys, tray and installer identity (including a new MSI upgrade code). Existing 1.13.x installs are not migrated automatically: the database schema is unchanged, so an install carries over once the database file, the environment-variable prefix and the Windows data folder are renamed by hand; everyone signs in again. Earlier entries below keep the working title as the historical record.

### Fixed

- Backup verification and restore validation no longer keep a handle on the database file after they finish, which could make the first start after a restore fail on Windows and on Docker Desktop bind mounts.

## [1.13.0] - 2026-09-15

### Added

- Native Windows deployment: a self-contained win-x64 distribution (server with the web app, worker, and a loopback-only default configuration), with data stored under the user's local application data folder.
- A Windows tray launcher that owns the server lifecycle: start, stop and restart, health status, open in browser, an opt-in LAN access toggle, a Set Port dialog with availability checks, start at sign-in, and automatic port selection around Windows excluded port ranges (default 27272).
- A per-user MSI installer (no elevation) with an install-folder picker, launch after install, Start menu entry, upgrades that keep the autostart choice, and an uninstall that preserves user data.
- The system information endpoint reports the server platform, and the admin Libraries screen uses Windows path wording when the server runs on Windows.

### Fixed

- Tray LAN access: the bind address is passed on the server command line so the toggle takes effect over the bundled configuration.
- Re-confirming an unchanged port in the tray no longer reports a busy port.

## [1.12.0] - 2026-09-14

### Added

- "Recently updated" browse sort: a folder ranks by its newest archive anywhere inside it, and folders and archives are interleaved newest first.
- The home "New chapters" row now shows one card per top-level folder ("latest + N new") instead of one card per chapter.
- Settings > New Chapters: choose which libraries contribute to the home row and how many days count as "recently added" (previously fixed at 30 days).
- Home toolbar with a card-size slider, shared with the library card size.
- Double-page reading shows both page numbers (for example "12-13").
- Search results mark each hit as a folder or an archive.

### Changed

- Opening a folder from a home card shows it sorted by "Recently updated" for that visit only. Your saved sort is unchanged.
- Choosing the Name sort defaults to ascending order.

## [1.11.0] - 2026-09-14

### Added

- The read-state filter now applies to series folders, based on the state of the archives inside them.
- Option to hide folders that contain no archives.
- After jumping to a letter, scrolling up loads the earlier items.
- Webtoon mode supports tap zones and swipe with a configurable step. Free scrolling stays the default.
- Home "New chapters" row, grouped by library.

### Changed

- Double-page mode shows a single page in narrow portrait windows. Wide spreads are unaffected.
- The "Reveal" page-turn wipes over the previous page instead of a blank frame.

## [1.10.4] - 2026-09-14

### Changed

- "Recently added" and "Recently read" sorts are always newest first. The ascending/descending toggle is shown only for the Name sort.

## [1.10.3] - 2026-09-14

### Changed

- The webtoon page-width slider goes down to 15% (was 30%) for wide screens.
- The library sidebar starts collapsed unless you have expanded it before.

## [1.10.2] - 2026-09-14

### Changed

- On phones, the card-size slider moved into the View menu, which frees space in the top bar.
- All phone layouts now switch at a single 600 px breakpoint. Before, they switched at slightly different widths.

## [1.10.1] - 2026-09-13

### Fixed

- Phone layout polish: the library menu button toggles open and closed, long folder names in the breadcrumb are clamped to two lines, the card-size icons are clearer, and the A-Z jump rail collapses into a letter picker.

## [1.10.0] - 2026-09-13

### Added

- Read-state filter (Reading / Read / Unread) at any folder level.
- A "Performance" card in Settings for the items-per-load option.

### Changed

- Active menu options are highlighted with color throughout the app instead of a checkmark.
- On phones, reader controls live in a bottom sheet, the library list is its own page, and the breadcrumb has a compact design. Desktop and tablet layouts are unchanged.

## [1.9.1] - 2026-09-12

### Changed

- The "Reveal" page-turn is now a direction-aware wipe, clearly different from "Slide" and "None".

### Fixed

- The system "reduce motion" setting now actually disables page-turn animation.

## [1.9.0] - 2026-09-12

### Added

- Page-turn animation setting: Slide, Reveal, or None. It respects the system reduce-motion preference.
- "Always open read chapters from the start" preference (off by default).
- Folders show a "Continue" or "Start" label for their next chapter.
- The reader help overlay opens automatically the first time.

### Changed

- Marking a chapter unread fully resets it, the same way marking a folder unread does.
- A chapter you mark read by hand is treated as fully read. Where it reopens now depends on your last page position, not only on its read status.

## [1.8.1] - 2026-09-11

### Fixed

- The "#" (numbers and symbols) group appears first in the jump rail, matching name-sort order.
- Folder cover thumbnails show in search results.
- The View menu highlights the selected option with color.
- Spacing around the admin "Scan all libraries" button.

## [1.8.0] - 2026-09-11

### Added

- Infinite scroll in library browse, with a sticky A-Z jump rail that follows your position.
- Tapping the top bar scrolls back to the top, and a setting controls how many items load at a time.
- Admin "Scan all libraries" button and endpoint.
- Reader: full-surface swipe zones, a reworked page scrubber, and a help overlay.

## [1.7.3] - 2026-09-11

### Fixed

- Read and in-progress marks, and the folder "Continue" row, refresh as soon as you finish a chapter. You no longer have to leave and come back.
- Moving a file onto a title that already has progress, or importing progress while reading, no longer fails with a server error.
- Removed misleading error log lines for concurrent progress updates that were already recovered.

## [1.7.2] - 2026-09-11

### Fixed

- The active highlight and item counts no longer spill past the edge of the library sidebar.

## [1.7.1] - 2026-09-11

### Changed

- Back in the reader always returns to the folder. Moving between chapters no longer stacks up browser history.
- Refreshed application and install icons.

### Removed

- Webtoon automatic next/previous chapter. It conflicted with touch gestures, and the explicit chapter buttons remain.

### Fixed

- Read status in the folder view is up to date after leaving the reader.
- Folders in search results show a cover.

## [1.7.0] - 2026-09-11

### Added

- Swipe gestures in the reader, following the reading direction.
- Page scrubber and next/previous chapter buttons in the reader bar.
- Range selection in browse: Shift-click and Ctrl/Cmd-click on desktop, long-press "Select to here" on touch, and Select all / all unread / all read.
- A pinned "Continue" row at the top of a folder that shows the next unread chapter. The list keeps its sort order.
- Administrators can restore the database from a backup. The file is validated, a snapshot is taken first, and the swap is atomic with automatic rollback. Media is never touched.

## [1.6.2] - 2026-09-11

### Fixed

- Returning from the reader restores the folder's scroll position without a blank screen.

## [1.6.1] - 2026-09-11

### Fixed

- Leaving the reader returns to the folder you came from, not Home.
- Reaching the end in double-page or webtoon mode marks the chapter read.
- Marking a folder unread also resets chapters that are in progress.
- A folder's cover is the first page of its first archive in name order.
- Consistent icons across the app, and a logo next to the home page title.

## [1.6.0] - 2026-09-11

### Added

- Card view with a card-size slider in library browse.
- Folder badges that show whether a folder is read or in progress.
- Per-category control of debug logging, so verbose areas can be turned up selectively.
- New application icons and branding.

### Fixed

- Choosing webtoon mode for one title no longer forces it on every other title.
- Marking a chapter unread resets its progress.
- Long library item counts no longer overflow the sidebar.

## [1.5.0] - 2026-09-10

### Added

- Ascending and descending order for every browse sort.
- New users join through a one-time activation link.
- A persistent, collapsible library sidebar, a current-folder breadcrumb, and reading-direction badges.

### Changed

- Faster page and thumbnail processing through concurrent worker dispatch.
- Faster scanning. Moved or renamed files are recognized by content and keep their reading state.
- Denser desktop layout.

### Fixed

- Intermittent server errors when the same reading progress was saved twice at once.

## [1.4.1] - 2026-09-10

### Changed

- Container images report the source commit in their version string (for example `1.4.1+sha.<commit>`). No change to the application itself.

## [1.4.0] - 2026-09-10

### Added

- Private libraries and an Incognito mode that hides them from your own listings, search, and continue reading.
- A new home page with a library sidebar and continue reading grouped by library.
- Read-ahead page prefetch in webtoon mode.
- The app version appears in the footer and at `GET /api/v1/system/info`.
- The A-Z jump navigation handles titles in multiple languages and scripts.

### Fixed

- Folders that contain only subfolders show a cover.
- The root breadcrumb is clickable.

## [1.3.0] - 2026-09-09

### Added

- Rename libraries. Deleting a library removes only its metadata and never the media files.
- Browse sort by Name, Recently added, or Recently read.
- Default reader page mode per device, which follows screen orientation.
- Client-side page prefetch in paged and spread modes.
- Cover thumbnails in search results.

### Changed

- Thumbnails are generated continuously in the background, replacing the one-time startup batch.

## [1.2.0] - 2026-09-09

### Added

- Reading direction per library and per folder.
- Read marks that persist, with bulk and selection actions.
- Continue-reading management: dismiss entries and hide finished ones.
- Grid, List, and Poster view modes.
- Automatic move to the previous chapter, and a progress rail you can tap to jump.
- Rotating database backups and import of reading progress from YACReader.

### Changed

- Thumbnails persist across restarts.
- Scanning is safe to run while people are reading, and interrupted analysis resumes at startup.

## [1.1.0] - 2026-09-08

### Added

- Reader: fit-to-screen, double-page mode with an offset option and wide-page handling, immersive fullscreen with a help overlay, webtoon placeholders while pages load, and auto-advance to the next chapter.
- Administrators can change the log level at runtime.

### Changed

- The database schema is managed with EF Core migrations, so upgrades apply schema changes automatically.

## [1.0.0] - 2026-09-07

First stable release.

### Added

- Folder-native libraries: point MangaPlex at existing folders and it presents them as they are, without importing, copying, or modifying anything.
- Reads CBZ/ZIP and non-solid RAR and 7z archives in an isolated worker process, including animated images. Solid RAR/7z archives are reported as unsupported.
- Angular web reader with paged, two-page spread, and webtoon modes, plus reliable resume.
- Browse, search, and continue reading across libraries.
- Multiple users with per-library access. There are no default credentials: the first administrator is created on first run, and accounts created by an admin must change their temporary password on first login.
- Reading state that survives moved files, with safe relinking of moved titles.
- Image variants and thumbnails in a bounded cache, plus backups and diagnostics.
- A Linux container image that mounts media read-only, and a Windows standalone build.
