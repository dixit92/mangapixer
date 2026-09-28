# Series information

MangaPixer can show a series summary (title, description, authors, genres, publication status, cover art) for your folders and archives. The information comes from two places:

- **ComicInfo.xml** inside your archives. It is read when an archive is analyzed; nothing leaves your server.
- **MangaUpdates**, only if an admin allows it. An admin *identifies* a folder (or a single archive) with a MangaUpdates series; MangaPixer then fetches that series' details and cover once and stores them on your server. With **Automatic matching** on, MangaPixer also matches new series folders on its own (see [Automatic matching](#automatic-matching)).

Folders and archives with information show an **(i)** in the cover's bottom-left corner. It opens a side panel (a bottom sheet on a phone); **Open series page** leads to the full page. A folder that holds several series (an anthology or an author's folder, for example) lists them in the panel instead, without a series page. Inside a series folder, **Series info** in the top bar opens the same panel; an admin inside a folder without information sees **Identify…** there instead (when web lookups are on for the library). The (i) and the top-bar button update as soon as an admin links, unlinks or marks a folder, without reloading the page.

The (i) also appears on **Search** results and on the **Favorites** page (a favorites stack shows its folder's).

### Summary on hover

With a mouse or trackpad, rest the pointer for a moment on the cover, the title or the (i) of an item that shows the (i) - in the library's card and list views, in Search and on the Favorites page. A short summary appears beside it: the title, up to two alternative titles, the facts line, up to three genres and the first lines of the description. You can move the pointer into the summary; **More** under the description opens the series page. It closes when the pointer leaves, when you press Esc, click, or scroll. A click still opens the item, and the (i) still opens the full panel.

- Nothing is fetched while you just move the pointer across the library: the summary is loaded only after the pointer has rested on one item, from your own server (the same information the panel shows). Nothing is sent to MangaUpdates.
- It follows **Show series information**: when an admin hides series information for a library or everywhere, there is no (i) and no summary.
- Touch screens (phones, tablets) are not affected, and keyboard focus does not open it: use the (i) there.
- Turn it off in **Settings** > **Series information** > **Show series information on hover**. It is on by default and saved to your account.

On the series page the description appears once, under **About**. Genres are shown as text. The precedence line under **Sources** appears only when both MangaUpdates data and ComicInfo exist. A series page for a single archive offers **Read** (or **Continue reading**) and **Show in folder**.

## Where the information applies

A link made on a folder applies to the folder and everything inside it, so every chapter in a series folder shows the series. An archive can also be linked on its own (a one-shot in an artist folder, for example).

- **Don't match** marks a folder or archive as "not one series": nothing is inherited from above. Use it on anthology, magazine and artist folders, then link the right subfolders or archives individually.
- **Source precedence** decides which source wins when both have a value: **Web first** (the default) or **ComicInfo first**, per library or per folder. Chapter-level details (number, volume, chapter title) always come from ComicInfo.

These actions are in the panel's and series page's **Admin** menu, and in the **Series** menu of the browse selection bar (hidden while **Show series information** is off for that library or globally). The same menus set a folder's [Content](#folder-content).

## Search by alternative title

Once a folder or archive is linked to a series, **Search** also finds it by any of that series' alternative titles: a folder named `Dungeon Meshi` turns up for `Delicious in Dungeon`. These hits appear in their own **Series matches** row above the normal results (at most 20, on the first page of results), and each card adds a caption such as `aka Delicious in Dungeon` (the shortest matching title; hover for the full text). A folder that is also a normal name match is shown once, in **Series matches**.

- Only links that are in use count (**Confirmed** or automatic); folders marked **Don't match** and matches awaiting review never appear.
- The row follows the same access rules as the rest of search: libraries you cannot see, and Private libraries while Incognito is on, contribute nothing.
- When **Show series information** is off, globally or for a library, that library's alternative titles are not searchable.
- Searching runs entirely on your server; nothing is sent to MangaUpdates.

## Identify a series (admins)

Before you can look anything up, turn on **Fetch series information from the web** (see [Admin settings](#admin-settings)) and the **Fetch** switch of the library. Folders waiting in the [Review](#review) list open the same dialog from **Identify…**.

1. Open **Identify…** from the panel's or series page's **Admin** menu, from **Identify…** in the top bar inside a folder without information, or select one folder in browse and choose **Series** > **Identify…**. When lookups are off, the menu item and the dialog say why.
2. Search: the box is filled with a suggestion (the cleaned folder name, an English title in trailing square brackets such as `[Delicious in Dungeon]`, or the ComicInfo series). Edit it if needed and press **Search**. Only this text is sent. **Hide doujinshi & novels** (on by default, off below a folder whose [Content](#folder-content) is **Doujinshi & adult one-shots**) leaves doujinshi, novels, artbooks and drama CDs out of the results.
   - Or paste a MangaUpdates series address (`https://www.mangaupdates.com/series/njeqwry/berserk`) or a shortcode (`mu:51239621230`, `mu:njeqwry`) and press **Look up**. MangaPixer reads the series number from what you paste, so only that number is sent. Old-style `series.html?id=` addresses do not work: open the series on MangaUpdates and copy its current address.
   - If an archive's ComicInfo already points to a MangaUpdates series, **ComicInfo points to a MangaUpdates series – Use it** previews it directly.
3. Results are ranked by how closely they match the folder name (**Strong**, **Possible**, **Weak**). "matched as" shows the alternative title MangaUpdates matched. **Preview** opens a series.
4. The preview shows your folder next to the MangaUpdates record: item count, the ComicInfo series, whether the pages are tall strips, and MangaUpdates' type (Manga, Manhwa, …), year, volumes, authors and genres ("MangaUpdates: webtoon" when its users tag it so). Warnings point out likely mistakes, for example a novel instead of the comic, a different year, or far more items than the record has volumes or chapters.
5. **Link** stores the series and its cover and applies it. **Undo** in the message that follows restores what was there before.

**Refresh from MangaUpdates** in the **Admin** menu fetches the linked series again (the page shows how long ago it was fetched). **Unlink** removes a link; inheritance from above resumes. Nothing is refreshed or matched automatically unless an admin turns on [Automatic matching](#automatic-matching).

## Automatic matching

**Automatic matching** (see [Admin settings](#admin-settings)) is one switch for the whole server, off by default. It applies to every library whose **Fetch** switch is on. Turning it on sends nothing by itself; it needs its own consent, separate from the one for **Fetch from the web**.

- **What is matched.** After each scan, MangaPixer looks at the folders the scan found, and at folders that gained archives, and decides from their names and contents what each one is: a series (chapters or volumes of one work, also with `Volumes` / `Chapters` subfolders), a one-shot, or a collection of different works (an anthology, an artist's folder). Series folders are matched as a whole; in a collection folder each archive, or each numbered group such as `Title 1` / `Title 2`, is matched on its own. Archives lying loose next to other folders are matched one by one too (a one-shot, or a whole series kept in one archive), unless they look like volumes or chapters of one work. Category and container folders (`Manga`, an author's folder of series) are never matched themselves; the series inside them are. Folders that are unclear go to review and are never linked automatically.
- **Artist folders.** A folder is taken as an artist's folder, matched archive by archive, when it is named like the creator most of its archives carry (`[Artist] Title`, `[Circle (Artist)] Title`), or - without any tag - when it is named exactly like an author or artist of a series already linked in the same library (the name in either order, `Given Family` or `Family Given`). The second rule uses only the series information already stored on your server; nothing is sent to find it. It never applies to a folder whose own archives look like volumes or chapters of one work (a series whose title is also an author's pen name stays a series), to a folder of one archive, or to a folder that is linked itself. In an artist's folder, a record whose authors MangaUpdates lists links automatically only when that artist is one of them.
- **Chapters and names.** Numbered chapters with their own chapter titles (`Title 025 Chapter Name`) are one series. A name in brackets or parentheses anywhere in a folder or file name (`Title [Author]`, `Title [English Title] (Author)`, or `Author] Title` with a lone bracket), or next to a plain separator (`Title by Author`, `Title - Chapter 12 | Author`, `Author - Title`), is taken as a possible author: a record by that author, or whose MangaUpdates title carries it as `(AUTHOR Name)`, wins over records with the same title. It is only used as evidence, never on its own: a two-word name in brackets at the end (`Title [Two Words]`) can be an English title or an author, and it links a record automatically only when the folder's own name fits that record too.
- **Partial names.** A record titled `Title: Long Subtitle`, `Title ~Long Subtitle~` or `Title - Long Subtitle` is offered for review for a folder named `Title`, and so is a long title whose first three or more words are the folder's name, but none of these is linked automatically. When the archives carry the longer name, that name is searched second. A number that belongs to the title (`Title Level 99`) is not mistaken for a sequel number. When the right record is not on the first page of results - a one-word title, or nothing close enough - the second page of the same search is read too.
- **Counts.** A folder that holds far more volumes or chapters than the series has published goes to review. MangaPixer compares the highest volume or chapter number in the file names (so `12.5` chapters and extras do not add up) with the largest count MangaUpdates states: the latest chapter, the chapter total in the status (webtoons that restart their chapter numbers each season), and the English publishers' totals. A folder that mixes volumes and chapters is not compared.
- **Covers.** When two records tie on the title for a folder of volumes or a one-shot (a series and its anthology, or several series with the same name), MangaPixer compares their covers with the folder's own cover: it downloads the two candidates' cover images from MangaUpdates, compares each with the stored thumbnail of the folder's first archive, and gives a small preference to the record whose cover is the same picture. The comparison is made on your server; only the thumbnail MangaPixer already made is used on your side (no archive is opened for it), and the downloaded covers are deleted right after. A matching cover puts the right record first in **Needs review**; it never links a record on its own, never counts against a record, and is not used for chapter folders or webtoons (their first page is not a cover). The two downloads count in the daily budget like any other request. Covers are compared only when **Compare covers** is on, under [Automatic matching settings](#automatic-matching-settings).
- **Category folders.** A folder named exactly `Manga`, `Manhwa`, `Manhua` or `Webtoon(s)` above a series gives a small preference to records of that origin. It never counts against a record: a manhwa kept in a `Manga` folder is still linked.
- **What is never matched.** Anything with a link already (confirmed or automatic), anything inside a folder marked **Don't match**, and anything inside a linked series (a new chapter of a linked series is covered by its link, so it costs no request).
- **Confident matches go live at once** and are listed under **Auto-linked** so you can check them: **Confirm**, **Unlink**, **Identify…** or **Don't match**. Close calls go to **Needs review** with up to five stored candidates; nothing from them is shown to readers until you accept one. Works with no good match are listed under **Unmatched** and tried again after 30, 90 and 180 days, then not any more.
- **Match now** (per library) matches a whole library at once; see [Automatic matching settings](#automatic-matching-settings).
- **Pacing.** Automatic lookups send at most one request per second, so Identify stays responsive. A work costs at most four searches (a second page counts as one) plus a few record lookups, and only records that could match are looked up. Automatic lookups count in the same daily budget as everything else: when it is used up, automatic matching waits for the next day (00:00 UTC). Raise the budget if you want it to go faster. When MangaUpdates asks MangaPixer to slow down, automatic matching pauses too.
- **Doujinshi.** Automatic searches leave doujinshi, novels, artbooks and drama CDs out, like **Hide doujinshi & novels**; a folder's [Content](#folder-content) setting can allow doujinshi below it.
- **Thresholds** can be adjusted under [Automatic matching settings](#automatic-matching-settings).

What the matcher did - links it made, close calls, works without a match and folders it could not follow - is listed on the [Review tab](#review) of the Metadata Manager page.

### Renamed and moved folders

When a folder is renamed or moved, MangaPixer recognizes its archives at the new place and moves the folder's link or **Don't match**, its source precedence, its default reading mode and its **Content** setting to the new folder, as long as at least 80% of its archives went to the same new folder. Otherwise (a folder split in two, for example) the settings wait under **Missing folders**, where you can **Re-attach** them to a folder of the same library or delete them. Archives keep their links and reading progress when they move anyway.

### Background refresh

With **Automatic matching** on, linked series are refreshed from MangaUpdates by their series number: every 30 days while they are ongoing, every 90 days once complete, never when MangaUpdates no longer lists them, and at most 100 a day. Only the series number is sent. The cover is fetched again only when it changed.

## Folder Content

Automatic matching leaves doujinshi out of its searches, because they otherwise crowd out the series you are looking for. If a folder does hold doujinshi or adult one-shots, tell MangaPixer: **Admin** > **Content** in the panel or on the series page (folders only), or **Series** > **Content** in the selection bar for several folders at once:

- **Auto** (the default): automatic matching leaves doujinshi out.
- **Doujinshi & adult one-shots**: automatic matching also searches doujinshi in this folder and everything below it, and **Identify** starts with **Hide doujinshi & novels** unticked there.
- **Not doujinshi**: never search doujinshi here, even inside a folder marked **Doujinshi & adult one-shots**.

**Changing it matches again.** When a change allows or stops doujinshi below the folder (setting or clearing **Doujinshi & adult one-shots**), the items below it that are still in **Needs review** or **Unmatched** were searched with the other rule, so they are queued to match again and the message says how many. Linked and **Don't match** items are never touched, nor anything below a subfolder with its own Content. With more than 200 such items MangaPixer asks first (**Match again** in the message); with Automatic matching off it only tells you how many could be re-run from Review. For a tree such as `Doujins/Artists/<artist>/`, marking `Doujins` is enough.

Like the folder reading direction, the value applies to everything below the folder until a subfolder sets its own; the menu says whether it is **set here**, **inherited** or the **default**, and **Inherit (clear)** removes a folder's own value. When MangaPixer's folder check thinks a folder looks like doujinshi (from its name), the menu marks that choice **suggested**; it never applies it by itself. Content only changes what automatic matching searches for; the Identify dialog has its own **Hide doujinshi & novels** box.

## Reporting a wrong series

Anyone who can see a series' MangaUpdates information can tell the admins it is wrong: **Wrong series?** in the series panel or on the series page. Choose what is wrong (a different series; right series, wrong details; the folder is not one series; something else) and, if you like, add a note of up to 500 characters. Only admins see the report and the note. Nothing changes for anyone until an admin looks at it.

After sending, the panel shows **You reported this**; once an admin has dealt with it, **Reviewed** (you can report again if it is still wrong). You can have one open report per series and send up to 20 reports a day (more on large servers: 2% of the series you can see); the dialog says when you have reached the limit. A report is about the series, not the single archive.

Admins see the reports, with the note, under **Flags** on the [Metadata Manager page](#series-metadata-page-admins) and resolve them by relinking with **Re-identify…**, **Unlink**, **Don't match** or **Dismiss**; resolving applies to every open report on that series. Notes are never written to the logs.

## What is sent

Only to `api.mangaupdates.com` (search and series details) and `cdn.mangaupdates.com` (cover images), only in libraries whose **Fetch** switch is on, and only:

- when an admin presses **Search**, **Look up**, **Preview**, **Link**, **Accept** or **Refresh**: the search text you confirmed;
- with **Automatic matching** on: the cleaned name of each new series folder, or of an archive that is its own work - in a collection folder or loose next to other folders (for example "Series Title" from "Series Title [English Title]"), or the series name its ComicInfo agrees on - **sent without anyone reviewing it first**, which is why this needs its own consent. A MangaUpdates link in an archive's ComicInfo is used first, and then only its number is sent. Nothing inside a folder marked **Don't match** is ever looked up;
- with **Automatic matching** and **Compare covers** on, when two records tie on the title for a folder of volumes or a one-shot: the cover images of those two records, downloaded from `cdn.mangaupdates.com` by the address MangaUpdates gave (nothing from your library is sent with them);
- with **Hide doujinshi & novels** ticked, and always for automatic searches, the fixed list of types to leave out (`Doujinshi`, `Novel`, `Artbook`, `Drama CD`; without `Doujinshi` below a folder whose **Content** is **Doujinshi & adult one-shots**);
- MangaUpdates series numbers;
- a generic `User-Agent: MangaPixer-Metadata`.

Never sent: file paths, your file list, user accounts, reading progress, reports and their notes, cookies, or anything that identifies your server. MangaUpdates sees your server's IP address, as with any web request. Readers' browsers never contact MangaUpdates: covers are stored on your server and served by MangaPixer. Logs record IDs, counts, status codes and timings, never search text, folder names or titles.

MangaPixer is polite to MangaUpdates: at most 2 requests per second (5 per second for cover images), a daily request budget, and when MangaUpdates asks it to slow down it waits (up to an hour) before trying again. Search results are kept in memory for an hour, so repeating a search sends nothing.

Series data is provided by [MangaUpdates](https://www.mangaupdates.com) as-is and is credited to it wherever it is shown.

<a id="series-metadata-page-admins"></a>
## Metadata Manager page (admins)

*Renamed from "Series metadata" in 1.27.0; the route is still `/admin/metadata`.*

Admins open **Metadata Manager** from the account menu. Its own summary tile - to review, open reports, requests used today against the budget, and whether automatic matching is on - sits at the top of the page itself, above four tabs: **Settings**, **Review**, **Flags** and **Runs**; tapping a number in the tile switches straight to its tab. When folders wait for review or reports are open, the account icon shows their number on a badge, and the menu item shows the same number and opens **Review** (or **Flags** when only reports wait).

### Admin settings

The **Settings** tab:

- **Show series information**: hides all series information (from files and from the web) for everyone when off. The data stays stored. Each library has its own **Show** switch too.
- **Fetch from the web**: off by default. It can only be turned on after ticking the box under the consent text that explains what is sent. Turning it on sends nothing by itself. Once you have agreed, the text folds away; **What is sent?** next to the consent date shows it again.
- **Daily request budget**: one budget for everything - Identify, automatic matching and background refresh. A whole number, 5000 by default; every search, series fetch and cover image counts one, and the count resets at 00:00 UTC. The bar and the status line show the requests used today, whether MangaUpdates asked MangaPixer to wait, and the last error. When the budget is spent, automatic work stops and Identify waits until the next day; raise the budget whenever you need more.
- **Automatic matching**: off by default, one switch for all libraries whose **Fetch** is on. It can only be turned on after ticking the box under its own consent text (folder names are sent automatically, without review), and only while **Fetch from the web** is on (the consent text appears once it is). Like the first consent, it folds behind **What is sent?** once you have agreed. Turning it off stops new lookups; links it made stay until you remove them. See [Automatic matching](#automatic-matching).
- Per library: **Fetch**, **Show**, **Precedence**, **Match now** (see [Automatic matching settings](#automatic-matching-settings)) and **Delete fetched data** (removes that library's web links and the stored series and covers no other library uses).
- **Advanced: matching thresholds**: the three thresholds and **Reset to defaults** (see [Automatic matching settings](#automatic-matching-settings)).
- **Stored data**: "ComicInfo: X of Y archives read" shows how far the background ComicInfo read has come; **Delete all fetched web data** removes every web link, stored series and cover. Don't-match marks and ComicInfo information stay.

To make sure the server never contacts MangaUpdates, whatever is set in the app, set `Metadata__NetworkDisabled=true` (see [Configuration](configuration.md#settings-stored-in-the-app)).

### Automatic matching settings

The **Automatic matching** switch and its consent are described under [Admin settings](#admin-settings), what it does under [Automatic matching](#automatic-matching). Its card lists the libraries it applies to: every library whose **Fetch** switch is on.

**Match now** on a library queues all of its series folders at once. Before you start, it shows a local estimate (nothing is sent to get it): how many folders it will try, about how many requests that takes and how many days at the current budget, and how many are already linked. **Also retry folders that found no match before** includes earlier misses. The first time a library is matched, **Review everything once** keeps every result in **Needs review** instead of linking it, so you can check the matcher on your library before anything goes live.

**Advanced: matching thresholds** changes how sure the matcher must be:

- **Auto-link title score** (85-99%, default 92%): the top candidate's title must match at least this well to link on its own;
- **Lead over the runner-up** (5-30%, default 10%): and beat the second candidate by at least this much;
- **Review floor** (40-90%, default 60%, below the auto-link score): below this a folder counts as unmatched instead of waiting for review.

Higher numbers link less on their own and send more to review. Changes apply to the next matching; **Reset to defaults** restores all three.

### Review

The **Review** tab lists what the matcher did, with a count on each list and a library filter:

- **Needs review**: close calls. Each row shows the folder (or archive), where it is, why it is here (for example *Close second*: the runner-up scored almost as high; *Count*: your item count does not fit the record; *Year*, *Type*, *Related series*, *Author*) and the stored candidates. Pick a candidate and **Accept**, or **Identify…** to search yourself, or **Don't match**.
- **Auto-linked**: links made automatically, newest first. **Confirm** keeps one (automatic matching never changes a confirmed link), **Change…** opens Identify, **Unlink** and **Don't match**.
- **Unmatched**: works the matcher found no good candidate for, with the date of the next automatic try.
- **Don't match**, **Confirmed**: what is marked or linked by hand.
- **Missing folders**: links or **Don't match** marks, source precedence, reading defaults and **Content** left on a folder that was renamed or moved where MangaPixer could not follow it. **Re-attach to…** opens a folder picker for the same library; **Delete** removes what was left.

Archives matched on their own are marked **Archive**; several archives matched together as one work are marked **Archive group**. Candidate covers are not shown until you expand a row (the arrow on the right, or `e`), because each cover is a request to MangaUpdates.

Select rows (the checkbox, `x`, or **Select all**) for bulk actions such as **Accept top candidates**, **Don't match** or **Re-run matching**. Every change shows a message with **Undo**; nothing is sent until that message closes, so **Undo** leaves everything as it was. Keyboard: `j` / `k` move between rows, `a` accept, `d` Don't match, `i` identify, `c` confirm, `u` unlink, `x` select, `e` show covers.

On a phone each row is a card with the candidates as a list to choose from; tap a card and its actions appear in the bar at the bottom. Long-press a card to start selecting; the bottom bar then carries the bulk actions.

### Flags

Readers' [reports](#reporting-a-wrong-series). Open reports come first, and among them reports on automatic links. Each shows the folder, what it is linked to and whether that link was automatic or made by an admin, who reported it, the reason and the note. Resolve it with **Re-identify…** (opens Identify; the report is closed once you link), **Unlink**, **Don't match** (offered first when the reader said the folder is not one series) or **Dismiss**. **Resolved** and **All** list earlier reports.

### Runs

The **Runs** tab shows whether automatic matching is working or waiting, and why (switched off, budget spent, MangaUpdates asked it to slow down), how many folders are queued, the live run's progress with **Cancel**, and every run with its counts: folders tried, auto-linked, sent to review, unmatched, failed and requests used. **Auto links changed by an admin** counts the automatic links someone later changed, unlinked or marked Don't match - a measure of how well the matcher does on your library, kept on your server only.
