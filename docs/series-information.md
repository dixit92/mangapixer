# Series information

MangaPixer can show a series summary (title, description, authors, genres, publication status, cover art) for your folders and archives. The information comes from two places:

- **ComicInfo.xml** inside your archives. It is read when an archive is analysed; nothing leaves your server.
- **MangaUpdates**, only if an admin allows it. An admin *identifies* a folder (or a single archive) with a MangaUpdates series; MangaPixer then fetches that series' details and cover once and stores them on your server. With **Automatic matching** on, MangaPixer also matches new series folders on its own (see [Automatic matching](#automatic-matching)).

Folders and archives with information show an **(i)** in the cover's bottom-left corner. It opens a side panel (a bottom sheet on a phone); **Open series page** leads to the full page. A folder that holds several series (an anthology or an author's folder, for example) lists them in the panel instead, without a series page. Inside a series folder, **Series info** in the top bar opens the same panel; an admin inside a folder without information sees **Identify…** there instead (when web lookups are on for the library). The (i) and the top-bar button update as soon as an admin links, unlinks or marks a folder, without reloading the page.

On the series page the description appears once, under **About**. Genres are shown as text. The precedence line under **Sources** appears only when both MangaUpdates data and ComicInfo exist. A series page for a single archive offers **Read** (or **Continue reading**) and **Show in folder**.

## Where the information applies

A link made on a folder applies to the folder and everything inside it, so every chapter in a series folder shows the series. An archive can also be linked on its own (a one-shot in an artist folder, for example).

- **Don't match** marks a folder or archive as "not one series": nothing is inherited from above. Use it on anthology, magazine and artist folders, then link the right subfolders or archives individually.
- **Source precedence** decides which source wins when both have a value: **Web first** (the default) or **ComicInfo first**, per library or per folder. Chapter-level details (number, volume, chapter title) always come from ComicInfo.

These actions are in the panel's and series page's **Admin** menu, and in the **Series** menu of the browse selection bar (hidden while **Show series information** is off for that library or globally).

## Search by alternative title

Once a folder or archive is linked to a series, **Search** also finds it by any of that series' alternative titles: a folder named `Dungeon Meshi` turns up for `Delicious in Dungeon`. These hits appear in their own **Series matches** row above the normal results (at most 20, on the first page of results), and each card adds a caption such as `aka Delicious in Dungeon` (the shortest matching title; hover for the full text). A folder that is also a normal name match is shown once, in **Series matches**.

- Only links that are in use count (**Confirmed** or automatic); folders marked **Don't match** and matches awaiting review never appear.
- The row follows the same access rules as the rest of search: libraries you cannot see, and Private libraries while Incognito is on, contribute nothing.
- When **Show series information** is off, globally or for a library, that library's alternative titles are not searchable.
- Searching runs entirely on your server; nothing is sent to MangaUpdates.

## Identify a series (admins)

