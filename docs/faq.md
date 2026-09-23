# FAQ

### Is there a default username and password?

No. A new server has no accounts. You create the first admin on the setup screen the first time you open it. See [Install with Docker](install-docker.md#step-5-create-the-admin-account).

### Will it change, move or tag my files?

No. The server only ever reads your media; everything it generates lives in its own data, cache and scratch folders. The install guides also mount your media read-only. **Remove library** deletes only the server's own records. See [Library layout](library-layout.md#read-only-guarantee).

### Which formats can it read?

CBZ/ZIP and CBR/RAR (non-solid). CB7/7z files show up in the library but cannot be opened yet. PDF, EPUB and folders of loose images are not supported. See [Library layout](library-layout.md#supported-archive-formats).

### How are "Chapter 2" and "Chapter 10" ordered?

Numerically. Name sort compares runs of digits as numbers, so `Chapter 2` comes before `Chapter 10` whether or not the numbers are zero-padded. Pages inside an archive use the same rules. Libraries scanned by an older version are corrected automatically the first time the upgraded server starts; no rescan is needed. See [Library layout](library-layout.md#sorting).

### Does it notice new files automatically?

No. An admin starts every scan with **Scan now** or **Scan all libraries**. See [Library layout](library-layout.md#rescans-moves-and-deletions).

### Does it use ComicInfo.xml or fetch metadata online?

No. Names come from your folders and files. The server makes no calls to metadata services and has no telemetry. The web app's fonts and icons are bundled, so it works on a network with no internet access.

### Does it contact the internet at all?

Only if an admin turns on the **Update Checker**, which is off by default. It then asks GitHub at most once a day whether a newer MangaPixer release exists, without sending anything about your server or users. See [Configuration](configuration.md#settings-stored-in-the-app).

### If I rename or move a series folder, do I lose my progress?

Not for archives that were already analyzed. On the next scan, MangaPixer recognizes moved archives by their content and keeps their progress. See [Library layout](library-layout.md#rescans-moves-and-deletions).

### Does each person have their own progress?

Yes. Progress, read marks, Continue reading, Private libraries and most settings belong to each account. Admins decide which libraries each reader can see.

### Does Incognito stop my reading from being recorded?

No. Incognito hides the libraries you marked Private from Home, search and browsing. Progress is still saved. See [Users and access](users-and-access.md#private-libraries-and-incognito).

### Can I use it on my phone or tablet?

Yes, in the browser. The layout adapts to phones and tablets, and the reader supports tap zones and swipes. You can add it to your home screen, where it opens without the browser's toolbars. There is no native app, and no offline or download mode; pages stream from your server.

### Can I reach it from outside my home?

Yes, through a reverse proxy that adds HTTPS. See [Reverse proxy and HTTPS](reverse-proxy-and-https.md). Serving it under a sub-path (like `/manga`) is not supported; give it its own hostname.

### How do I move my server to another machine?

1. Stop the container.
2. Copy the whole data root (`/data`, or `/config/data` on Unraid) to the new machine. Copy `keys/` along with it if you want everyone to stay signed in.
3. Mount your media at **the same paths inside the container** as before (for example `/media/comics`). Libraries remember their folder paths.
4. Start the container.

Cache and scratch do not need to be copied.

### How much disk space does it need?

- **Database:** grows with the number of archives and users. Expect a few hundred MB for tens of thousands of archives.
- **Thumbnails:** one small WebP per archive, in the data root.
- **Backups:** by default the newest 7 daily database copies, each about the size of the database. You can keep fewer, or move them to another disk (see [Backup and restore](backup-and-restore.md#choosing-where-backups-are-kept)).
- **Page cache:** capped at 1 GiB by default.
- **Scratch:** capped at 1 GiB; it only holds files for a moment while pages are extracted.

The cache and scratch limits are configurable (see [Configuration](configuration.md#storage)).

### How much memory does it use?

When nobody is reading, the server itself typically uses around 150-300 MB. Opening archives is done by separate helper processes, each around 100-200 MB while it works; they start when needed and shut down after 3 minutes without work, so an idle server runs none. A reader waiting on a page always gets priority over background work. On a small NAS you can limit the helpers to one with `MangaPixer__Media__MaxConcurrentJobs=1`. See [Configuration](configuration.md#media-processing) and [How MangaPixer works](how-it-works.md).

### Why SQLite? Do I need a database server?

No database server is needed. MangaPixer keeps everything in one SQLite file, `mangapixer.db`, in its data folder. A home library has a handful of users and a single server, which is exactly what SQLite is good at: nothing extra to install, secure or upgrade, and a backup is a copy of one file. The server takes consistent backups for you while it runs.

### Can I run it without Docker?

Yes, on Windows. Since 1.13.0 there is a native package: a tray app that runs the server, installed by a per-user MSI. See [Install on Windows](install-windows.md). On Linux and NAS systems, Docker is the supported way to run it.

### Is there an API?

Yes. Everything the web app does goes through a JSON API under `/api/v1`. Its description is served at `/openapi/v1.json`. For calling admin endpoints from a script, see [Backup and restore](backup-and-restore.md#calling-the-admin-api-from-a-script).

### Is this YACReader?

No. MangaPixer is an independent server inspired by YACReader, not a fork. It can import your YACReader reading progress; see [Backup and restore](backup-and-restore.md#importing-reading-progress-from-yacreader).
