# Privacy and security

What MangaPixer keeps to itself, what it can send when an admin allows it, and how to report a vulnerability.

- **No telemetry, analytics or phone-home.** Fonts and icons are bundled and served by your own server, never from a CDN.
- **Two optional internet features, both off by default and switched on only by an admin:** the Update Checker and fetching series information from MangaUpdates (see below).
- **Logs never contain paths or titles**, only IDs, counts, timings and sanitized error codes. Reader-facing API responses never contain filesystem paths.
- **Source media is read-only**, enforced by the code and by the `:ro` mounts.
- **No default credentials.** The first admin is created by you on the setup screen, and that endpoint refuses once any user exists.
- Session cookies use ASP.NET Core Data Protection; the keys live in `<DataRoot>/keys`, so treat the data volume as sensitive.

## What leaves your server

Out of the box, nothing: MangaPixer makes no internet requests until an admin turns something on. The **Update Checker** asks GitHub once a day whether a newer release exists. **Fetch series information from the web** (Administration > Series metadata, after a consent text, and per library) lets an admin look a folder up on [MangaUpdates](https://www.mangaupdates.com): only the search text the admin confirms in the Identify dialog, MangaUpdates record numbers, a fixed list of types to leave out (when **Hide doujinshi & novels** is ticked) and a generic `User-Agent` are sent, only to `api.mangaupdates.com` and `cdn.mangaupdates.com`, and only when an admin presses Search, Look up, Link or Refresh. File paths, your file list, user accounts and reading progress are never sent. Fetched data and cover art are stored on your server and served from it, so readers' browsers never contact MangaUpdates. **Automatic matching** is a separate switch with its own consent text, off by default: when an admin turns it on, libraries that fetch from the web are matched in the background, so the cleaned name of each new series folder (or of an archive that is its own work: in a collection folder, or loose next to other folders) is sent without anyone reviewing it first, and linked series are refreshed by their MangaUpdates number. It uses the same hosts and the same daily request budget, at most one request per second, and never looks inside a folder marked **Don't match**. `Metadata__NetworkDisabled=true` switches all of this off regardless of the admin settings. Details: [Series information](series-information.md#what-is-sent).

Series data fetched from the web is provided by [MangaUpdates](https://www.mangaupdates.com) and credited to it wherever it is shown.

To report a vulnerability, see [SECURITY.md](../SECURITY.md).
