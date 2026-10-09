# Features

Everything MangaPixer does today, by area. The [README](../README.md) has the short version; the links lead to the guides with the details.

## Library and browsing

- Browse the folder hierarchy in a card view (with a card-size slider) or a list view.
- Sort by name (ascending or descending), recently added, recently read, or recently updated. Recently updated ranks a folder by its newest archive, like a new chapter.
- Filter by read state (reading / read / unread) at any folder level. The filter also applies to series folders through their contents, and you can hide empty folders.
- A-Z jump rail with multilingual collation, infinite scroll in both directions after a jump, and read/reading rollup badges on folders. This means you can tell at a glance whether a folder contains only comics you've read, some you haven't read yet, or nothing you've started at all.
- Multi-select, including shift/ctrl ranges and touch long-press, to mark items read or unread in bulk.
- Full-text search over titles and folder names (trigram search), with cover thumbnails and a folder-vs-archive badge. Series linked to MangaUpdates are also found by their alternative titles: a `Dungeon Meshi` folder turns up when you search for `Delicious in Dungeon`.

## Reader

- Four modes: paged left-to-right, paged right-to-left (manga), double-page spreads, and vertical webtoon scrolling. Admins set a default mode per library and per folder, and each user can override it per item or set a personal default.
- Double-page mode adapts: it shows a single page in narrow portrait, keeps wide spreads whole, and shows both page numbers. When extra pages split a spread across the wrong pair, you can shift the pairing anywhere in an archive, and it is saved for everyone who reads it.
- Image quality: pages are sent at the size your screen shows them (or full size, your choice), downscaled on the server with a choice of filter to keep screentones clean. Pages shown larger than their resolution can be redrawn on your device's graphics chip: **Crisp** (AMD FSR 1, light; the default) or **Enhance** (the Anime4K line-art upscaler, **Efficient** or **Max quality**), in paged and vertical mode. Enhance uses WebGPU over HTTPS and WebGL2 elsewhere, so both also work over plain `http://` on your LAN, and the reader always shows which engine is running.
- Touch and keyboard navigation: direction-aware swipe zones, arrow keys, a draggable page scrubber, a help overlay (`?`), and immersive fullscreen.
- Webtoon tap zones and swipe move by a configurable step (90% of the screen by default). Turn them off for free scrolling only.
- Configurable page-turn animation (Slide / Reveal / None).
- Auto-advance to the next or previous archive, plus page prefetch around the current position.

## Series information

