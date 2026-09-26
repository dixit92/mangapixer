# Series information

MangaPixer can show a series summary (title, description, authors, genres, publication status, cover art) for your folders and archives. The information comes from two places:

- **ComicInfo.xml** inside your archives. It is read when an archive is analysed; nothing leaves your server.
- **MangaUpdates**, only if an admin allows it. An admin *identifies* a folder (or a single archive) with a MangaUpdates series; MangaPixer then fetches that series' details and cover once and stores them on your server.

Folders and archives with information show an **(i)** in the cover's bottom-left corner. It opens a side panel (a bottom sheet on a phone); **Open series page** leads to the full page. A folder that holds several series (an anthology or an author's folder, for example) lists them in the panel instead, without a series page. Inside a series folder, **Series info** in the top bar opens the same panel; an admin inside a folder without information sees **Identify…** there instead (when web lookups are on for the library). The (i) and the top-bar button update as soon as an admin links, unlinks or marks a folder, without reloading the page.

On the series page the description appears once, under **About**. Genres are shown as text. The precedence line under **Sources** appears only when both MangaUpdates data and ComicInfo exist. A series page for a single archive offers **Read** (or **Continue reading**) and **Show in folder**.

## Where the information applies

A link made on a folder applies to the folder and everything inside it, so every chapter in a series folder shows the series. An archive can also be linked on its own (a one-shot in an artist folder, for example).

- **Don't match** marks a folder or archive as "not one series": nothing is inherited from above. Use it on anthology, magazine and artist folders, then link the right subfolders or archives individually.
- **Source precedence** decides which source wins when both have a value: **Web first** (the default) or **ComicInfo first**, per library or per folder. Chapter-level details (number, volume, chapter title) always come from ComicInfo.

These actions are in the panel's and series page's **Admin** menu, and in the **Series** menu of the browse selection bar (hidden while **Show series information** is off for that library or globally). The same menus set a folder's [Content](#folder-content).

## Identify a series (admins)

