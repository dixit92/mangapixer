# Series information

MangaPixer can show a series summary (title, description, authors, genres, publication status, cover art) for your folders and archives. The information comes from two places:

- **ComicInfo.xml** inside your archives. It is read when an archive is analysed; nothing leaves your server.
- **MangaUpdates**, only if an admin allows it. An admin *identifies* a folder (or a single archive) with a MangaUpdates series; MangaPixer then fetches that series' details and cover once and stores them on your server.

Folders and archives with information show an **(i)** in the cover's bottom-left corner. It opens a side panel (a bottom sheet on a phone); **Open series page** leads to the full page. A folder that holds several series (an anthology or an author's folder, for example) lists them in the panel instead, without a series page. Inside a series folder, **Series info** in the top bar opens the same panel.

## Where the information applies

A link made on a folder applies to the folder and everything inside it, so every chapter in a series folder shows the series. An archive can also be linked on its own (a one-shot in an artist folder, for example).

- **Don't match** marks a folder or archive as "not one series": nothing is inherited from above. Use it on anthology, magazine and artist folders, then link the right subfolders or archives individually.
- **Source precedence** decides which source wins when both have a value: **Web first** (the default) or **ComicInfo first**, per library or per folder. Chapter-level details (number, volume, chapter title) always come from ComicInfo.

These actions are in the panel's and series page's **Admin** menu, and in the **Series** menu of the browse selection bar.

## Identify a series (admins)

Before you can look anything up, turn on **Fetch series information from the web** (see [Admin settings](#admin-settings)) and the **Fetch** switch of the library.

1. Open **Identify…** from the panel's or series page's **Admin** menu, or select one folder in browse and choose **Series** > **Identify…**. When lookups are off, the menu item and the dialog say why.
2. Search: the box is filled with a suggestion (the cleaned folder name, an English title in square brackets such as `[Delicious in Dungeon]`, or the ComicInfo series). Edit it if needed and press **Search**. Only this text is sent.
   - Or paste a MangaUpdates series address (`https://www.mangaupdates.com/series/njeqwry/berserk`) or a shortcode (`mu:51239621230`, `mu:njeqwry`) and press **Look up**. MangaPixer reads the series number from what you paste, so only that number is sent. Old-style `series.html?id=` addresses do not work: open the series on MangaUpdates and copy its current address.
   - If an archive's ComicInfo already points to a MangaUpdates series, **ComicInfo points to a MangaUpdates series – Use it** previews it directly.
3. Results are ranked by how closely they match the folder name (**Strong**, **Possible**, **Weak**). "matched as" shows the alternative title MangaUpdates matched. **Preview** opens a series.
4. The preview shows your folder next to the MangaUpdates record: item count, the ComicInfo series, whether the pages are tall strips, and MangaUpdates' type, year, volumes, authors and genres ("MangaUpdates: webtoon" when its users tag it so). Warnings point out likely mistakes, for example a novel instead of the comic, a different year, or far more items than the record has volumes or chapters.
5. **Link** stores the series and its cover and applies it. **Undo** in the message that follows restores what was there before.

**Refresh from MangaUpdates** in the **Admin** menu fetches the linked series again (the page shows how long ago it was fetched). **Unlink** removes a link; inheritance from above resumes. Nothing is refreshed or matched automatically.

## What is sent

Only when an admin presses **Search**, **Look up**, **Preview**, **Link** or **Refresh** in an enabled library, and only to `api.mangaupdates.com` (search and series details) and `cdn.mangaupdates.com` (cover images):

- the search text you confirmed;
- MangaUpdates series numbers;
- a generic `User-Agent: MangaPixer-Metadata`.

Never sent: file paths, your file list, user accounts, reading progress, cookies, or anything that identifies your server. MangaUpdates sees your server's IP address, as with any web request. Readers' browsers never contact MangaUpdates: covers are stored on your server and served by MangaPixer. Logs record ids, counts, status codes and timings, never search text or titles.

MangaPixer is polite to MangaUpdates: at most 2 requests per second (5 per second for cover images), a daily request budget, and when MangaUpdates asks it to slow down it waits (up to an hour) before trying again. Search results are kept in memory for an hour, so repeating a search sends nothing.

Series data is provided by [MangaUpdates](https://www.mangaupdates.com) as-is and is credited to it wherever it is shown.

## Admin settings

**MangaPixer Administration** > **Series metadata**:

- **Show series information**: hides all series information (from files and from the web) for everyone when off. The data stays stored. Each library has its own **Show** switch too.
- **Fetch from the web**: off by default. It can only be turned on after ticking the box under the consent text that explains what is sent. Turning it on sends nothing by itself.
- The status line shows the requests used today, whether MangaUpdates asked MangaPixer to wait, and the last error.
- **Daily request budget**: a whole number, 5000 by default. Every search, series fetch and cover image counts one; the count resets at 00:00 UTC.
- Per library: **Fetch**, **Show**, **Precedence**, and **Delete fetched data** (removes that library's web links and the stored series and covers no other library uses).
- **Delete all fetched web data** removes every web link, stored series and cover. Don't-match marks and ComicInfo information stay.
- "ComicInfo: X of Y archives read" shows how far the background ComicInfo read has come.

To make sure the server never contacts MangaUpdates, whatever is set in the app, set `Metadata__NetworkDisabled=true` (see [Configuration](configuration.md#settings-stored-in-the-app)).