- Reads the `ComicInfo.xml` inside your archives (series, number, summary, credits, genres, publisher). Nothing leaves your server for this.
- A card with series information shows an **(i)**: it opens a side panel with the summary, and a full series page lists the items.
- Admins can link a folder or archive to a [MangaUpdates](https://www.mangaupdates.com) series with **Identify** (search, or paste a MangaUpdates address). Optional and off by default; see [What leaves your server](privacy-and-security.md#what-leaves-your-server).
- **Automatic matching** (a separate switch with its own consent, also off by default) matches new folders in the background. It tells series, one-shots, artist folders and collections apart by their names and contents, so category folders are never linked themselves. Confident matches go live at once; close calls wait for an admin on the **Metadata Manager** page (account menu), where each can be accepted, identified by hand or marked **Don't match**. Linked series are refreshed in the background.
- In **Needs review**, **Later** sets a hard case aside (kept on the server, for every admin) so the easy matches can be accepted first. A row also says when other waiting works are by the same circle or artist (read from names like `[Circle (Artist)] Title`, also without the opening bracket) or sit in the same folder; one tap lists them together for a bulk action, and **Authors** lists the largest groups first.
- Several selected items can be identified **one by one** (the Identify dialog steps through them), and **Re-run matching** works on a selection from the library view.
- Webtoons, manhwa and manhua without a real volume list show their **chapters** in order instead of guessed volumes, and are counted in chapters (*new in 1.34.0*).
- In a folder of stories, such as an artist's one-shots, the stories linked to the same collected volume (tankoubon) show as one stacked card in the Volumes view (*new in 1.37.0*; see [Stories collected in one volume](volumes.md#stories-collected-in-one-volume)).
- Readers can report a wrong series with **Wrong series?**; admins see the reports on the same page.
- Per folder, admins choose whether web data or `ComicInfo.xml` wins, whether it holds doujinshi (**Content**), and can mark a folder **Don't match** when it is not one series, or (*new in 1.34.0*) **Collection about** a series when it holds works about it, such as fan-made doujinshi: the folder shows the series, and each work inside is matched on its own. *New in 1.37.0:* **Artist folder** marks a folder of one artist's works - it is never linked to a series, its stories are matched one by one, and records by that artist count in their favour (see [Artist folders](series-information.md#artist-folders)). Renamed or moved folders keep these settings. **Show series information** hides it all for everyone.

## Per-user reading state

- Each user has their own progress, read marks and "Continue reading" row. You can dismiss items from the row, and finished ones hide automatically.
- A "Start reading" / continue shortcut on each folder that opens the next unread item.
- Optional "always open read items from the start" preference.

## Home

- "Continue reading": one row across all your libraries.
- "New chapters": recently added archives, grouped by library and stacked per series folder (latest chapter plus a "+N" badge): the folder with its own series link, or else the folder that holds the new archives, so a library whose top level is categories such as Manga and Manhwa shows one card per series, not per category. Each user chooses the time window in days (30 by default) and which libraries contribute.
- Continue reading and New chapters cards carry the same controls as the library view: the favorites star and, where there is series information, the (i) and the summary on hover.
- A New chapters card of a linked series, and a Continue reading card of one of its chapters, show the series cover (for example your volume 1's cover) rather than a chapter's first page - see [Covers](covers.md#on-the-home-page).
- Tapping a New chapters card of a linked series opens it in the Volumes view (by name, the Continue row on top); other folders open sorted by Recently updated.
- A grid of your libraries.

## Multi-user, privacy and access

- No default credentials. A fresh instance shows a first-run setup screen, and the first admin account is created there (see [First-run setup](users-and-access.md#first-run-setup)).
- Admin and reader roles, with per-user library access grants.
- Onboard users with a password, or with a single-use activation link that expires after 48 hours so the user sets their own password.
- **Private libraries and Incognito:** each user can mark libraries as private. While Incognito is on (the default for every new browser session), private libraries are hidden from browse, home and search.
- Login rate limiting, CSRF protection on authenticated state-changing requests, forced password change, per-user session revocation, and last-admin protection.

## Administration and operations

- Add, rename and remove libraries from the web UI, using a folder picker confined to the media root. Removing a library deletes only MangaPixer's own metadata and thumbnails; your files are untouched.
- Scans run per library or across all libraries, and can be canceled. Each library is also rescanned automatically on its own schedule (daily by default; hourly, every 6 hours, weekly or off), set in **Scheduled jobs**. There is no filesystem watching yet; an app with an [API token](api-tokens.md#request-a-library-scan) allowed to request scans can start one after it adds files (*new in 1.36.0*).
- Persistent thumbnails that survive restarts and cache clears. A background backfill fills them in and yields to active readers. Thumbnails can be regenerated per library.
- Automatic rotating database backups (daily, 7 kept by default) and on-demand backups. A validated backup can be uploaded for restore; it is applied atomically on the next restart, with rollback if that fails (see [Backup and restore](backup-and-restore.md)).
- Runtime log-level control (global and per subsystem) and a diagnostics export.
- YACReader progress import: if a library folder contains a YACReader library database, an admin can preview its read progress and import it into their own account. The YACReader data is only read.
- **API tokens and a metadata export for other apps:** an admin creates a read-only token for a tool such as MangaList, which can then read each linked series' information (links, volume lists, completion) from `/api/v1/export/` - names only, never a path. A token works nowhere else and is stored only as a fingerprint; revoked and expired tokens can be cleared from the list (*new in 1.37.0*) (see [API tokens](api-tokens.md) and [Metadata export API](metadata-export.md)).
- Health endpoints (`/health`, `/health/ready`) and an OpenAPI document at `/openapi/v1.json` (the checked-in contract is `contracts/openapi.json` in the repository).
- Web app manifest and icons, so MangaPixer can be added to a phone or tablet home screen.

## Supported formats

| | Formats |
|---|---|
| Archives | ZIP (`.cbz`, `.zip`), RAR 4 and RAR 5 (`.cbr`, `.rar`) |
| Page images | JPEG, PNG, WebP, AVIF, GIF, BMP, TIFF |

Archives are read in place by a managed library (no external `7z` or `unrar` binary), and pages are decoded in a separate, supervised worker process so a malformed file cannot take the server down. Not supported yet: **solid RAR and 7-Zip archives** (scanned and listed, but the reader cannot open them; repack them as `.cbz`), PDF, EPUB, CBT and folders of loose images. Details: [Supported archive formats](library-layout.md#supported-archive-formats).
