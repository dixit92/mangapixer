# Changelog

All notable changes to MangaPixer are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Dates are the release tag dates. Before 1.0.0 the project used an internal `0.1.0-dev.N` pre-release series that is not listed here.

## [Unreleased]

### Added

- **Enhance in vertical (webtoon) mode.** Rendering: **Enhance** now also sharpens small-source webtoon strips on your device's graphics chip (WebGPU). Each page is enhanced in horizontal bands as you reach it: the plain page shows while you scroll quickly and the enhanced one fades in a moment after you stop. Changing the page width, rotating or zooming never re-renders anything, graphics memory stays bounded however long the chapter is, and only pages shown more than 1.2 times larger than their original width are enhanced. `e` now switches Rendering in vertical mode too. If the graphics chip resets twice within a minute, vertical-mode Enhance pauses and the page shows normally. See [Rendering](docs/reader.md#image-quality).
- **Enhance quality: Balanced or Max quality.** A new choice under Rendering. **Balanced** (the new default) runs a lighter Anime4K network that uses much less graphics memory and battery; **Max quality** keeps the heavier network Enhance used until now, for single and double page. Vertical mode always uses Balanced.

### Changed

- Rendering: Enhance in single and double page now uses **Balanced** by default; choose **Max quality** for the previous look.

### Fixed

- **Reader:** pressing `Esc` to close the Reading mode, Image fit or settings menu (or the options sheet on a phone) no longer also leaves the reader or exits full screen; press `Esc` again to leave. Arrow keys and shortcut letters used inside an open menu no longer turn the page or change settings behind it.

## [1.23.0] - 2026-09-24

### Added

