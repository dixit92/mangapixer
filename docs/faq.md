# FAQ

### Is there a default username and password?

No. A new server has no accounts. You create the first admin on the setup screen the first time you open it. See [Install with Docker](install-docker.md#step-5-create-the-admin-account).

### Will it change, move or tag my files?

No. The server only ever reads your media; everything it generates lives in its own data, cache and scratch folders. The install guides also mount your media read-only. **Remove library** deletes only the server's own records. See [Library layout](library-layout.md#read-only-guarantee).

### Which formats can it read?

CBZ/ZIP and CBR/RAR (non-solid). CB7/7z files show up in the library but cannot be opened yet. PDF, EPUB and folders of loose images are not supported. See [Library layout](library-layout.md#supported-archive-formats).

### Why does "Chapter 10" come before "Chapter 2"?

Name sort in the library compares names character by character. Zero-pad the numbers in your file names (`Chapter 002`, `Chapter 010`). Pages inside an archive already sort by number. See [Library layout](library-layout.md#sorting).

### Does it notice new files automatically?

No. An admin starts every scan with **Scan now** or **Scan all libraries**. See [Library layout](library-layout.md#rescans-moves-and-deletions).

### Does it use ComicInfo.xml or fetch metadata online?

No. Names come from your folders and files. The server makes no calls to metadata services and has no telemetry or analytics. The web app's fonts and icons are bundled, so it works on a network with no internet access.

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

- **Database:** grows with the number of archives and users. <0.5 GB for 10,000+ tracked archives
- **Thumbnails:** one small WebP per archive, in the data root.
- **Page cache:** capped at 1 GiB by default.
- **Scratch:** up to 1 GiB, and only used heavily by solid archives.

The cache and scratch limits are configurable (see [Configuration](configuration.md#storage)).

### Can I run it without Docker?

Yes, on Windows. Since 1.13.0 there is a native package: a tray app that runs the server, installed by a per-user MSI. See [Install on Windows](install-windows.md). On Linux and NAS systems, Docker is the supported way to run it.

### Is there an API?

Yes. Everything the web app does goes through a JSON API under `/api/v1`. Its description is served at `/openapi/v1.json`. For calling admin endpoints from a script, see [Backup and restore](backup-and-restore.md#calling-the-admin-api-from-a-script).

### Is this YACReader?

No. MangaPixer is an independent server inspired by YACReader, not a fork. It can import your YACReader reading progress; see [Backup and restore](backup-and-restore.md#importing-reading-progress-from-yacreader).