Before you can look anything up, turn on **Fetch series information from the web** (see [Admin settings](#admin-settings)) and the **Fetch** switch of the library. Folders waiting in the [Review](#review) list open the same dialog from **Identify…**.

1. Open **Identify…** from the panel's or series page's **Admin** menu, from **Identify…** in the top bar inside a folder without information, or select one folder in browse and choose **Series** > **Identify…**. When lookups are off, the menu item and the dialog say why.
2. Search: the box is filled with a suggestion (the cleaned folder name, an English title in trailing square brackets such as `[Delicious in Dungeon]`, or the ComicInfo series). Edit it if needed and press **Search**. Only this text is sent. **Hide doujinshi & novels** (on by default) leaves doujinshi, novels, artbooks and drama CDs out of the results.
   - Or paste a MangaUpdates series address (`https://www.mangaupdates.com/series/njeqwry/berserk`) or a shortcode (`mu:51239621230`, `mu:njeqwry`) and press **Look up**. MangaPixer reads the series number from what you paste, so only that number is sent. Old-style `series.html?id=` addresses do not work: open the series on MangaUpdates and copy its current address.
   - If an archive's ComicInfo already points to a MangaUpdates series, **ComicInfo points to a MangaUpdates series – Use it** previews it directly.
3. Results are ranked by how closely they match the folder name (**Strong**, **Possible**, **Weak**). "matched as" shows the alternative title MangaUpdates matched. **Preview** opens a series.
4. The preview shows your folder next to the MangaUpdates record: item count, the ComicInfo series, whether the pages are tall strips, and MangaUpdates' type (Manga, Manhwa, …), year, volumes, authors and genres ("MangaUpdates: webtoon" when its users tag it so). Warnings point out likely mistakes, for example a novel instead of the comic, a different year, or far more items than the record has volumes or chapters.
5. **Link** stores the series and its cover and applies it. **Undo** in the message that follows restores what was there before.

**Refresh from MangaUpdates** in the **Admin** menu fetches the linked series again (the page shows how long ago it was fetched). **Unlink** removes a link; inheritance from above resumes. Nothing is refreshed or matched automatically unless an admin turns on [Automatic matching](#automatic-matching).

## Folder Content

Automatic matching leaves doujinshi out of its searches, because they otherwise crowd out the series you are looking for. If a folder does hold doujinshi or adult one-shots, tell MangaPixer: **Admin** > **Content** in the panel or on the series page (folders only), or **Series** > **Content** in the selection bar for several folders at once:

- **Auto** (the default): automatic matching leaves doujinshi out.
- **Doujinshi & adult one-shots**: automatic matching also searches doujinshi in this folder and everything below it.
- **Not doujinshi**: never search doujinshi here, even inside a folder marked **Doujinshi & adult one-shots**.

Like the folder reading direction, the value applies to everything below the folder until a subfolder sets its own; the menu says whether it is **set here**, **inherited** or the **default**, and **Inherit (clear)** removes a folder's own value. When MangaPixer's folder check thinks a folder looks like doujinshi (from its name), the menu marks that choice **suggested**; it never applies it by itself. Content only changes what automatic matching searches for; the Identify dialog has its own **Hide doujinshi & novels** box.

## Reporting a wrong series

Anyone who can see a series' MangaUpdates information can tell the admins it is wrong: **Wrong series?** in the series panel or on the series page. Choose what is wrong (a different series; right series, wrong details; the folder is not one series; something else) and, if you like, add a note of up to 500 characters. Only admins see the report and the note. Nothing changes for anyone until an admin looks at it.

After sending, the panel shows **You reported this**; once an admin has dealt with it, **Reviewed** (you can report again if it is still wrong). You can have one open report per series, and a daily number of reports that grows with the size of the libraries you can see; the dialog says when you have reached it.

## What is sent

Only when an admin presses **Search**, **Look up**, **Preview**, **Link** or **Refresh** in an enabled library, and only to `api.mangaupdates.com` (search and series details) and `cdn.mangaupdates.com` (cover images):

- the search text you confirmed;
- with **Hide doujinshi & novels** ticked, the fixed list of types to leave out (`Doujinshi`, `Novel`, `Artbook`, `Drama CD`);
- MangaUpdates series numbers;
- a generic `User-Agent: MangaPixer-Metadata`.

Never sent: file paths, your file list, user accounts, reading progress, cookies, or anything that identifies your server. MangaUpdates sees your server's IP address, as with any web request. Readers' browsers never contact MangaUpdates: covers are stored on your server and served by MangaPixer. Logs record ids, counts, status codes and timings, never search text or titles.

MangaPixer is polite to MangaUpdates: at most 2 requests per second (5 per second for cover images), a daily request budget, and when MangaUpdates asks it to slow down it waits (up to an hour) before trying again. Search results are kept in memory for an hour, so repeating a search sends nothing.

Series data is provided by [MangaUpdates](https://www.mangaupdates.com) as-is and is credited to it wherever it is shown.

## Series metadata page (admins)

**MangaPixer Administration** shows a **Series metadata** tile: how many folders wait for review, open reports, requests used today against the budget, and whether automatic matching is on. It opens the **Series metadata** page (`/admin/metadata`), which has four tabs: **Settings**, **Review**, **Flags** and **Runs**. When folders wait for review or reports are open, the account menu shows their number on a badge and a **Series metadata** item that goes straight there.

### Admin settings

The **Settings** tab:

- **Show series information**: hides all series information (from files and from the web) for everyone when off. The data stays stored. Each library has its own **Show** switch too.
- **Fetch from the web**: off by default. It can only be turned on after ticking the box under the consent text that explains what is sent. Turning it on sends nothing by itself.
- **Daily request budget**: one budget for everything - Identify, automatic matching and background refresh. A whole number, 5000 by default; every search, series fetch and cover image counts one, and the count resets at 00:00 UTC. The bar and the status line show the requests used today, whether MangaUpdates asked MangaPixer to wait, and the last error. When the budget is spent, automatic work stops and Identify waits until the next day; raise the budget whenever you need more. There is no hidden reserve.
- **Automatic matching** (see below).
- Per library: **Fetch**, **Show**, **Precedence**, **Match now** (see below) and **Delete fetched data** (removes that library's web links and the stored series and covers no other library uses).
- **Advanced: matching thresholds** (see below).
- **Stored data**: "ComicInfo: X of Y archives read" shows how far the background ComicInfo read has come; **Delete all fetched web data** removes every web link, stored series and cover. Don't-match marks and ComicInfo information stay.

To make sure the server never contacts MangaUpdates, whatever is set in the app, set `Metadata__NetworkDisabled=true` (see [Configuration](configuration.md#settings-stored-in-the-app)).

### Automatic matching

**Automatic matching** is one switch for the whole server, off by default. It needs **Fetch from the web**, and it can only be turned on after ticking the box under its own consent text, which explains that folder names are then sent without anyone reviewing them first (see [What is sent](#what-is-sent)). Turning it on sends nothing by itself; matching happens in the background, within the daily budget.

It applies to every library whose **Fetch** switch is on; the card lists them. Links it is sure about go live at once and appear under **Review** > **Auto-linked**; close calls wait under **Needs review**. Linked series are also refreshed in the background (every 30 days while a series is ongoing, every 90 days once it is complete). Turning the switch off stops both; links already made stay until you remove them.

**Match now** on a library queues all of its series folders at once. Before you start, it shows a local estimate (nothing is sent to get it): how many folders it will try, about how many requests that takes and how many days at the current budget, and how many are already linked. **Also retry folders that found no match before** includes earlier misses. The first time a library is matched, **Review everything once** keeps every result in **Needs review** instead of linking it, so you can check the matcher on your library before anything goes live.

**Advanced: matching thresholds** changes how sure the matcher must be:

- **Auto-link title score** (0.85-0.99, default 0.92): the top candidate's title must match at least this well to link on its own;
- **Lead over the runner-up** (0.05-0.30, default 0.10): and beat the second candidate by at least this much;
- **Review floor** (0.40-0.90, default 0.60, below the auto-link score): below this a folder counts as unmatched instead of waiting for review.

Higher numbers link less on their own and send more to review. Changes apply to the next matching; **Reset to defaults** restores all three.

### Review

The **Review** tab lists what the matcher did, with a count on each list and a library filter:

- **Needs review**: close calls. Each row shows the folder (or archive), where it is, why it is here (for example *Close second*: the runner-up scored almost as high; *Count*: your item count does not fit the record; *Year*, *Type*, *Related series*, *One-shot*, *Author*) and the stored candidates. Pick a candidate and **Accept**, or **Identify…** to search yourself, **Don't match**, or **Later** (moves the row to the end of the list).
- **Auto-linked**: links made automatically, newest first. **Confirm** keeps one (automatic matching never changes a confirmed link), **Change…** opens Identify, **Unlink** and **Don't match**.
- **Unmatched**: folders without a good candidate, mixed folders and loose archives, with the date of the next automatic try.
- **Don't match**, **Confirmed**: what is marked or linked by hand.
- **Missing folders**: links, precedence and reading defaults left on a folder that was renamed or moved where MangaPixer could not follow it. **Re-attach to…** opens a folder picker for the same library; **Delete** removes what was left.

Archives matched on their own are marked **Archive**; several archives matched together as one work are marked **Archive group**. Candidate covers are not shown until you expand a row (the arrow on the right, or `e`), because each cover is a request to MangaUpdates.

Select rows (the checkbox, `x`, or **Select all**) for bulk actions such as **Accept top candidates**, **Don't match** or **Re-run matching**. Every change shows a message with **Undo**; nothing is sent until that message closes, so **Undo** leaves everything as it was. Keyboard: `j` / `k` move between rows, `a` accept, `d` Don't match, `i` identify, `l` later, `c` confirm, `u` unlink, `x` select, `e` show covers.

On a phone each row is a card with the candidates as a list to choose from; tap a card and its actions appear in the bar at the bottom. Long-press a card to start selecting; the bottom bar then carries the bulk actions.

### Flags

Readers' [reports](#reporting-a-wrong-series). Open reports come first, and among them reports on automatic links. Each shows the folder, what it is linked to and whether that link was automatic or made by an admin, who reported it, the reason and the note. Resolve it with **Re-identify…** (opens Identify; the report is closed once you link), **Unlink**, **Don't match** (offered first when the reader said the folder is not one series) or **Dismiss**. **Resolved** and **All** list earlier reports.

### Runs

The **Runs** tab shows whether automatic matching is working or waiting, and why (switched off, budget spent, MangaUpdates asked it to slow down), how many folders are queued, the live run's progress with **Cancel**, and every run with its counts: folders tried, auto-linked, sent to review, unmatched, failed and requests used. **Auto links changed by an admin** counts the automatic links someone later changed, unlinked or marked Don't match - a measure of how well the matcher does on your library, kept on your server only.