Before you can look anything up, turn on **Fetch series information from the web** (see [Admin settings](#admin-settings)) and the **Fetch** switch of the library.

1. Open **Identify…** from the panel's or series page's **Admin** menu, from **Identify…** in the top bar inside a folder without information, or select one folder in browse and choose **Series** > **Identify…**. When lookups are off, the menu item and the dialog say why.
2. Search: the box is filled with a suggestion (the cleaned folder name, an English title in trailing square brackets such as `[Delicious in Dungeon]`, or the ComicInfo series). Edit it if needed and press **Search**. Only this text is sent. **Hide doujinshi & novels** (on by default) leaves doujinshi, novels, artbooks and drama CDs out of the results.
   - Or paste a MangaUpdates series address (`https://www.mangaupdates.com/series/njeqwry/berserk`) or a shortcode (`mu:51239621230`, `mu:njeqwry`) and press **Look up**. MangaPixer reads the series number from what you paste, so only that number is sent. Old-style `series.html?id=` addresses do not work: open the series on MangaUpdates and copy its current address.
   - If an archive's ComicInfo already points to a MangaUpdates series, **ComicInfo points to a MangaUpdates series – Use it** previews it directly.
3. Results are ranked by how closely they match the folder name (**Strong**, **Possible**, **Weak**). "matched as" shows the alternative title MangaUpdates matched. **Preview** opens a series.
4. The preview shows your folder next to the MangaUpdates record: item count, the ComicInfo series, whether the pages are tall strips, and MangaUpdates' type (Manga, Manhwa, …), year, volumes, authors and genres ("MangaUpdates: webtoon" when its users tag it so). Warnings point out likely mistakes, for example a novel instead of the comic, a different year, or far more items than the record has volumes or chapters.
5. **Link** stores the series and its cover and applies it. **Undo** in the message that follows restores what was there before.

**Refresh from MangaUpdates** in the **Admin** menu fetches the linked series again (the page shows how long ago it was fetched). **Unlink** removes a link; inheritance from above resumes. Unless **Automatic matching** is on, nothing is refreshed or matched automatically.

## Automatic matching

**Automatic matching** (see [Admin settings](#admin-settings)) is one switch for the whole server, off by default. It applies to every library whose **Fetch** switch is on. Turning it on sends nothing by itself; it needs its own consent, separate from the one for **Fetch from the web**.

- **What is matched.** After each scan, MangaPixer looks at the folders the scan found and decides from their names and contents what each one is: a series (chapters or volumes of one work, also with `Volumes` / `Chapters` subfolders), a one-shot, or a collection of different works (an anthology, an artist's folder). Series folders are matched as a whole; in a collection folder each archive, or each numbered group such as `Title 1` / `Title 2`, is matched on its own. Archives lying loose next to other folders are matched one by one too (a one-shot, or a whole series kept in one archive), unless they look like volumes or chapters of one work. Category and container folders (`Manga`, an author's folder of series) are never matched themselves; the series inside them are. Folders that are unclear go to review and are never linked automatically.
- **What is never matched.** Anything with a link already (confirmed or automatic), anything inside a folder marked **Don't match**, and anything inside a linked series.
- **Confident matches go live at once** and are listed under **Auto-linked** so you can check them: **Confirm**, **Unlink**, **Identify…** or **Don't match**. Close calls go to **Needs review** with up to five stored candidates; nothing from them is shown to readers until you accept one. Works with no good match are listed under **Unmatched** and tried again after 30, 90 and 180 days, then not any more.
- **Match this library now** (per library) queues every work of a library that has never been matched and shows first how many works that is, how many requests it will roughly take and how many days at the current daily budget. **Review everything once** sends even the confident matches of that run to **Needs review**. **Re-run matching** on selected review rows tries them again.
- **Pacing.** Automatic lookups send at most one request per second, so Identify stays responsive, and they count in the same daily budget as everything else: when it is used up, automatic matching waits for the next day (00:00 UTC). Raise the budget if you want it to go faster. When MangaUpdates asks MangaPixer to slow down, automatic matching pauses too.
- **Doujinshi.** Automatic searches leave doujinshi, novels, artbooks and drama CDs out, like **Hide doujinshi & novels**. Set a folder's **Content** to **Doujinshi & adult one-shots** to allow doujinshi results for everything below it; **Not doujinshi** and **Auto** (the default) keep them out. The nearest folder with a setting wins, like a folder's default reading mode.
- **Thresholds** (advanced): how close a title must be for an automatic link (0.85-0.99, default 0.92), how far ahead of the runner-up it must be (0.05-0.30, default 0.10), and the lowest score that still goes to review (0.40-0.90, default 0.60). **Reset to defaults** restores them.

### Review

The **Review** page lists, per library or for all libraries: **Needs review**, **Auto-linked**, **Unmatched**, **Flags**, **Don't match**, **Confirmed** and **Missing folders**. Stored candidates show their title, type, year, volumes and why they were not linked automatically (for example a close second match, or a year or volume count that does not fit). A candidate's cover is fetched only when you open it. **Accept** links the chosen candidate; bulk actions accept the top candidates, confirm automatic links, mark Don't match, unlink or re-run matching for many rows at once. **Runs** shows each matching run with its counts and how many of its results admins later changed or accepted; these counts stay on your server.

### Wrong series?

Readers who see series information from MangaUpdates can report it with **Wrong series?** (wrong series, wrong details, not one series, or something else, with an optional note of up to 500 characters). The report is on the series, not the single archive. A reader can have one open report per series and send up to 20 reports a day (more on large servers: 2% of the series they can see). Admins see the reports, with the note, under **Flags** and resolve them by relinking with **Identify…**, **Unlink**, **Don't match** or **Dismiss**; resolving applies to every open report on that series. Readers see whether their report is still open or has been reviewed. Notes are shown only to admins and never written to the logs.

### Renamed and moved folders

When a folder is renamed or moved, MangaPixer recognises its archives at the new place and moves the folder's link or **Don't match**, its source precedence, its default reading mode and its **Content** setting to the new folder, as long as at least 80% of its archives went to the same new folder. Otherwise (a folder split in two, for example) the settings wait under **Missing folders**, where you can **Re-attach** them to a folder of the same library or delete them. Archives keep their links and reading progress when they move anyway.

### Background refresh

With **Automatic matching** on, linked series are refreshed from MangaUpdates by their series number: every 30 days while they are ongoing, every 90 days once complete, never when MangaUpdates no longer lists them, and at most 100 a day. Only the series number is sent. The cover is fetched again only when it changed.

## What is sent

Only to `api.mangaupdates.com` (search and series details) and `cdn.mangaupdates.com` (cover images), only in libraries whose **Fetch** switch is on, and only:

- when an admin presses **Search**, **Look up**, **Preview**, **Link**, **Accept** or **Refresh**: the search text you confirmed;
- with **Automatic matching** on: the cleaned name of each new series folder, or of an archive that is its own work - in a collection folder or loose next to other folders (for example "Series Title" from "Series Title [English Title]"), or the series name its ComicInfo agrees on - **sent without anyone reviewing it first**, which is why this needs its own consent. A MangaUpdates link in an archive's ComicInfo is used first, and then only its number is sent. Nothing inside a folder marked **Don't match** is ever looked up;
- with **Hide doujinshi & novels** ticked, and always for automatic searches, the fixed list of types to leave out (`Doujinshi`, `Novel`, `Artbook`, `Drama CD`; without `Doujinshi` below a folder whose **Content** is **Doujinshi & adult one-shots**);
- MangaUpdates series numbers;
- a generic `User-Agent: MangaPixer-Metadata`.

Never sent: file paths, your file list, user accounts, reading progress, reports and their notes, cookies, or anything that identifies your server. MangaUpdates sees your server's IP address, as with any web request. Readers' browsers never contact MangaUpdates: covers are stored on your server and served by MangaPixer. Logs record ids, counts, status codes and timings, never search text, folder names or titles.

MangaPixer is polite to MangaUpdates: at most 2 requests per second (5 per second for cover images), a daily request budget, and when MangaUpdates asks it to slow down it waits (up to an hour) before trying again. Search results are kept in memory for an hour, so repeating a search sends nothing.

Series data is provided by [MangaUpdates](https://www.mangaupdates.com) as-is and is credited to it wherever it is shown.

## Admin settings

**MangaPixer Administration** > **Series metadata**:

- **Show series information**: hides all series information (from files and from the web) for everyone when off. The data stays stored. Each library has its own **Show** switch too.
- **Fetch from the web**: off by default. It can only be turned on after ticking the box under the consent text that explains what is sent. Turning it on sends nothing by itself.
- The status line shows the requests used today, whether MangaUpdates asked MangaPixer to wait, and the last error.
- **Automatic matching**: off by default, one switch for all libraries whose **Fetch** is on. It can only be turned on after ticking the box under its own consent text (folder names are sent automatically, without review), and only while **Fetch from the web** is on. Turning it off stops new lookups; links it made stay until you remove them. The advanced **Thresholds** and **Reset to defaults** are next to it.
- **Daily request budget**: a whole number, 5000 by default. Every search, series fetch and cover image counts one, whether an admin or automatic matching asked for it; the count resets at 00:00 UTC. There is no separate limit for automatic matching: it stops when the budget is used up, and so does Identify until the next day or until you raise the budget.
- Per library: **Match this library now** with its estimate (see [Automatic matching](#automatic-matching)).
- Per library: **Fetch**, **Show**, **Precedence**, and **Delete fetched data** (removes that library's web links and the stored series and covers no other library uses).
- **Delete all fetched web data** removes every web link, stored series and cover. Don't-match marks and ComicInfo information stay.
- "ComicInfo: X of Y archives read" shows how far the background ComicInfo read has come.

To make sure the server never contacts MangaUpdates, whatever is set in the app, set `Metadata__NetworkDisabled=true` (see [Configuration](configuration.md#settings-stored-in-the-app)).