- **Double page: fix the pairing anywhere in an archive, for everyone.** When extra pages (credits, colour pages) leave a two-page spread split across the wrong pair, pick the other double-page mode, press the new `o` key, or press `d`, step to the next page and press `d` again. The pairing shifts from the spread on screen onward and is saved on the server for that archive, so every user who can read it sees the same pairing; a changed file starts over. The mode "Double page (offset cover)" is now **Double page (shifted)**, and the highlighted mode follows the spread on screen. Archives nobody has adjusted keep using this browser's cover-alone setting. See [Fixing double-page pairing](docs/reader.md#fixing-double-page-pairing).
- **Backup settings: move existing snapshots when the location changes.** Changing where rotating backups are kept now offers **Move existing snapshots (N files, X MB)**, checked by default. The snapshots are moved in the background with progress on the card: each one is copied, checked (size and SHA-256) and only then removed from the old folder, never overwriting a file already there, and the **Keep the newest** limit then applies in the new folder. Any snapshot that could not be moved is listed and stays where it was. Clear the box to keep the previous behaviour (snapshots stay behind, unmanaged). API: `moveExistingSnapshots` on `PUT /api/v1/operations/backups/settings`, progress at `GET /api/v1/operations/backups/move`.
- The **Backup settings** card now explains that pre-migration and pre-restore safety snapshots always stay in the data folder (the newest 3 of each), so an upgrade or restore never depends on a custom folder that might be unavailable.
- **iPhone and iPad: full-screen reading hint.** In a Safari tab, Fullscreen can only hide MangaPixer's own bars; the Home Screen app is the true full-screen experience. A short, dismissible hint now shows the Share, **Add to Home Screen**, Add steps the first time you open the reader on a device and whenever you tap Fullscreen in a browser tab. **Not now** hides it until the page is reloaded and **Don't show again** remembers the choice on that device. It never shows in the installed app or on other devices.
- **Automatic library scans.** Each library is now rescanned on a schedule, **daily by default**, so new chapters appear without anyone pressing **Scan now**. Under each library in **MangaPixer Administration** > **Libraries**, **Auto-scan** offers **Off**, **Hourly**, **Every 6 hours**, **Daily** and **Weekly**, with the **Last scan** and an approximate **Next scan** beside it. The interval counts from the last completed scan, manual or automatic. Scans wait 3 minutes after start-up, run one library at a time and never alongside another scan, and a library whose folder is unreachable is skipped (logged once) until it comes back. `MangaPixer:Scanning:Scheduler:Enabled=false` turns automatic scans off for the whole server. See [Automatic scans](docs/library-layout.md#automatic-scans).

### Changed

- **Admin Analytics:** a caption under the overview tiles now explains that the totals include every user's activity, including Private libraries, while the per-user table leaves out each user's Private-library reading.

### Fixed

- **Double page:** switching modes after the first wide page did nothing, and the `d` key silently reset the cover offset. Both now work as described above.
- **Vertical mode:** the page strip can now be focused with the keyboard (arrow and Page keys scroll it, `Enter` shows or hides the controls). Tapping and scrolling are unchanged.

## [1.22.2] - 2026-09-24

### Changed

- **Unraid: install from Community Applications.** MangaPixer is now listed in the Unraid **Apps** tab. [Install on Unraid](docs/install-unraid.md) starts there, and the manual route downloads the same template from [dixit92/unraid-templates](https://github.com/dixit92/unraid-templates), which is now its only home. The duplicate copy at `deploy/unraid/mangapixer.xml` is removed; containers installed from it keep working.

### Fixed

- **Enhance on iPhone and iPad:** with the **Slide** page transition, tapping to the next page could sometimes leave a black box where the page should be, until you turned the page again. Slide no longer holds its final position after the turn, and if Safari ever drops an Enhance frame, the normal page now shows through instead of black.
- **List view:** the favorite star no longer covers the thumbnail. In list view it now sits at the end of the row, next to the read and selection markers, at full touch size; card view is unchanged.

## [1.22.1] - 2026-09-23

### Changed

- The Unraid template now mounts its media share at `/media/manga` and explains how to add more shares side by side (for example `/media/comics`). Mounting one share inside another could make Docker create a folder inside your media.
- Documentation brought up to date with the current release, and a new [How MangaPixer works](docs/how-it-works.md) page explains the parts that run, where data is stored, what happens when you scan and read, memory use and privacy. Notable corrections: the server does honour `X-Forwarded-*` headers from trusted proxies (secure cookies, per-client sign-in limits, `https://` activation links; the nginx example now sends them, and `MangaPixer:Network:KnownProxies` / `KnownNetworks` are documented), sessions last 7 days from your last activity, and user deletion and activation-link reissue are available. The reader guide now covers Page quality, Downscale filter and Enhance.

### Fixed

- `THIRD-PARTY-NOTICES.md` now attributes Anime4K (bloc97, MIT), which the Enhance option is built on, and the `anime4k-webgpu` package; the web runtime inventory is refreshed for the current dependencies.

## [1.22.0] - 2026-09-23

### Added

- **Library icons.** Administrators can give each library its own icon from a curated set (Admin > Libraries). The icon replaces the generic folder glyph in the sidebar, the mobile library list, the home page, and the libraries page. A library without a chosen icon gets a distinct default derived from its name, so libraries are easier to tell apart out of the box. New admin endpoint `PUT /api/v1/admin/libraries/{id}/icon` and an optional `icon` field on libraries.
- **Analytics for administrators.** A new Analytics section on the admin page shows library, content, processing and engagement totals, plus a per-user table (including your own account) with last sign-in, last reading activity, and counts of chapters completed, in progress, bookmarks and favorites. It shows counts and times only, never titles, file names or paths, and reading in a library a user marked Private is left out of that user's counts. New admin-only endpoints `GET /api/v1/admin/analytics/overview` and `GET /api/v1/admin/analytics/users`.
- **Backup settings in the admin page.** A new Backup settings card turns scheduled backups on or off and sets the interval and how many snapshots to keep, without editing configuration or restarting. Values set in configuration still win and are shown as managed by configuration. New admin endpoints `GET` and `PUT /api/v1/operations/backups/settings`.
- **Custom backup location.** Rotating backups can be kept in a folder of your choice (for example an archive disk or a NAS share) instead of the data folder, set in the Backup settings card (your current password is required) or with `MangaPixer:Backups:Location`. The server checks the folder before saving: it must be absolute and writable, its parent must exist, and it may not overlap the data, cache or scratch folders, your media or library folders, or system folders. If the folder later becomes unavailable (for example an unmounted share), backups pause and say so loudly (a banner in the admin page, the audit trail, and a Degraded `/health/ready`) instead of quietly filling the data disk. `MangaPixer:Backups:AllowLocationChange=false` locks the location. Pre-migration and pre-restore safety snapshots always stay in the data folder.
- **Unraid template.** `deploy/unraid/mangapixer.xml` is an Unraid container template with the same hardened settings as the Unraid Compose file; `docs/install-unraid.md` explains how to install it until it is listed in Community Applications.

### Changed

- **Lower idle memory.** The helper processes that open archives now shut down after sitting unused for a while (3 minutes by default) instead of running for as long as the server does, and the server no longer starts a second helper that it never used. A quiet server now uses roughly half the memory it did. The next page or scan starts a fresh helper, which adds about a fifth of a second to that first request. Tune with `MangaPixer:Media:WorkerIdleTimeoutSeconds` (`0` restores the old behaviour) and `MangaPixer:Media:MinWarmWorkers`.
- Pre-migration and pre-restore safety snapshots are now pruned: the newest 3 of each kind are kept. After a restart the backup schedule continues from the newest snapshot, so frequent restarts no longer each take a backup and push older daily snapshots out.
- The server now uses the .NET workstation garbage collector, which keeps its idle memory lower with no measured throughput cost (set `DOTNET_gcServer=1` to go back).

### Removed

- `POST /api/v1/operations/backup`, which wrote a database copy to any server path supplied in the request. Nothing in MangaPixer used it; use **Back up now** or the custom backup location instead. Strictly this removes an API route; it is listed here and under Security because it was an unsafe, unused endpoint.

### Fixed

- The Rendering "Enhance" upscaler now frees all of its GPU memory: previously only part of it was released when the page size changed, and none of it when you left the reader or turned Enhance off.
- Stopping the server (or an idle helper process) no longer waits five seconds and then force-kills each helper; helpers now exit promptly when asked to.

### Security

- Removed the unused `POST /api/v1/operations/backup` endpoint (see Removed): with an administrator session it could write a full database copy, including password hashes, to an arbitrary location, including source media folders or a network share on Windows.
- Backup failures no longer write absolute folder paths into the server log.

## [1.21.1] - 2026-09-21

### Fixed

- Webtoon pages no longer become extremely pixelated under Auto page quality. The reader now sizes each page's downloaded image from that page's own dimensions, so an unusually tall strip is fetched at full resolution instead of being shrunk to fit a shorter neighbour and then stretched back up on screen.

## [1.21.0] - 2026-09-21

### Added

- **Favorites.** Star any archive, folder, or subfolder to mark it a favorite. Favorites get a dedicated entry above your libraries in the sidebar and their own view (most recently favorited first), a star toggle on browse cards and rows, in the reader (favoriting the open chapter), and in search results, and a "favorites only" filter in browse. Two per-user options (off by default) let you also surface a Favorites row on the home page and give favorites prominence in search (a badge and a boost to the top of results). Favorites are per user and respect private/incognito libraries.
- **Update checker (opt-in).** Administrators can turn on a check that tells you when a newer MangaPixer release is available, shown in the admin page and app footer. It is off by default and makes a single request to the public GitHub Releases API with no identifying information or telemetry; it is the only outbound third-party call the server makes, and only when you enable it.
- **Reader keyboard shortcuts** for switching single/double page mode, cycling the downscale filter (Sharp / Balanced / Soft), and toggling Rendering (Smooth / Enhance). The reader help overlay lists the full set.

### Changed

- In list view you can now select an item directly with a per-row checkbox without first entering selection mode; tapping the row itself still opens the item.

## [1.20.0] - 2026-09-18

### Added

- A new "Downscale filter" reader option (Sharp / Balanced / Soft) chooses the resampling kernel the server uses for display-sized pages. Balanced (Mitchell) is the new default and tames the screentone moire that the sharper Lanczos kernel could produce; Sharp keeps the previous Lanczos look; Soft (area average) is the smoothest on heavily screentoned scans. The choice is per device, applies only when Page quality is Auto, and is sent as a `filter` query parameter on sized page requests; the `X-MangaPixer-Variant` header now reports it (for example `webp@2160:balanced`). Administrators can change the server default with `MangaPixer:Media:PageVariants:DefaultFilter`.
- The home "New chapters" cards now show the same Read / Reading marker as the library view, computed from the same read-state rollup the row's filter uses (a new `readState` field on each stack).
- When MangaPixer runs as an installed home-screen app, the reader now opens in its immersive mode with the bars hidden; the Fullscreen button still brings them back.

### Changed

- The home page's card-size slider is labelled as page-wide ("Card size", applies to all rows), and the New-chapters filter is visually tied to its section heading.

## [1.19.2] - 2026-09-18

### Fixed

- On iPad and iPhone, the reader's Fullscreen button now switches to an in-page immersive mode (hiding the reader's own bars) instead of using Safari's fullscreen, which showed a persistent system close button and the status bar over the page. Adding MangaPixer to the Home Screen gives a reader without Safari's bars; the installed app now declares an opaque black status bar and its app title.

## [1.19.1] - 2026-09-18

### Fixed

- The reader's "Enhance" rendering option now takes effect on high-density phone and tablet screens. It previously compared the page's on-screen size in CSS pixels with the image's native size, so on a 2x or 3x display a page that was actually being upscaled was treated as a downscale and the enhancement was skipped; desktop displays were unaffected.

## [1.19.0] - 2026-09-18

### Added

- Pages are now delivered at a size matched to the screen they are shown on. The reader asks the server for the smallest of three sizes (1080, 1440 or 2160 pixels on the longest edge) that still covers the display at its native pixel density, and the server produces that size with a high-quality Lanczos downscale. Pages load faster and line art and screentones look crisper than a browser downscale; nothing is ever upscaled on the server, and pages that are already small are sent as they are. A new "Page quality" reader option (Auto / Full) turns this off per device, and the Original size fit mode always requests full resolution.
- A new "Rendering" reader option (Smooth / Enhance) adds an optional GPU line-art upscaler (Anime4K, running in the browser via WebGPU) for paged and double-page views when a page is displayed larger than its native size. Enhance is off by default, loads its code only when selected, and is shown as unavailable on devices without WebGPU. It does not apply to the webtoon (vertical scroll) view in this release.
- Two configuration keys under `MangaPixer:Media:PageVariants` (`MaxDimensions`, `WebpQuality`) let administrators change the size ladder and the WebP quality used for sized page variants.
- Page responses carry an `X-MangaPixer-Variant` header naming the variant that was actually served.

### Fixed

- The per-snapshot Restore buttons in the Administration page's Backups card are now aligned in a consistent column.
- The installed Android home-screen app no longer shows a stray document-level scrollbar on open; normal browser tabs are unaffected.
- Clickable rows in the admin folder browser are real buttons now, so they can be reached and activated from the keyboard.

## [1.18.0] - 2026-09-18

### Added

- Administrators can now list the automatic rotating database backups and restore the server from a chosen backup, directly from the Administration page's Backups card.
- A new admin audit trail records sensitive administrative actions (user deletion, activation-link reissue, password reset, database restore, and logging changes) and is viewable as a paged list in the Administration page.
- The library list view now has a column-count control (1-3) on wide screens, alongside the existing card-size slider.

### Fixed

- In double-page (spread) reading, the Fit width, Fit height and Original size modes now size each page correctly instead of being constrained to the single-page rules.
- On an installed Android home-screen app, a false "zoomed in" reading no longer blocks swipe paging between pages.

## [1.17.1] - 2026-09-18

### Changed

- The Administration page now groups all logging controls in one **Debug Logging** card — the global log level with the per-subsystem overrides (Scanning / Media / Reading) shown beneath it — and gives database backups their own **Backups** card.

## [1.17.0] - 2026-09-17

### Added

- Admins can now delete a user account. The last remaining admin is protected and cannot be deleted.
- Admins can reissue a fresh activation link for a user who was invited but has not yet activated their account.
- The home "New chapters" view now has a read-state filter (Reading / Read / Unread), matching the library browse filter.
- In-reader bookmarks: bookmark the current page and jump back to bookmarked pages from a bookmarks panel in the reader.
- A per-category debug log-level control is now available in the Administration screen.

### Changed

- The browse sort menu uses distinct icons for Name / Recently added / Recently read / Recently updated, which previously read as three similar clock glyphs.
- The library list view now uses multiple columns on wide screens, and the selection/read marker sits to the right of each row instead of over the cover thumbnail.
- The reader's next/previous chapter arrows are now direction-aware, reflecting left-to-right versus right-to-left reading direction.

## [1.16.0] - 2026-09-17

### Added

- Reverse-proxy support: the server now honors `X-Forwarded-*` headers from trusted proxies, configurable via `MangaPixer__Network__KnownProxies` / `MangaPixer__Network__KnownNetworks` (default trusts loopback and private ranges only). Behind a TLS-terminating reverse proxy, activation links now use the correct external scheme (`https`) instead of `http`.

### Security

- Auth cookies are now marked `Secure` when the effective request scheme is `https` (including behind a trusted reverse proxy), while plain-http LAN access still works.
- Logging out now actually revokes the server-side session record, so the session cannot be reused after logout.
- Changing a user's admin role now immediately invalidates that user's existing sessions instead of taking effect only at their next sign-in.
- Restoring a database backup now genuinely invalidates all sessions.
- The login rate limiter now keys on the real client IP when behind a trusted proxy (so distinct clients get distinct limits), reports an accurate `Retry-After`, and no longer shares a single rate-limit bucket across all activation attempts.

### Fixed

- Active sessions now extend their expiry as they are used, instead of being hard-expired after a fixed 7 days despite the sliding auth cookie.

## [1.15.0] - 2026-09-17

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
