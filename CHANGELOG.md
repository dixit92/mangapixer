# Changelog

All notable changes to MangaPixer are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Dates are the release tag dates. Before 1.0.0 the project used an internal `0.1.0-dev.N` pre-release series that is not listed here.

## [Unreleased]

### Added

- **Search by words.** Search now finds every word you type, in any order: `piece one` finds `One Piece`. Upper and lower case, accents
  and punctuation no longer matter (`pokemon` finds `Pokémon Adventures`, `re zero` finds `Re:Zero`); put words in double quotes to keep
  them together and in order. See [Search](docs/library-layout.md#search).
- **Typos.** When few names match exactly, close matches follow the exact ones - one wrong, missing, extra or swapped letter in a word of 4
  to 7 letters, two from 8 letters - and the page says **Showing close matches for ...**.
- **One or two letters** now find the names with a word that starts with them (before, such a search silently found nothing), and the page
  says so.
- **Quick results while you type.** On desktop and tablet the top bar has a search box. Pause for a moment and a list opens with the best few
  **series**, **folders**, **archives**, **creators** and **tags** (with how many works carry each); **Up** / **Down** and **Enter** open one,
  **Enter** with nothing chosen opens the full **Search** page with what you typed, **Escape** closes the list, and **/** jumps into the box.
  On a phone the **Search** page shows the same list under its field. Typing a field such as `creator:ura`, `tag:`, `genre:`, `series:` or
  `library:` lists the values of that field instead, and choosing one completes it. Only what you can open is listed. See
  [Quick results](docs/library-layout.md#quick-results-while-you-type).
- **Tags.** The Info panel has a **Tags** row: the categories MangaUpdates lists for a linked series (the 20 with the most votes) and the
  ComicInfo `Tags` of its archives, in one row - spellings that differ only in case, spacing, `-` or `_` are one tag. A series shows its
  strongest tags first with **+N more**; a folder of many works shows the tags most of them share. Select a tag to find every work that
  carries it. The tag index is rebuilt once per library in the background after the update.
- **Tags that combine.** Opened from the Search page, an Info chip now adds its tag to that search instead of replacing it (your text,
  the read filter and the other tags stay). A tag in the search box can be switched to **Not** (works without it), and a new **Refine**
  list shows the tags of all the results with how many carry each - add one, accept either it **or** a tag already searched, or leave it
  out. Counts only include what you can open.
- **Typed search, complete and listed.** `tag:`, `series:`, `-` for not (`-genre:horror`), `|` for or (`genre:horror|mystery`),
  quotes for an exact value, `year:2010..2015`, and in any search `is:read` / `is:unread` / `is:reading` and `library:<name>`. A **?**
  next to the search box lists them with examples. A word with a colon MangaPixer does not know stays plain text.
- **Time chips under the date sorts.** *Recently added* and *Recently updated* now show a jump rail of **Today**, **This week**, **This month**,
  **This year** and one chip per older year (in your own time zone); a chip lands on its first entry. No rail when everything falls in one
  period.
- **Phones: a scrub strip for long lists.** Folders and libraries with 50 entries or more get a thin strip on the right edge. Slide a finger
  along it to run through the jump rail's chips - a bubble shows the letter or number range you are on - and lift to jump there. The picker
  button stays on every list, and shorter lists keep it alone.
- **Compressed database backups.** Scheduled and "Back up now" backups are now gzip-compressed (`rotating-<date>-<time>.db.gz`) - on a real
  library about a quarter of the plain size. Each backup is checked before it is kept: the server writes a plain copy in the data folder, runs
  an integrity check, compresses it, reads the compressed file back to compare it with the copy, and only then gives it its final name.
  Backups from earlier versions (`.db`) stay in the list, count towards "keep the newest" and can still be restored. The safety snapshots
  taken before an upgrade or a restore stay plain `.db`. Set `MangaPixer__Backups__Compress=false` to keep writing plain `.db` backups.
- **Restore accepts compressed backups.** "Restore from an uploaded file" and the restore API take a `.db` or a `.db.gz` (recognized by
  content). A compressed file is unpacked into the server's own working folder with the 512 MiB limit applied to the unpacked size, and a
  damaged or oversized file is refused before anything is staged. The Backups card marks compressed snapshots.
- API (additive): `GET /api/v1/search/suggest?q=` (the signed-in session only - an API token never reaches it); `GET /api/v1/search` adds
  `folderCount`, `archiveCount`, `closeMatchCount`, `insideCounts` and, in a tag search, `refine`; `GET /libraries/{id}/jump-index` accepts
  `readState`, `hideEmpty`, `favoritesOnly`, `completion`, `sort` and `tzOffset`, a bucket may carry `group` (`folders` / `files`), and `mode`
  may be `dates`.

### Changed

- **Search lists names, not paths.** Every folder and archive is still found by its own name, linked to series information or not. An archive
  that matched only because of the folder it sits in is no longer listed one by one: the folder's card says how many it holds, for example
  **+41 inside**.
- **Best matches first.** Results are ordered exact name, then names that start with your search, then names with a word that starts with
  each word, then the rest; folders before archives, then by name. The **Series matches** row is ordered the same way, and the count says
  **N folders, M archives**.
- **Starred results move to the top of the whole result list** (with highlighting starred results on in **Settings** > **Favorites**) -
  before, only within each page.
- In a tag search, text typed next to the tags also finds a work by its series' other titles and by its folder's name (before: only the
  work's title), every word in any order; `-word` leaves matching works out. `creator:urasawa` now finds "Naoki Urasawa" (a name with
  all the typed words; quotes keep the exact match).
- **The jump rail stays under filters.** With a read-state filter, *Hide empty folders*, *Favorites only* or a *Completion* answer on, the rail
  above the list now covers exactly the entries the filter keeps - with **Unread** on, its chips are the letters or number ranges of the
  unread entries, and a chip lands on the first of them. Before, the rail disappeared whenever a filter was on.
- **Doujin-style folders get a useful rail.** In a folder where most names open with a tag (`[Circle] Title`, `(Event) Title`) the chips show
  the first letter inside the tag with its bracket - `[A` ... `[Z` - instead of one `#` chip for the whole folder.
- **Folders and files each get their own letters.** At a level that holds both, the smaller kind gets its own chips once it has 20 or more
  entries (below that it keeps the single **Folders** / **Files** chip); the rail labels each kind.
- **Volumes view: chapter 1 joins volume 1.** When a series' volume list starts volume 1 at chapter 2-5 (an opening chapter listed without a
  volume), your chapters from 1 up to that first listed chapter now sit at the start of the **Volume 1** stack instead of on their own. A
  chapter 1 you do not have is still reported missing. See [Volumes](docs/volumes.md#how-chapters-are-placed-in-a-volume).
- **Small folders have no rail.** A level with fewer than about 12 entries (after any filter) no longer shows a rail of one-entry chips.
- **Lower memory use over long uptimes:** the server's shared in-memory cache now has a size budget, home "New chapters" data that can no
  longer be used is released, and failed sign-in attempts are forgotten once their lockout window has passed.
- **Much less memory held after heavy work:** the container image limits glibc's memory pools (`MALLOC_ARENA_MAX=2`) for the server and its
  helper processes - container memory under steady reading fell by more than 40 percent in our measurements, with unchanged page times. Set
  `MALLOC_ARENA_MAX=` (empty) to go back to glibc's default; see [Memory allocator](docs/configuration.md#memory-allocator).
- **Upgrade note:** the first start of this version rebuilds the search index once (a few seconds for a large library) before the server
  opens. No database migration. A "Load more" left open in a browser across the update starts again from the first page.
- **Docker Hub mirror:** each release image is also published as `dixit92/mangapixer` on Docker Hub (same tags as ghcr.io, which stays the
  primary registry), so Unraid Community Applications can show the app's **Last Update**.
- Angular 22.2.4 and .NET 10.0.14 (dependency updates).

## [1.48.0] - 2026-10-26

### Added

- **Reading state on favorites.** Archive cards carry **Read** / **Reading**, and a favorite folder - and a stack of favorites - carries Read or Reading for the whole folder, on the Favorites page and on the home Favorites row.
- **Home Favorites row:** its cards carry the (i) with the series summary on hover, and a star (turning it off takes the card out of the row).
- **Favorite a folder from inside it.** A star in a folder's top bar, right after **Info**, stars the folder you are viewing.
- **Settings > Library display:** the card size and list columns sliders, with a note that they apply to this device only.

### Changed

- **Card size and list columns per device.** They are now kept per browser or device instead of per account: a phone and a desktop can each keep their own. A device you have not set yet starts from the value you chose last somewhere else, and keeps it from then on; changing it on one device never moves another.
- **Vertical (webtoon) reading: bigger side taps.** The left and right edges of the screen now run the full height and are 20 % wide, so a tap anywhere along a side moves a screen (which edge goes forward is still **Edge taps**). The back and forward strips shrink to the top and bottom 15 % of the middle; the rest of the middle still shows or hides the controls (double tap: fullscreen). The reader's help overlay shows the new layout, and its card no longer covers the labels on phones.
- A favorite archive inside a linked series now shows that series' (i), like the Continue reading row.
- On the Favorites page the kind icon and a stack's count moved to the top-left of the cover, leaving the top-right for the read badge.
- New automatic tests check who may see what on every route: admin and operations pages refuse other users, routes that take a library, folder, item or series refuse one you have no access to, and the shared caches are checked with two users.
- **Incognito - library Info:** with Incognito on, the **Info** panel at the top level of one of your Private libraries is empty, like its browse, letter jump bar and rings. A folder's Info inside it still opens from a direct link.
- **Incognito - Tracking:** with Incognito on, the **Tracking** page, its chip counts and the account menu's Tracking badge leave out the series of your Private libraries.
- **Tracking:** a series in a library you can no longer open is no longer listed on the Tracking page or counted in its badge. Its tracking is kept: if you get the library back, the series is listed again as it was.
- SharpCompress 0.50.7 (dependency update).

### Fixed

- **Completion filter with "Show series information" off.** Where series information is hidden (for the library or for the whole server), the library's **Completion** filter and its counts are gone too - as the (i) and search already did - so they no longer tell which folders are linked series and whether each is complete.
- **Reader:** asking for the reader mode of an item in a library you have no access to now answers "not found", like any unknown item (it used to answer with that library's default reader mode).
- **Incognito:** your Private libraries no longer show the gold "read on <tracker>" rings or the letters of their jump bar while Incognito is on - their browse is empty, and now these are too.
- **Info panel - merged creators:** a creator chip no longer names the creator a merged creator was merged into when every work of that creator is in a library you cannot open (or in your Private library while Incognito is on); it shows the name your own works carry.
- **Private libraries:** marking a library Private that you cannot open (or that does not exist) is refused as "not found", and nothing in that request is saved. Settings sends only the libraries it lists.
- **Reading:** saving progress, a read mark, a bookmark or hiding an item from **Continue reading** for an item in a library you cannot open now answers "not found", like any unknown item (it used to answer "unauthorized").
- **Phone:** the **Info** button in a folder's top bar was a narrow oval with its icon off-centre, and the **Tracking** icon sat slightly left; both are now 40 px squares with the icon centred, and the **Chapters** / **Folders** switch is as tall as the buttons beside it.

## [1.47.0] - 2026-10-23

### Added

- **Creators in the metadata export.** Each item's `record` now lists its creators - one entry per person with `name`, `role` (`Author`, `Artist` or `Author, Artist`), the MangaUpdates author number (`muAuthorId`, `null` for MangaDex and Grand Comics Database credits) and `otherNames` (the pen names and other spellings MangaPixer stored after an admin ran **Artists' other names**; `[]` until then). A record without creators has no `creators` key, and every export page gains `recordFields` (`["creators"]`), so a client can tell "none known" from an older MangaPixer. Items without creators are unchanged; items with creators come back once from `updatedSince` after the update. See [Metadata export](docs/metadata-export.md#creators).
- **Info for a whole library.** At a library's top level the browse bar now has **Info** too: it shows the tags of every work in the library you can see, and a tag opens the search filtered by it, as on a folder. Series at the top level keep their own Info and actions.
- **"+N more" in the Info panel** now shows the rest of a row of tags in place (it was a plain count before).
- **Read filter in tag search.** The **Reading** / **Read** / **Unread** chips now also work when the search is filtered by a tag. A work counts as read when you have read everything of it you can see - for a series kept in several folders, all of them - and as reading when you have started it; Incognito leaves your Private libraries out, as everywhere.
- **Taken-down MangaDex records (admins).** **Metadata Manager** > **Runs** lists the MangaDex records that MangaDex no longer returns: how many, each record's title, the series here that use it, since when it has been missing, how many checks in a row missed it and when it was last found. It only shows what the new-chapter check already stored - nothing is requested, and no other site is asked for a replacement. See [Runs](docs/series-information.md#runs).

### Changed

- **Faster home and library pages.** Home's "New chapters" row is about 65 % faster (53 -> 19 ms on a 34k-item library copy): its covers are kept in memory until a cover, a cover setting, a series link, a poster or the library changes. Library pages and search are faster too (a library's top page 27-44 %, search 52-64 %, folders 16-24 %): every database connection now uses the same safe settings as the start-up connection (in-memory temporary tables, a 30-second lock wait, a 32 MiB page cache), and folder read badges, a folder's "Continue" archive and folder covers use cheaper queries with the same answers. Durability settings are unchanged: full sync, write-ahead log, foreign keys on, no memory-mapped I/O.
- Mark read on a large folder, a folder's Info panel, the review page's duplicate counts, the cover picker's series list and the latest-added dates after a scan use cheaper database queries (same answers).
- **Volumes view:** a loose chapter with a number is listed at its number between the volumes instead of after all of them (volume 9 ends at chapter 88.3, chapters 89 and 90 have no volume, volume 10 starts at 91: the two chapters now sit between the two volumes). Ordering only - no chapter is put in a volume. See [Volumes](docs/volumes.md).
- The chapter list of a webtoon (the **Chapters** view) shows the chapters without the file extension.
- **Artist reference:** a creator added in **Declared facts** now gets the MangaUpdates author your server already knows by that name (it used to happen only after a rename), and changing only the capitals (or word order, accents) of a declared artist's name keeps its reference. In **Match folders by name**, an artist matched from your own records is audited as `local_author_match` instead of `author_search`.
- **Reader:** the paged reader's **Display** menu sits in two columns on wide screens and uses closer items on short ones, so it fits a 720 px high window without scrolling inside.
- (Admins) A creator tag's **Rename** / **Merge into...** / **Split...** menu opens the creator directly, without looking it up by name first.
- ASP.NET Core / Entity Framework Core 10.0.13, SharpCompress 0.50.6, Angular 22.2.3 (dependency updates).

### Fixed

- **Jump rail:** after a chip near the end of the list, the highlight stays on the chip you clicked (it used to move to the range of the top row when the page could not scroll further).
- The admin diagnostics page and the diagnostics log export could rarely answer with an error while a new timing was first recorded at the same moment.

## [1.46.0] - 2026-10-21

### Changed

- **Library pages load much faster for readers who track series.** The gold "read on your tracker" rings of all the series cards on a page are worked out together instead of one series at a time (a library's top page went from over 900 database queries to under 70). The rings are unchanged.
- **The Completion filter answers at once after the first time.** Each folder's answers are kept until something they depend on changes (a scan, a link, a declared edition or tracking setting, a volume list, a stored record, the preferred language, ComicInfo, the volume contents scan) - and at most 10 minutes. The answers are unchanged, and working them out is lighter.
- **Much faster home "New chapters":** each library's recent stacks are kept until the library changes (a scan, a new or moved archive, a link change) and the read state is worked out only for the stacks shown; a new database index finds a library's recently added archives directly. Same stacks, same order, and each person still sees only their own libraries and read state.
- **Faster first visits after a restart or an update:** once the server answers, it prepares the library first pages, the home rows and the page encoder in the background, pausing whenever someone uses the server. It only reads and sends nothing anywhere; `MangaPixer:Warmup:Enabled=false` switches it off.
- **Smaller, cached web app downloads:** the web app's files are sent compressed (Brotli or gzip - the first load transfers about a third of the bytes) and its versioned files are cached by the browser for a year, so a revisit downloads only what a new version changed. API answers are unchanged.
- The "Continue" shortcut is worked out only for the first page of a list, and at a library's top level without walking the whole library; the library list remembers each library's file count until it is scanned again; search counts its matches and prepares its cards once per page instead of twice.
- The background cover check no longer writes to the database for covers that did not change (it sent one delete per chapter and per plain volume of every linked series after each restart, with nothing to delete).
- API note: `nextUnread` of a browse page is now null on any page after the first (a `cursor` or `before` request).

## [1.45.1] - 2026-10-20

### Fixed

- **The tag index no longer rewrites a library on every pass.** Works with nothing to index (a plain folder without a web record, ComicInfo or declared facts) were added and removed again each time the index was rebuilt; a rebuild with nothing new now writes nothing.
- **A refreshed web record rebuilds only the libraries that use it.** Storing or refreshing a record, and reading ComicInfo in the background, used to rebuild the tag index of every library; now only the libraries that link the record (or hold the archives) are rebuilt, a burst of changes gives one rebuild per library, and the periodic check no longer finds every library changed after any refresh.
- **Back from the reader is lighter.** The gold "read on your tracker" ring on the cards is re-read with one small request for the cards on the page, instead of loading the whole folder listing again (twice) - noticeable in long, scrolled folders.

## [1.45.0] - 2026-10-19

### Added

- **Info panel with clickable tags.** The browse button over a folder now reads **Info**. Its panel - and the series page - shows the folder's tags in rows: **Creators** (with "Story", "Art" or "Story & art"), **Series**, **Magazine**, **Publisher**, **Year started**, **Genres**, **Type** and, for works in a "Collection about" folder, **About** ("Fan works of <series>"). A tag shows how many works you can see carry it once that is 2 or more. On a folder that holds many works (an artist's folder, "One Shots") the panel shows the tags of the works below ("12 here"), the 12 most common per row and "+N more". A declared type or creator that disagrees with the web record is shown next to it in amber, marked as a conflict. Info also appears on folders without series information of their own when works below them have tags.
- **Tags count only what you can open.** Each tag's number counts the works in the libraries you may see - your Private libraries are left out while Incognito is on, and libraries with "Show series information" off add nothing.
- **Tag search.** Selecting a tag opens Search filtered by it: the tag sits in the search field as a chip you can remove, several tags narrow the results together, and you can also type `creator:"Name"`, `genre:`, `publisher:`, `magazine:`, `year:` or `type:`. The results are works - a series split over several folders or libraries is listed once, with "2 locations" to see where each part lives - and each card opens the folder (or, for a one-shot, the archive) it is in. The filter is part of the page address, so a reload or a shared link opens the same results.
- **A tag index of your libraries**, built in the background after links, refreshes, ComicInfo reads, declared facts and scans change - never while a library scans, and it waits while you read: works (a series counts once however many folders or libraries hold it; a one-shot archive is a work of its own), canonical creators (every spelling of an author, joined by the MangaUpdates author id, the author's other names and, for unlinked works, ComicInfo), magazines, publishers, start years, genres, types and the series a collection is about.
- **Admins: Rename, Merge into... and Split... a creator** from a creator tag's menu, for when two names are one person or one name is two - kept through every rebuild; a merged creator's links and bookmarks open the creator it was merged into. Merges and splits are in the audit log.
- **Find an artist on MangaUpdates.** An artist folder named after someone none of whose works you have linked yet can now be tied to that artist's MangaUpdates author: **Find on MangaUpdates** in the **Artist folder** dialog, or **Search MangaUpdates for the rest** in **Match folders by name** as artist folders (off until ticked; one folder per second, at most 50 per run). Each author found shows its name, the other name the search matched, the number of series, up to three genres and a link to its MangaUpdates page; nothing is marked until you pick one and press **Mark**, and marking sends nothing. Admins only; never automatic.
- **New chapters are noticed sooner.** With Automatic matching on, MangaPixer checks MangaDex for the latest uploaded chapter of your linked series every 6 hours (**Scheduled jobs** > Series information refresh > **Check for new chapters**: every 1 to 6 hours) - one request for every 100 series - and reads MangaUpdates' list of the day's releases four times a day; a series with a new chapter has its lists or its record read again right away.
- **Series you are reading are checked every 2 days**: a series with an archive marked read in the last two weeks, or Reading on a tracker.
- **Search: read status, a read filter and Load more.** Search results show the same **Read** / **Reading** marks as the library; a row of chips - **All**, **Reading**, **Read**, **Unread** - shows only those results (the count follows the filter); searches with more than 50 results show **Load more**. The search text, tag filters and read filter are part of the page address.
- **Metadata export:** `companions.mangadexLatestUpload` (`chapterId`, `fetchedAt` - the item changes only when the chapter does) and the page envelope's `sweeps.mangadexLatestUpload`, for MangaList's shared chapter signal.

### Changed

- **Fetch from the web asks for consent again (version 7).** MangaUpdates may now also be sent the search text you confirm when you look for an artist, as an author search, and the author numbers of the artists you pick; and, with Automatic matching on, MangaDex is asked for the latest uploaded chapter of your linked series (by their MangaDex ids only, 100 at a time) and MangaUpdates' public list of the day's releases is read (by page number only). Fetching pauses after the update until an admin accepts the new text; Automatic matching's consent is unchanged and resumes with it.
- **Following the pace, a series is checked at about half the time between its new chapters** (a monthly series every 15 days, a weekly one every 3 days) instead of at or after the next chapter; the series information refresh also runs every 4 hours between the daily runs for the series that have become due.
- **A check for new chapters that stops early** (daily budget, MangaDex asking to slow down, a switch turned off) still confirms every library whose series were all checked before it stopped. A MangaDex record that MangaDex no longer returns keeps its last latest chapter in the export; a series also linked to MangaUpdates keeps getting new-chapter refreshes from MangaUpdates.
- **Artist folders and their MangaUpdates author**: Match folders by name (as artist folders) saves the matched artist's MangaUpdates author number when only one known author has that name, and renaming a folder's declared artist links the new name to its author the same way - nothing is sent; otherwise **Find on MangaUpdates** still works.
- **An admin's Refresh counts like a scheduled one**: the change it sees is noted for the series' pace, and a new chapter makes the MangaDex lists due at once.
- **Administration > Libraries: several libraries scanning at once.** A scanning library is shown on its whole block - its row and its Auto-scan and Declared lines - with one moving line under all of them; the rings and lines of all scanning libraries move together, the ring sits centred on the library's icon, and while more than one library scans a line above the list says how many ("3 libraries scanning · Cancel all"). The "Scan all libraries" tooltip opens above the button.
- **Match now says when there is nothing to match**: "Nothing to match - every folder is linked or decided" (and, while folders that found no match before are left out, how many there are and that "Also retry" tries them again).
- **A series without a Volumes view says why**: its View menu shows "No Volumes view: no volume list is known for this series yet" when no volume list can place its chapters.
- The Info panel's credits and genres lines are replaced by the tag rows when tags exist (the hover summary keeps the short text).
- A filter that leaves nothing in a folder says so ("No series here match ..." with Show any, or "Nothing here matches the filter" with Clear filter) instead of "This folder is empty".
- The incomplete-volume badge ("8 of 9 ch") and the duplicates chip explain themselves in a tooltip; on a touch screen a tap on the badge shows the explanation instead of opening the volume.
- Metadata Manager > Review: the candidates' scores line up whether or not a candidate has an "Open on site" link, and a long-press on that link no longer starts select mode. The series Tracking popover takes the focus when it opens, so Tab walks its controls in order.
- Dependencies: Angular CDK / Material 22.2.3, Playwright 1.64.1.

### Fixed

- **The gold tracker ring appears without a reload after you come back from the reader** - also on earlier chapters a higher tracker number now covers.
- **A volume list no longer disappears when MangaDex briefly answers with none**: the last good list is kept while MangaDex still lists volume covers for the series (or its volume total is unchanged).
- Renaming or moving a series folder keeps its series link and settings even when a file had left the folder at an earlier scan: only files that disappeared together with the folder count against the "80% of the files moved" rule.
- Search results with the same name in several folders no longer skip results between pages.
- A library that starts scanning between two status updates can be cancelled (Cancel scan / Cancel all were greyed for it).
- MangaDex volume lists stored before 1.43.0 learn which chapters can be read there over the next days (at most 100 a day), instead of at their next regular check.
- The author search's text is never written to the log, not even at Debug level.

## [1.44.0] - 2026-10-17

### Added

- **Open the current match on MangaUpdates or MangaDex from Metadata Manager's review lists.** A linked series' title, and an icon beside each candidate in **Needs review**, open the series' page on its site in a new tab, without telling the site which address you came from; MangaPixer itself makes no request for it.
- **Find the chapters inside volume files.** When no volume list says which chapters a volume file holds, MangaPixer now reads the pages of the volume file and of the chapter files next to it, in the background, and compares them (another release of the same pages counts). The chapter files it finds join the volume's stack, and the volume's page says "Chapters 51-58 are inside the volume file"; when they fill the volume, it shows "Volume 8" instead of "~ Volume 8" and the volumes around it are placed from it. It never reads folders marked "Don't match", doujinshi folders or series, or webtoons listed as chapters, and never changes a volume a list maps. Files are only read; nothing leaves the server. The work follows "Background work while someone reads", and folders you open in the Volumes view go first.
- **Administration > Volume contents** has the switch "Find the chapters inside volume files" (on by default) and its counts; **Scheduled jobs** has a **Volume contents** row (hourly and after library scans). `MangaPixer:VolumeScan:Enabled=false` turns it off.
- **Metadata export:** a volume read from the files lists the chapter files inside it (`filesInside`, `sources: ["scan"]`; the proven range when the volume is known), and those chapter files are listed as `duplicates` with `inVolume`. Left out when there are none.
- **A Completion filter** in a library's **Filter** menu: pick **Have it all**, **Finished, missing some**, **Everything so far**, **Missing some** or **Can't tell** - the answers of the Completion tab - to see only the series folders with that answer; each answer shows how many series in the folder have it. It appears in folders that hold linked series, works for everyone who can open the folder, and combines with **Show**, **Hide empty folders** and **Favorites only**. While it is on, other folders and loose files are hidden and the **Filter** button shows the answer's icon.
- **Set *tracker*'s volumes to N.** When MyAnimeList's or MangaUpdates' own chapter count finishes more volumes than it lists (for example 271 chapters finish volume 27, but the list says 25), the Tracking page, the series' **Tracking** button and Select mode offer to raise the tracker's volume count to match, by the series' volume list from MangaDex or Wikipedia. Only on your click, only the volume count - never the chapters or the status - and only for a series you are reading or plan to read.
- **The series' Tracking button says why a folder is not synced** - in one of your Private libraries, doujinshi, or sync turned off for it - instead of "nothing read", and offers **Turn sync on** right there (the same setting as **Select** > **Sync** > **On**; nothing is sent by the switch itself).

### Changed

- **A running library scan shows on its row** in **Administration** > **Libraries**: a ring around its icon, a thin line along the bottom of the row, and **Scanning…** with **Cancel scan** in its line. The icons on the right stay where they are; **Scan now** is greyed out until the scan ends.
- **A volume's incomplete mark says "5 of 6 ch"** instead of "5/6", and its tooltip says how many chapters are missing.
- **On a phone, the Tracking page's select bar folds the reasons** why an action does not apply behind one "Why N actions are off" line; a tap opens them.
- **The media worker speaks protocol 6** (the new page-hash request); server and worker must be the same version, as always.
- **Magick.NET 15**, SharpCompress 0.50.5, Angular 22.2.2 and Playwright 1.64 (dependency updates; image hashes are unchanged).

### Fixed

- **Split chapters count toward your trackers.** A series whose newest chapters are split files (33.1 + 33.2 ... 37.1 + 37.2) counted only up to its last whole chapter, so the Tracking page said the tracker was ahead. A split chapter now counts once all its parts are read - for the sync, the gold ring, and **Set progress from *tracker* to MangaPixer**, which marks the parts too.
- **The gold ring shows on a virtual volume's own page** - the chapter cards inside a volume of the Volumes view now carry it like everywhere else.
- **The Tracking page never shows a total below what MangaPixer counts** ("130 of 129 ch" reads "130 ch").

## [1.43.2] - 2026-10-16

### Added

- **Select several series on the Tracking page.** Press **Select** above a tracker's list - in any chip, in a search or in **All** - and tick series (with a mouse, Shift-click ticks a range), or press **Select all *N* in this chip** to take every series in the chip, also the ones not shown yet. The bar at the bottom shows each action with how many of the selected series it applies to - **Set progress from *tracker* to MangaPixer**, **Set progress from MangaPixer to *tracker***, **Sync now**, **Don't sync**, **Sync again** - the chip's own action first, and says why an action applies to none. One confirmation with the counts, then MangaPixer works through them ("37 of 51..."; **Cancel** stops after the part it is on), doing for each series exactly what its own button does - never lowering a number on the tracker, never unmarking anything here - and sums it up in one line. It stops when today's request limit for the tracker is reached.
- **Open a series on MyAnimeList or MangaUpdates.** The tracker's numbers on the Tracking page and in a series' **Tracking** button, and **Open on *tracker*** in a row's **...** menu, open the series' page on the tracker in a new tab, without telling the tracker which server you came from. Shown when the tracker's number for the series is known.
- **Set today** beside each date in **Set dates...** fills in today's date where you are (not the UTC date). As before, the dates go to MyAnimeList with the next update.

### Fixed

- **A chapter 0 in a series' volume list no longer counts as missing.** MangaDex often lists a prologue, pilot or announcement as chapter 0, and many releases have none - a folder holding chapters 1-6 showed volume 1 as "5 of 6" with a "Ch. 0 - missing" placeholder. A listed chapter 0 now counts only when you have a file numbered 0; every other number keeps its rules.

### Removed

- **Send all** on the Tracking page: select all in **Waiting for you** and press **Set progress from MangaPixer to *tracker*** instead. For API clients: `POST /api/v1/trackers/{slug}/entries/send-all` is gone; `POST /api/v1/trackers/{slug}/entries/batch` and `GET /api/v1/trackers/{slug}/series/ids` are new (cookie sign-in only, as every tracker route).

## [1.43.1] - 2026-10-15

### Fixed

- **"Not on your list" for series MangaPixer could not look up.** Before the one-time MyAnimeList id refresh had run, **Check my list on MyAnimeList** marked every series you read here that had no MyAnimeList id yet as "Not on your list". Such a series now reads **No MyAnimeList id** (also the rows 1.43.0 stored that way), and only a series whose id is known can be "Not on your list".
- **The MyAnimeList ids arrive the same day.** The one-time refresh that learns them from MangaDex records now starts shortly after the server starts, instead of at the next nightly series information refresh - still only with automatic matching on, inside the daily budget and its pacing. Press **Check my list on MyAnimeList** again once they are in.

## [1.43.0] - 2026-10-15

### Added

- **Sync your reading progress to MyAnimeList and MangaUpdates.** An admin allows a tracker for the server in **Administration** > **Trackers** (after reading what is sent; MyAnimeList only on a personal, self-hosted server or a personal desktop install, with this server's own MyAnimeList app registration); each user then connects their own account on the new **Tracking** page. While you read, finishing or marking read a chapter or volume of a linked series updates your list: the series' tracker number, chapters and volumes read, Reading / Completed and, for MyAnimeList, the start and finish dates (filled in only where MyAnimeList has none, or the dates you set) - never titles or files. MangaPixer never lowers a number without asking, never touches a series you put on hold, dropped or completed there, never completes an ongoing series, and never syncs your Private libraries or doujinshi unless you turn sync on for that folder or series. A folder marked read all at once, and imported reading, wait for your **Send**. MangaPixer is the hub: each tracker follows MangaPixer, never another tracker, and nothing comes back from a tracker unless you ask. See [Tracker sync](docs/tracker-sync.md) and, for admins, [Tracker setup](docs/tracker-setup.md).
- **The Tracking page** (account menu > **Tracking**, with a count of the series that need you - a waiting jump, a tracker ahead, or a failed sync): connect MyAnimeList (you sign in on myanimelist.net; MangaPixer never sees your password) or MangaUpdates (username and password sent once, never stored), and every series you track per tracker - 50 a page, with filter chips and counts, a search, both sides' numbers, the dates on MyAnimeList (**Set dates...**) and two buttons: **Set progress from *tracker* to MangaPixer** (asks first; marks the chapters the tracker counts read here, never unmarks) and **Set progress from MangaPixer to *tracker*** (never lowers). A greyed-out button says why under it. The **...** menu has **Sync now**, **Set *tracker* to a number...** (after a warning, the only way to lower a number) and **Don't sync this series**.
- **Check my list on MyAnimeList** reads your whole MyAnimeList manga list (a page of up to 1000 entries per request) and adds every series here that is on it - also one you read there but never here, so you can bring it in. Entries for series that are not here are skipped and nothing about them is kept. For MangaUpdates it checks your series here, 50 at a time. It never changes anything on the tracker.
- **The gold ring.** A thin gold border around a cover (around the whole row in list view, and around a series folder when every file of the series here is counted) means every tracker that series syncs to counts it as read. A series' **Tracking** button, next to **Series info**, shows how it stands on each tracker and opens its row on the Tracking page.
- **Sync to trackers** in a library's selection: turn sync **On** for a folder or series (also inside a Private library, or for doujinshi), **Off**, or back to the default. A folder's setting covers everything inside it, and follows the folder when it moves.
- **Administration** > **Trackers**: allow MyAnimeList or MangaUpdates for the server, enter this server's MyAnimeList app registration (the redirect URL is filled in for you, with a Copy button; the client secret is never shown again), set the **requests per user per day** for each tracker (2000 by default), and see or disconnect users' connected accounts. Admins can also set a series' **MyAnimeList id** or **MangaUpdates id** from its Admin menu (the number or the series' address).
- MangaDex records now keep their **MyAnimeList id** (from the MangaDex answer MangaPixer already reads), so tracker sync knows a series' MyAnimeList entry; MangaDex records stored before this version are refreshed once to learn it, inside the metadata daily budget. `Trackers__NetworkDisabled=true` turns tracker sync off regardless of the admin setting.
- **MangaDex's readable chapters in the metadata export.** `companions.mangadexReleased` gains `readable`: the highest whole chapter that can actually be read on MangaDex in the preferred language, and how many whole chapters that is - `latestChapter` and `chapters` also count chapters MangaDex took down. To learn it, each read of a series' MangaDex volume list asks for the preferred-language list a second time without the taken-down chapters (one more request to the same address, with the same record number and language, inside the daily budget; none when nothing is released in that language). The field is left out until a series' list is next read, so older clients see no change; `schemaVersion` stays 1.

### Changed

- **"Official Volume Available"** replaces "Available in English" on the stack of a volume that is out officially in your preferred language while you have it as chapter files - the old wording read as if your copy were not in that language. The tooltip names the language ("Out as an official English volume - you have it as chapters"), and the status line, the Missing report and Official releases say "Volume 15 out as an official English volume".

### Fixed

- **A one-shot now counts for tracker sync.** A series that is a single file without a chapter or volume number counts as chapter 1 and volume 1: **Set progress from MyAnimeList / MangaUpdates** marks it read, reading it sends 1 chapter / 1 volume, and its card shows the gold ring.
- **Chapter extras next to a split chapter are no longer read as missing parts.** In a folder holding a chapter in parts (`6.1`, `6.2`) and its extras (`6.5` "Extras", `6.6` "Bonus"), `6.6` counted as a part, so the Volumes view showed 6.3, 6.4 - and the extra 6.5 you have - as missing, and tracker sync never counted chapter 6 as read. Now `.5` and every number above it continue a chapter's parts only after `.4`, and a number you have is never missing; a real gap between parts (4.1 and 4.3 here: 4.2) is still shown. MangaList reads split chapters the same way.
- **Enhance no longer shows a black block over a page.** On some devices (seen on iPad over HTTPS, where Enhance runs on WebGPU) the graphics chip now and then returned a black picture for a page or for part of a vertical-mode page, which covered the page until you reloaded. MangaPixer now checks each enhanced picture before it shows it: when it comes back black while the page is not, the page stays as it is, Enhance tries once more, and after a second black result that page (or that part of it) is shown without Enhance. The Upscaling status line counts how many such pictures were caught since the reader was opened (for example "GPU: WebGPU ready, WebGL2 ready - 2 blank frames caught"); nothing is stored or sent. A genuinely black page is still enhanced as before.

## [1.42.0] - 2026-10-12

### Added

- **Performance settings for administrators.** A new **Performance** card in Administration sets the number of media workers (by default a quarter of the server's processors, 2 to 4), the page cache size (1 GB by default) and what background work - analysing new archives, cover thumbnails, cover look-ups - may do while someone reads: **Pause**, **Slow** (default, one worker) or **Normal**. Changes apply at once, without a restart. A value set in the server configuration wins and is shown as such; the new key `MangaPixer:Media:BackgroundWhileReading` sets the background setting there.
- Administration diagnostics (`GET /api/v1/operations/diagnostics`, admin-only) report the media workers, the page cache and how long pages wait; page images carry a `Server-Timing` header (durations only).
- `scripts/perf/`: a synthetic reader load harness for measuring a test instance.
- **MangaDex's released chapters in the metadata export.** For a linked series whose MangaDex volume list was read in the preferred language, each export item now carries `companions.mangadexReleased`: the language, the highest chapter MangaDex lists as released in it, how many whole chapters that is, and when the list was read. It comes from the stored list - no extra request - and is left out when the list was not read, so older clients see no change. Useful for series whose MangaUpdates entry has no or an old latest chapter.
- **Series only on MangaDex.** A series MangaUpdates does not list yet can now be linked to its MangaDex title: choose **MangaDex** in Identify (search, or paste a `mangadex.org/title/...` address). The series then shows MangaDex's title, status, description, authors and cover, its latest chapter in your preferred language, and gets its volume list and volume covers from the same record. With Automatic matching on, a folder MangaUpdates finds nothing close for is also searched on MangaDex - what it finds waits for you under **Needs review** with a **MangaDex** tag; it is never linked on its own. Folders left unmatched earlier get this when you run **Match this library now** with **Also retry unmatched**. **Search MangaDex when MangaUpdates finds nothing** (Metadata Manager > Settings > Automatic matching, on by default) switches it off; `Metadata__AutoMatch__MangaDexFallback=false` switches it off regardless of the setting.
- **Switch to MangaUpdates** in the Admin menu of a series linked to MangaDex, once MangaDex links it to a MangaUpdates series: the links move to the MangaUpdates record and keep their confirmation; MangaDex stays its companion for volume covers and volume lists.
- Metadata export: `record.provider` can be `mangadex` (then `companions.mangadex` is the same id).
- **Estimated volumes in the metadata export.** When the Volumes view of a linked folder estimates a volume (`~ Volume 7` - a volume the list does not map), the export now adds it to the folder's volume list with `chaptersEstimated: true`: its chapters are the first and last chapter file of the folder the estimate places in that volume. A client such as MangaList can offer to replace those chapters with the volume file - for you to confirm, never on its own.

### Changed

- **Pages someone is reading always come first.** When every worker is busy, a page on screen is served before the pages the reader loads ahead, which come before background work. On a test library being analysed while two people read, opening an archive went from about 1.8 s to about 0.4 s with the new default of four workers on a 16-core server (about 1.0 s with two), and pauses on page turns disappeared.
- **The reader loads at most two pages ahead at a time**, and only once the page on screen is there, so that page no longer waits behind its own pre-loading.
- The default number of media workers now follows the processor count (2 to 4) instead of always 2.
- PNG, WebP and GIF pages are no longer decoded twice when they are prepared.
- Fetching from the web and Automatic matching ask for consent again: MangaDex can now receive the search text you confirm in Identify and, with Automatic matching, the cleaned name of a folder MangaUpdates found nothing close for.
- When MangaDex links a title found by automatic matching to a MangaUpdates series, that MangaUpdates series is proposed instead (read by its number).
- **More file names are read in the Volumes view and the Missing report.** When the files of a folder start with the same series title followed by a number that changes from file to file, that number is the chapter: `Series 100 Vol 11 Title` is chapter 100 of volume 11 (before, the whole file counted as volume 11), and `Series 002 Title`, `Series 014.5  Title, Part 2` or `Series 120 (2023) (Digital)` are chapters 2, 14.5 and 120. The number needs three digits (before a volume also `01` or `12.5`) and is never a year; a number every file shares is part of the title, so the files of a series like `Mob Psycho 100` or `Cyborg 009` stay volumes. In a `Volumes` folder such a number is a volume.
- **A site's watermark at the end of a file or folder name** - `Read Online`, `Read Manga Online Free`, `Online Free` - is left out when the name is read and from the name automatic matching and Identify search for. One such word alone stays part of the title (`Sword Art Online`).
- Placeholder group tags such as `[no group]`, `(Unknown)` or `(Unofficial)` are no longer taken for a creator or a doujin's parody.

### Fixed

- The page cache no longer keeps a record of every page it ever removed for as long as the server runs.
- A chapter number with three decimals (`Ch.014.125`) was read as the whole chapter (14), so it looked like a second copy of chapter 14; it is now chapter 14.125. A three-digit part padded like the chapter number is that part: `Ch.014.005` is chapter 14.5.

## [1.41.0] - 2026-10-11

### Added

- **A jump rail in every folder, in both name orders.** Sorted by Name with no filter, the jump rail (on phones, the picker button) now appears inside folders too, not only at the library root, and also in descending order, where it is reversed. A chip lands on the first entry of its group in that order. It stays hidden in the Volumes view. See [Jump navigation](docs/library-layout.md#jump-navigation).
- **Number ranges for numbered chapters and volumes.** When most names in a folder number their chapters or volumes - `Ch. 0012`, `Vol. 003`, `Series - 045`, or just `0000` to `0321` - the rail shows round ranges instead of a single `#` or one letter: `0`, `50`, `100` ... `300` for chapters 0 to 321, at most 12 ranges. The tooltip and the phone picker show the whole range (`50-99`); a part such as `10.5` is in the range of `10`; a one-shot or another entry without a number gets an **Other** chip where it appears in the list.
- **The rail ignores what the names have in common.** `Series - Alpha`, `Series - Beta` give `A` and `B`, not one `S`.
- **Series info in the reader.** When the open archive belongs to a series with series information, the reader's toolbar has a **Series info** button (on phones, a **Series info** tile in **Reader options**) that opens the series page. Your place in the archive is saved on the way.
- **Double tap for fullscreen.** Double tap (or double click) the center of the page to enter or leave fullscreen, in single, double and vertical mode. A single tap in the center still shows or hides the controls.
- **Edge taps in vertical mode.** With **Tap to scroll** on, the left and right edges of the middle of the screen (20% of the width each) now move a screen too; the top and bottom thirds still go back and forward across the full width, and the center still shows or hides the controls (double tap: fullscreen). Choose which edge goes forward under **Edge taps** in the **Display** menu (on phones, in **Reader options**): **Right goes forward** (the default) or **Left goes forward**. The choice is remembered by this browser and does not follow the series' reading direction; with Tap to scroll off it is greyed out.
- **Stacks per library on the home page.** **Settings** > **New Chapters** has a new **Stacks per library** choice - 6, 12 (the default, as before), 24 or 50 - for how many series stacks each library shows in the home page's **New chapters** row.
- **MangaDex under Sources.** When a linked series also has its MangaDex title (the one its volume covers and volume lists come from), the series page lists it under **Sources** ("Also on: MangaDex"), as a link that opens MangaDex in your browser only when you click it. Nothing is fetched to show it.
- **Reading mode suggestions.** A new page, **MangaPixer Administration** > **Libraries** > **Reading mode suggestions**, lists the folders that would read better in another mode, from what MangaPixer already knows about your files - nothing is looked up on the web. Folders whose pages are mostly long webtoon strips (at least twice as tall as wide) are suggested **Vertical (webtoon)**; folders whose ComicInfo mostly says right-to-left are suggested **Right-to-left**; a linked series adds a hint (a webtoon or a Korean or Chinese series: vertical; a Japanese series: right-to-left) that the folder's own pages or ComicInfo can overrule. Each row shows what was found, the mode the folder opens in now and the suggested one, with a confidence. **Apply** (one row, or **Apply selected**) sets the folder's reading direction for everyone - the same setting as **Direction** in the library view; nothing is applied on its own. Folders that already have a direction set by an admin (on them or on a folder above) are never suggested; where their pages disagree they are listed under **Set by an admin**. A linked series that keeps its volumes or chapters in subfolders is listed once. See [Reading mode suggestions](docs/reader.md#reading-mode-suggestions).

### Changed

- **The reader toolbar groups its options.** On desktop and tablet the toolbar now reads: previous / next archive, Series info, a **Bookmarks** menu (bookmark this page, or open the list - its icon is filled when the page is bookmarked), Favorite, a **Display** menu (layout, image fit or page width, reading direction, page transition or tap to scroll) and a **Quality** menu (upscaling, enhance quality, page quality, downscale filter), help and fullscreen. **Reader options** on phones is grouped the same way, under **Display** and **Quality**. Keyboard shortcuts are unchanged.
- **The sideways swipe in vertical mode follows Edge taps.** With **Right goes forward** you swipe left to go forward, as before; with **Left goes forward** you swipe right.
- **Every jump chip covers one unbroken stretch of the list, and the chips follow the list.** A name that starts with punctuation or a bracket (`[Group] ...`, `-Bonus`) is under `#`, where the list shows it, instead of under its first letter; in a folder with both subfolders and archives, the kind with more entries gets the rail and the other one a single **Folders** or **Files** chip at its place (before, a letter mixed folders and archives and its chip skipped the archives); script groups follow the list order. The phone jump picker closes when you pick a range or letter, and a jump on a phone no longer scrolls the entry under the top bar.
- **Volume ranges and re-released volumes keep what you read.** A file that collects several volumes (named `v01-03`, `Vol. 1-3` or `Volume 01-05`) and replaces their chapter files counts as read when you had read every chapter of every volume in it, and as in progress at its first page when you had read some; each volume can also be covered by the volume file it replaced. A volume file that replaces the same volume - a re-encoded or re-released `v03` - takes what you had done with the old one, also in a folder that is not linked to a series: read stays read, and a volume you were reading continues at the same page when both files have the same number of pages. The file names must name the same work and edition (scan-group tags, years and the like aside), two files of the same volume side by side are left alone, and a file with identical content stays a move. As in 1.40.0 this happens on the next scan or at startup, while the replaced files are still in the trash; it applies to future upgrades and to replaced volumes still in the trash.
- **API: jump index.** `GET /api/v1/libraries/{libraryId}/jump-index` accepts `parentId` (a folder of that library, else 404), `direction` (`asc` / `desc`) and `group` (the browse Volumes-view override) and declares its response schema; `JumpIndexDto` gains `parentId`, `direction`, `mode` (`letters` / `numbers`), each bucket `firstNodeId` and `range`. Buckets are now in list order.
- **API: home and series information.** `LibraryViewPreferencesDto` gains `homeStacksPerLibrary` (0 = unset = 12); `GET /api/v1/home/recent-chapters` without `perLibrary` now uses it (an explicit `perLibrary` still wins). `SeriesInfoDto` gains `companions` (provider, name and page of the linked series' MangaDex record; empty otherwise).
- **API (admin): reading mode suggestions.** `GET /api/v1/admin/reading-mode-suggestions` and `POST /api/v1/admin/reading-mode-suggestions/apply` (`items`: node ids and reader modes, at most 500). Applying is recorded in the audit trail (`reader_mode.suggestions.apply`, with the number of folders).
- Identify's "tall strips" fact, automatic matching and the new suggestions share one tall-strip rule; results are unchanged.
- **Upgrade note:** this version adds a database migration (`AddHomeStacksPerLibrary`: one column on the reader preferences; every existing user keeps 12 stacks). No new consent, no new kind of request, the matcher's rules are unchanged and the media worker protocol is unchanged.

### Fixed

- **A running series keeps its MangaDex volume list when the list is ahead of the series' total.** MangaPixer set aside a MangaDex volume list as "numbered differently" when it named more volumes than the series' volume total and than the volume covers MangaDex has - normal for a series that is still running, whose translated volumes run ahead of both. Such a series lost its whole list, so its Volumes view could place chapters only by the volume in their file names, and chapters without one stayed loose. The check now applies only to a series that has ended (complete or cancelled), whose total is final. Affected series get their list on their next refresh, or at once with **Refresh** in the series' **Admin** menu.

## [1.40.0] - 2026-10-10

### Added

- **Themes.** **Settings** > **Appearance** sets how MangaPixer looks for your account: **Dark** (the default, as before), **Light**, **Black** (true black for OLED screens, with cards and menus a step above black so they stay apart), **Sepia** (warm paper with brown text) or **System**, which follows your device's light or dark setting and switches with it while the app is open. Each choice shows a small preview, applies at once and follows you to every device; other users keep their own. The app opens straight in your theme (each browser keeps a copy of your last choice), and every page follows it - the reader too: its toolbars, menus and the area around the page take the theme's colours; the page itself is never changed. See [Appearance](docs/users-and-access.md#appearance).
- **Accent colour.** Pick the colour of buttons, selections, switches, tabs and highlights: Violet (as before), Blue, Teal, Green, Amber or Rose. Every accent is tuned separately for the dark themes (Dark, Black) and the light ones (Light, Sepia) so text in the accent colour stays readable; on Amber, text on accent-coloured buttons is dark.

### Changed

- **API: appearance.** `GET /api/v1/reading/preferences` returns `theme` and `accent`; new `PUT /api/v1/reading/preferences/appearance` (`theme`, `accent`, each optional; an unknown value is a 400). `PUT /api/v1/reading/preferences` ignores both fields, so a client that sends back the whole preferences object never resets a theme chosen meanwhile.
- **Upgrade note:** this version adds a database migration (`AddThemePreference`: two optional columns on the reader preferences; every existing user keeps Dark). No new consent, no new kind of request, and the matcher's rules are unchanged. The media worker protocol is unchanged (5).

### Fixed

- **A volume that replaces its chapters keeps what you read.** When a tool replaces a series' chapter files with the volume file that collects them (in the same folder), the volume now counts as read for everyone who had read every chapter the series' volume list gives for it (extras such as a chapter 9.5 are optional), and as in progress at its first page for those who had read some of them. It needs a series link with a stored volume list and happens on the next scan, or when the server starts, while the removed chapters are still in the trash. A volume someone has already marked read or started reading is left as it is, and one you mark unread afterwards stays unread. Upgrades made before this version are repaired the same way if their chapters are still in the trash. See [Rescans, moves and deletions](docs/library-layout.md#rescans-moves-and-deletions).
- **The automatic matching log named the wrong result.** For a work held in **Needs review** although it scored well enough to link (a review-first run, or a work that is always reviewed), the log line said "Auto"; it now names the result stored and, separately, the score.

## [1.39.0] - 2026-10-09

### Added

- **This folder's edition.** A folder that holds another edition of a series than the regular one - an omnibus, master or deluxe edition - can now say so: **Admin** > **Declared facts…** on a folder has a new section **This folder's edition** with **Volumes in this edition**, an optional **Edition** label (Regular, Omnibus, Master, Deluxe) and **Track completion**. With a volume count set, the folder's volumes are counted against that number (volumes 1 to N, as volume files) instead of the regular edition's volume list: the volume total, the missing volumes and the Completion answer (**Finished - you have it all (Omnibus edition)**) follow the edition, and no volume is offered as an upgrade. Chapter answers and the stacks of the Volumes view are unchanged. The settings belong to the folder itself - they are not inherited by the folders below it, cannot be set on a library and are never used for matching. See [This folder's edition](docs/declared-hints.md#this-folders-edition).
- **Stop tracking a series without unlinking it.** Turn **Track completion** off for a folder you keep linked but do not collect to the end. The link, the series information, covers and refreshes stay; the folder gets no Completion, missing or upgrade answer: it leaves the Missing report and the Completion tab (their summaries count it as "not tracked"), and its series line says **Completion not tracked**. **Don't match** is still there for a folder that should have no series at all.
- **The series poster as a cover choice.** **Choose cover…** on a folder linked to a series now also offers the series' stored poster as **Series poster**, under **Covers from the web** - also when no volume covers are stored. Choosing it sends nothing: the folder shows the poster of the series it is linked to (a re-link or a replaced poster follows by itself) and goes back to its automatic cover when the link is removed, the poster is not stored or web covers are hidden. A folder that is not a series itself (several series inside it) does not offer a poster. See [Choose a cover](docs/covers.md#choose-a-cover-admins). API: `CoverMode` gains `Poster`, `CoverOptionsDto` gains `poster`, and an admin picker preview `GET /api/v1/nodes/{nodeId}/cover-poster`.

### Changed

- **Metadata export: edition and tracking.** The Completion block gains `volumeTotalOverride` and `edition` for a folder with a declared edition, and an item whose completion tracking is off says `"tracking": "off"` and has no Completion block (`completion` is `null`). Each key is left out when it is not set, so items without them keep their form; `schemaVersion` stays 1. See [Completion](docs/metadata-export.md#completion).
- **API (admin): a folder's edition.** `PUT /api/v1/admin/metadata/folders/{nodeId}/declared/edition` (`SetDeclaredEditionRequest`: `volumeTotal`, `edition`, `tracking`); `DeclaredFactValuesDto` and `NodeDeclaredFactsDto` gain `edition`; `DELETE .../folders/{nodeId}/declared` also clears the edition facts. `SeriesProgressDto` gains `volumeTotalOverride`, `edition`, `trackingOff`; `CompletionBasis` gains `Edition`, `SeriesAnswerReason` gains `NotTracked`, `MissingTotalSource` gains `Declared`; the Missing report and Completion summaries gain `notTracked`.
- **Match folders by name: "Circle (Artist)" folders.** A folder named in the doujin style, `Circle (Artist)` or `[Circle (Artist)]`, is now also compared by each of its two names on its own, so it finds its artist when your stored series know either the circle or the artist. When both are known, the list shows the folder with both matches for you to pick. A year or a tag in brackets (`(2019)`, `(Digital)`) is not taken for a name, and collections are compared as before. Nothing is sent; automatic matching is unchanged.
- **Matching: an author's other name in brackets.** MangaUpdates writes an author's other name in brackets after the main name (`Main Name (Other Name)`). Automatic matching now knows the author by each of those names: an archive whose ComicInfo or file name gives the other name agrees with the record instead of being held in **Needs review** as a different author, and a folder named after the other name is recognised as that artist's folder. **Match folders by name** finds such an artist by either name too.
- **Upgrade note:** no database migration and no new consent. The matcher's rules change, so with **Automatic matching** on every work still waiting in **Needs review** is checked once more in the background (about two or three MangaUpdates requests each, inside the daily budget, at most one per second). Works already linked are not re-checked. The media worker protocol is unchanged (5).

### Fixed

- **A declared artist showed a false conflict with the series' own author.** MangaUpdates writes an author's other name in brackets after the main name (`Pen Name (Other Name)`), and the **Declared** line said **Conflict** when a folder declared that other name. A name now also matches the name before the brackets and each name inside them, and - once **Artists' other names** has been looked up - any of the author's other names stored on your server. Nothing is sent.
- **Duplicates that were not duplicates.** Files were listed as copies of the same chapter or volume whenever they stated the same number, so `Title 009 Vol 01 Name` next to `Title 001 Vol 01 Name` (a chapter written before its volume) and `Title Season 1 v01` next to `Title Season 2 v01` in one folder showed up as several copies of volume 1. Files now count as copies only when their names differ by tags alone - a scanlation group, a year, `(Digital)`, a copy marker - or by words, never by another number. This applies to the Missing report, the review list's duplicate counts, the Volumes view's stacks and the metadata export's `duplicates`, and matches MangaList's own duplicates finder. How file names are read is unchanged.

## [1.38.0] - 2026-10-09

### Added

- **Match folders by name.** When a folder holds many artists' folders, or collections named after their series, an admin can now mark them all at once: select the folders in the browse view (up to 200), then **Series** > **Match folders by name…**, and choose **As artist folders** or **As collections**. Each folder's name is compared only with what your server already stores - nothing is sent: as artist folders, with every name of the artists of your stored series records (any spelling, any name order, and - once fetched - their other names from MangaUpdates), declaring the artist's main MangaUpdates name; as collections, with the titles and other titles of your stored series records. A list with tick boxes shows what would happen before anything changes: a folder with one match is ticked, a folder with several asks you to pick one, a folder without a match is left alone (or, as an artist folder, marked with its own name if you tick that), and a folder that already has a link or a mark is shown with it, unticked. **Mark** marks each ticked folder exactly as **Artist folder…** or **Collection about…** does for one folder (a collection's series cover is downloaded when your server does not have it yet), and says per folder what happened. For collections without a match, **Search the web for the rest** (off until you tick it) shows the exact search text of each folder - editable, each with its own tick box - and sends only the ticked ones to MangaUpdates, like the Identify search: one per second, at most 50 per run, stopping when MangaUpdates asks to slow down or the daily budget is spent; the results are only offered for you to pick. See [Match folders by name](docs/series-information.md#match-folders-by-name).
- **Artists' other names.** Metadata Manager > Settings has a new **Artists' other names** card: MangaPixer can read the MangaUpdates author page of each creator of your linked series - pen names, other spellings, the name in Japanese, Korean or Chinese script - so **Match folders by name** recognises an artist's folder named with any of them. It runs only when you press **Look up the rest** (the card says how many requests and about how long): one request per second, counted in the daily budget, sending only the MangaUpdates author number; it stops when the budget is spent or MangaUpdates asks to slow down, and **Cancel** stops it - the next press continues where it stopped. An author is asked again only after 180 days. **Delete all fetched web data** removes the names.
- **The metadata export lists duplicate files.** A linked series folder's item now carries `duplicates`: each chapter or volume number that more than one file in the same folder states (the duplicates the Missing report shows), with every file's node id, name and unit subfolder - so an app such as MangaList can open each copy in the reader (`/reader/{nodeId}`) and let you judge which to keep. It is a new optional block, `include=duplicates`, on by default; the key is left out when a folder has no duplicates, so other items do not change. Read-only, no new route; `schemaVersion` stays 1. See [Metadata export](docs/metadata-export.md).

### Fixed

- **Tablet browse: the Volumes | Folders switch squeezed the folder trail to a letter.** At tablet width (about 820 px) the controls left the breadcrumb a letter or two. When the switch is shown, the trail now keeps its own room and the controls move to a second row; desktop and phone are unchanged.
- **Undo after Identify on an artist folder added the folder's name as a second artist.** When an artist folder's artist had been renamed in the dialog and Identify then linked the folder, **Undo** marked it an artist folder again with the folder's name, so the folder ended up with two declared artists. Undo now puts back the artist that was declared.

### Changed

- **The web-lookups consent text is now version 5.** It adds that MangaUpdates may be sent the author numbers of the creators of series you have stored (when you ask for artists' other names) and the names you confirm in Match folders by name. Fetching from the web - and with it Automatic matching - stays paused after the update until an admin accepts the new text (Metadata Manager > Settings); Automatic matching's own consent is unchanged.
- **THIRD-PARTY-NOTICES: the list of web build and test packages is regenerated** from a reproducible Linux install (it was a stale Windows snapshot); the shipped packages' sections are unchanged.

## [1.37.1] - 2026-10-09

### Fixed

- **Reviewing on a phone: the Undo message covered the review buttons.** After Accept, Later or another review action, the message with **Undo** popped up at the bottom of the screen, right over the bottom bar's buttons, so the next row had to wait until it went away. It now opens just above the bar; the buttons stay visible and tappable, and **Undo** is still within reach of your thumb. Desktop is unchanged.

## [1.37.0] - 2026-10-08

### Added

- **Artist folders.** An admin can now say "this folder is one artist's folder": **Artist folder…** in the **Admin** menu of a folder's panel or series page, in the browse **Series** menu (one folder selected), or on a folder row waiting in **Needs review** or **Unmatched** (key `r`; **Artist folders** in the bulk bar marks every selected folder with its own name). A small dialog asks for the artist - the folder's name by default - and the role (**Story & art** by default). In one step the folder is never linked to a series and nothing inside inherits a link from it; the artist becomes the folder's declared creator (its other declared facts stay), so a record by that artist counts in a work's favour - a hint, never a veto; the folder's own review row goes; and - unlike **Don't match** - automatic matching keeps working inside it, archive by archive: with **Automatic matching** on the works inside are queued at once, those that found nothing before included. Nothing new is sent: the artist is only compared on your server. The folder's panel says **Artist folder**; artist folders are listed on the **Collections** tab of the review, kept by **Delete fetched web data**, carried over when a folder is renamed or moved, and exported with the new link state `ArtistFolder`. **Remove artist folder** takes the mark off; the declared artist stays. See [Artist folders](docs/series-information.md#artist-folders).
- **Stories collected in one volume.** In a folder that is neither a series nor a collection - typically an artist's folder of one-shots - the stories that are each linked to the same collected volume (a tankoubon record) now show as **one stacked card** in the Volumes view, placed by the volume's title: the title, its stored cover and "3 stories". Open it to see the stories in folder order, each with its own cover, with their read state, star and series information; **Select** marks a whole stack read or adds it to the favorites. The usual **Volumes | Folders** switch shows every story on its own again. These stacks carry no volume number, completion or "missing" marks - the volume's contents list describes the book, not your folder. A new or changed link shows on the next visit, without a rescan. It works from stored data only; the browser never contacts a provider. See [Volumes](docs/volumes.md#stories-collected-in-one-volume).
- **Clear revoked API tokens.** A revoked or expired token used to stay in the **API tokens** list forever. When the list holds any, **Clear revoked** below it removes all revoked and expired tokens after a confirm; they no longer work, so nothing changes for the apps you use. Active tokens stay, and so do tokens paused because their admin is no longer an active admin. The **Audit trail** keeps each removed token's history and records the removal. See [API tokens](docs/api-tokens.md#clear-revoked-tokens).
- **Who started the last scan.** On the **Libraries** card each library's line now says who started its last scan: **by schedule**, **by admin** with the admin's name, or **by token** with the API token's name. Only admins see it. See [Library layout](docs/library-layout.md).

### Fixed

- **An artist's folder stuck in Needs review as one work.** A folder that waited in **Needs review** as a single work - queued before MangaPixer recognised it as an artist's folder, for example before a series by that artist was linked - stayed there when it was matched again, showing the **Artist folder** and **Review only** chips with one of its stories as the suggestion, and nothing inside it was ever looked up. Matching it again (by **Re-run matching**, or after an update) now matches its stories one by one, as for any artist's folder, and its folder-level review row goes away. **Upgrade note:** folders left in that state are checked once more after the update (a few searches, from the daily budget); other works waiting in Needs review are not checked again.
- **Moves: "Use old" now shows in the metadata export as a carry.** Resolving a series-link move conflict with **Use old** showed in the export as a removal plus a new item; it now shows the old item's id as `carriedFrom`, as for a folder carried over after a rename or move. See [Metadata export](docs/metadata-export.md).

## [1.36.0] - 2026-10-07

### Added

- **Mouse-wheel page turns.** In single and double page, scrolling the mouse wheel (or two fingers on a trackpad) down turns to the next page and up to the previous one, in reading order - also right-to-left, and a whole spread at a time in double page. One scroll turns one page: the rest of the same scroll, a trackpad's glide included, is ignored until the wheel stops for a moment, so a flick never skips pages and never opens the next archive by itself (at the end, a second, new scroll does, like a second key press). The wheel turns pages only while the page fits the screen: a page taller or wider than the screen (Fit width, Fit height, Original size, or zoomed) and the vertical view scroll as before. Sideways scrolling is ignored, and Ctrl (Cmd) + wheel and a trackpad pinch still zoom the browser. See [Reader](docs/reader.md).
- **Covers of the series inside a folder.** A folder that is not a series itself - for example a main series and its spin-offs side by side, or a folder marked **Don't match** or **Collection about** a series - can now show the cover of a series inside it: in its **Choose cover** picker, **Covers from the web** lists the stored covers of every series linked below it, one heading per series in the folder's order (up to 20), above the covers of the other items. Only covers MangaPixer already stored are offered (volume covers, including those of virtual volumes, and a series' main cover); choosing one sends nothing anywhere. A folder that is a series itself keeps its own covers, as before. See [Covers](docs/covers.md).
- **API tokens can ask for a library scan.** When you create an API token you now tick what it may do: **Read the metadata export** (as before, ticked by default) and the new **Request library scans**. A token with the scan permission can ask MangaPixer to scan a library (`POST /api/v1/export/libraries/{id}/scan`), so files that an app such as MangaList has just put into a library folder show up without waiting for the next automatic scan. It is always a full scan of the library, never of a single folder, and it changes nothing else; that address accepts only tokens, never a browser sign-in. A token may start one scan per library every 5 minutes (configurable with `MangaPixer__Security__ApiTokens__ScanCooldownMinutes`); while a scan of that library is running the answer is `409` with `Retry-After`. Each started scan is recorded in the **Audit trail**, and the API tokens list shows what each token may do. Existing tokens keep reading the export only. See [API tokens](docs/api-tokens.md#request-a-library-scan).

### Changed

- **Administration: the Audit trail is at the end of the page,** right before **Debug logging** - both are looked at when something went wrong, not on every visit. **Update check** moved up next to **Trash** and **Analytics**.

### Fixed

- **Double page opened with overlapping pages.** With **Upscaling** on **Crisp** (the default) or **Enhance**, a double-page spread could open as "three partial pages" until you paged away and back: when the second page arrived after the first, the first page moved aside but its upscaled copy stayed where the page had been. The upscaled copy now follows its page - also when the reader switches to double page after the first page is shown, or the window is resized - without re-rendering it.
- **Wikipedia volume lists stalled while Wikidata was busy.** MangaPixer finds a series' English "List of ... chapters" page through Wikidata and asked it to refuse when its servers lag more than 5 seconds. Wikidata counts its search index's lag in that figure, which stayed above 5 seconds for hours, so every lookup was refused; and because MangaPixer asked again after the 5 seconds Wikidata suggested, the same series was retried every minute, using a request from the daily budget each time, while no other series was looked up. The two Wikidata lookups now accept a lag of up to 30 seconds (Wikipedia pages keep 5), and when Wikimedia does ask to slow down MangaPixer waits longer each time (30 seconds, 2 minutes, 10 minutes, then an hour) instead of every few seconds.
- **The library sidebar reaches the bottom of the window.** On a long page (Home, a library), once the top bar scrolled out of view the sidebar ended a little above the bottom edge of the window and the page background showed below it.
- **Webtoon Enhance on a page that grows as it loads.** In the vertical view with **Enhance** on, when a page whose size the archive does not record finished loading and pushed the pages below it down, their enhanced copies stayed where the pages had been until the next scroll. They now move with their pages.

## [1.35.1] - 2026-10-07

### Fixed

- **Chapter pace right after the upgrade.** "Pace from: new chapters" waited for two observed rises of the latest chapter at least four weeks apart, and MangaPixer only started recording them in 1.32.0 - so no series had a chapter pace yet and most ongoing series fell back to their (slower) volume pace. Until two rises are seen, the chapter pace is now the average since the series began: the latest chapter MangaUpdates lists (which follows scanlation releases) over the time since its start year. It can only make a series look slower than it is. The series page names the pace that set the schedule ("a new chapter about every 4 weeks on average", or "a new volume about every 7 months").

## [1.35.0] - 2026-10-07

### Added

- **Refresh pace from new chapters.** **Check ongoing series** in the **Series information refresh** job now offers **Follow their pace** as one of its choices, next to **Every week**, **Every 2 weeks** and **Every month**, and with it **Pace from**: **Whichever is faster** (the default), **New chapters (scanlations)** or **New volumes (original release)**. The chapter pace comes from how the latest chapter MangaUpdates lists (which follows scanlation releases) rose at earlier refreshes - nothing extra is sent. A series whose pace is not known yet is checked every month. The series page says which pace set a series' schedule ("a new chapter about every week").

### Changed

- **Scheduled jobs, regrouped.** The library scans are one table - a row per library with its schedule, time, last and next scan - and the other jobs come in three groups (**Web information**, **Library upkeep**, **Maintenance**) as cards of one shape: what the job does, its settings or when it runs, and its last and next run at the bottom. Every setting uses the same compact control.
- **Following the pace is its own choice.** Before, the choice for ongoing series also limited the pace - with **Every week**, every ongoing series was checked weekly whatever its pace, so the pace switch changed almost nothing. **Upgrade note:** if the pace was on, it stays on and the choice for ongoing series is no longer read; pick **Every week** under **Check ongoing series** to check every ongoing series weekly. One database migration (`AddRefreshPaceSource`, one nullable column); no new consent; the media worker protocol is unchanged (5).

### Fixed

- **Volume covers of volumes listed as split chapters.** When MangaDex's volume list gives a volume only as split chapters (`41.1`, `41.2` ... `44.2`) and no file names the volume, its web cover was never downloaded - the parts counted as extras, so the volume did not count as held - and its stack showed the first page of its first chapter (often a scanlator's page). Such a volume now counts as held once all of its parts are here, and its cover follows; a part missing still keeps it partial. The series status line counts these volumes the same way.

## [1.34.2] - 2026-10-05

### Fixed

- **Dated doujin names.** Archives named `[Artist] [2023-05] Character (Tag) (Title)` - also without the opening bracket, `Artist] [2023-05] ...`, and with a `[2023]` or `[2023-05-17]` date - were searched by the character's name, and doujins about the same character were grouped as one work. When a bracketed date follows the artist tag directly, the last group in parentheses is now the title: it is what automatic matching searches (also as `<series> dj - <title>` inside a **Collection about** folder), what groups the works of a folder, and what review shows. The other groups are tags, never the parody; size and release tags such as `(x1600)` are never the title. The character is searched last, only when nothing confident was found, and a record found that way only goes to **Needs review**. These names now also count as doujin-shaped without the opening bracket, so a folder of them is suggested as **Doujinshi & adult one-shots** content. Names without the date, such as `[Artist] Title (Parody)`, are read as before.

### Changed

- **Upgrade note:** no database migration and no new consent. The matcher's rules change, so with **Automatic matching** on every work still waiting in **Needs review** is checked once more in the background (about two or three MangaUpdates requests each, inside the daily budget, at most one per second). Works already linked automatically are not re-checked: look at **Auto-linked** for doujins with dated names linked to a character's record. The media worker protocol is unchanged (5).

## [1.34.1] - 2026-10-04

### Added

- **Collection about… on any waiting folder.** In **Needs review** and **Unmatched**, every folder row now offers **Collection about…** (key `f`) - not only the folders the review suggests as a collection - and opens Identify in its "pick the series" mode. In a folder's normal **Identify**, **Set as collection instead** / **Link instead** switches between linking the folder and marking it a collection about the series you found.

### Changed

- **Volume titles in the Volumes view.** A volume file shown on its own is titled like the stacks around it - **Volume 3** (**Volume 3.5** for a fractional one) - instead of its file name with the extension. The **Folders** view still shows file names.

## [1.34.0] - 2026-10-04

### Added

- **Identify several items, one at a time.** Select two or more items in browse and choose **Series** > **Identify one by one...**: the Identify dialog steps through them ("1 of 4: Name") with **Link**, **Skip** and **Stop**, and ends with "Linked 3 · skipped 1". Each item gets the record you choose for it, so single archives inside a folder marked **Don't match** or **Collection about** can be matched without touching the rest.
- **Re-run matching from browse.** **Series** > **Re-run matching** queues the selected items for automatic matching again and says why any were not queued ("already linked - unlink first", "inside a folder that is linked, marked Don't match or waiting in review").
- **Same author / Same folder on Unmatched.** The chips, the group filter and the **Authors** list of Needs review (1.33.0) are now also on the **Unmatched** tab, so a folder of unmatched doujins can be listed by circle and re-run or marked **Don't match** together.
- **Collections about a series.** A folder of works *about* a series - fan-made doujinshi of a manga, for example - can be marked **Collection about** that series (folder **Admin** menu, or the browse **Series** menu: Identify opens in a "pick the series" mode). The folder shows the series' poster, description and genres as context, without its numbers; nothing inside inherits the series; and each work inside is matched on its own, searched also as `<series> dj - <title>`, the way MangaUpdates names doujinshi. Setting it also sets the folder's Content to **Doujinshi & adult one-shots** (a box in the dialog) and, with Automatic matching on, queues the works inside. It works inside a linked series folder too; nothing below a **Don't match** folder is ever looked up. Collections are listed on a new **Collections** tab of Metadata Manager, carried over on rename and move, and exported as `link.state: "CollectionAbout"` with the series as `record`.
- **Review suggests it.** A waiting folder whose files are doujin-shaped works by several circles and whose name matches a series reads **Looks like a collection about <Series>**, with **Accept as collection** as its first action (key `f`, also as a bulk action). The matcher never sets it by itself.
- **Webtoons, manhwa and manhua list their chapters.** A folder linked to a series that MangaUpdates marks as a webtoon, or to a manhwa or manhua, now shows its chapter files in chapter order in the series view instead of guessing volumes - unless a real volume list exists (two or more volumes on MangaDex, or a Wikipedia chapter list). The switch reads **Chapters | Folders**, no **Volume N - Missing** cards or estimated `~ Volume N` stacks are drawn, and the status line counts chapters ("You have chapters 1-120 · 2 chapters missing"), never volumes. File names that state their volume still group. The Missing report counts these series in chapters too.

### Changed

- **Upgrade note:** no database migration in this version; matching is unchanged (works waiting in **Needs review** are not checked again) and no new consent is needed. Series whose stored MangaDex volume list was nearly empty now count as having no volume list, so - with Automatic matching and **Volume covers from the web** on - their AniList totals may be asked once (an already-approved request), and webtoons / manhwa without a real list show chapters. Marking folders **Collection about** queues the works inside for matching (about two or three MangaUpdates requests each, inside the daily budget). The media worker protocol is unchanged (5).

### Fixed

- Identify now shows the error when the item it was opened for cannot be loaded (before, the dialog stayed empty).
- **Running-index file names.** Archives named with a running index in front of the chapter - `0003 [0001 - Chapter Title].cbz`, `0002 [0000.5].cbz` - are read by the number in the brackets (chapter 1; chapter 0.5, an extra). Before, the index was taken as the chapter, so every chapter was off and `.5` extras were lost. `001 [Chapter Title]` is still chapter 1.
- **Nearly empty MangaDex volume lists.** A MangaDex list with at most one real volume while most chapters are in no volume is now treated as no volume list: it no longer puts one chapter in "Volume 1" and shows the other volumes as missing. Wikipedia and (with Automatic matching) the AniList totals apply as for a series without a list. Lists already stored are read this way at once; nothing is fetched again.
- **Re-run matching** on a work inside a folder that is linked, marked **Don't match** or waiting in review as one work now says so ("covered_by_folder") instead of reporting success and doing nothing.

## [1.33.0] - 2026-10-03

### Added

- **Later in Needs review.** **Later** sets a work aside so you can accept the easy matches first: it moves to the end of **Needs review** with a **Later** tag, for every admin and after a reload, until the work is decided (accepted, identified, marked **Don't match**) or checked again by the matcher. **Not later** brings it back. Works set aside are listed oldest first after all the others; while any are set aside, **All**, **To review** and **Later** above the list choose what to show, with a count. Also a bulk action and the `l` key. See [Review](docs/series-information.md#review).
- **Same author and same folder in Needs review.** Doujins and artist collections often wait in groups: a row now says when other works waiting for review are by the same circle or artist - **4 more by Circle Name**, read from the leading tag of the works' own names (`[Circle (Artist)] Title`, also without the opening bracket; a circle and its artist count as one author) - or sit in the same folder. Tap it (or press `g`) to list them together, then **Select all** and a bulk action such as **Later** or **Don't match**. **Authors** above the list shows every author with two or more works waiting, largest first. Built from the names on your server only: nothing is looked up and matching is unchanged. See [Review](docs/series-information.md#review).
- **API tokens** for other apps (Administration > **API tokens**): create a token with a name and an expiry (30 days, 90 days, 1 year - the default - or never), copy it once (it is never shown again), see when each token was last used, and revoke it. A token can only read the new metadata export, nothing else; it works only while the admin who created it is an active admin. MangaList is the first app that uses one. `GET /api/v1/export/ping` lets an app check its token. See [API tokens](docs/api-tokens.md).
- **Metadata export for MangaList and other tools.** A read-only API (`GET /api/v1/export/libraries`, `GET /api/v1/export/metadata`) hands another program on your network what MangaPixer knows about your series: every folder or archive with its own link, the linked record, its MangaDex and AniList companions, the volume list with English release dates and ISBNs, the Completion answer and when the information is looked at again. Clients sync incrementally (`updatedSince`, with removals and the old id of a renamed folder) and page through large libraries. Names only - never a path - and calling it never contacts a website. See [Metadata export API](docs/metadata-export.md).
- **Official sources from MangaDex.** When MangaPixer reads a series' MangaDex record (as it already does for volume lists and covers), it now also keeps the official links that record lists - the publisher's pages and stores - and offers them in the export. No new request: they fill in as each series' MangaDex record is next read.

### Changed

- **A library's scan schedule is set in Scheduled jobs only.** The **Libraries** card shows it as one line ("Auto-scan: Daily at 03:00 (server time)") with a **Change in Scheduled jobs** link to the library's row, next to the last and next scan.

- **Upgrade note:** this version adds three small database migrations (`AddReviewLater`, `AddExportState`, `AddApiTokens`: one new column and five new tables only; a snapshot is taken before they run). No new consent is needed and matching is unchanged: works waiting in **Needs review** are not checked again. MangaDex official links fill in as each series' MangaDex record is next read (no extra request). The metadata export builds its first snapshot of a library on the first export request; every client's first incremental call is answered with a full sync. The media worker protocol is unchanged (5).

### Security

- API tokens are stored only as a SHA-256 fingerprint, never logged, and refused for anything but reading the export. Too many wrong tokens from one address are answered with 429 for 5 minutes; one token may make up to 600 requests a minute.

## [1.32.0] - 2026-10-02

### Added

- **Comics, graphic novels and BD albums are recognised from your files.** MangaPixer reads the usual comics naming - issue numbers (`#12`, `Issue 12`, `No. 12`), annuals and specials, album numbers (`Tome 3`, `T03`, `Band 3`, `Deel 3`), collected-edition words (`TPB`, `HC`, `OGN`, `Intégrale`) and a `(2012)` start year after the series name - and notes, from your files alone, whether a work looks like a Western comic: declared **Comic** / **Graphic novel**, a comics category folder, a Grand Comics Database / Comic Vine / Metron link or a Western publisher in `ComicInfo.xml`. Nothing is sent to work this out, and manga stays manga: `#12` or `Omnibus` alone means nothing, `No. 8` inside a title stays part of the title, and a folder declared manga, under a `Manga` folder or tagged as manga in `ComicInfo.xml` is never taken for a comic.
- **Comics category folders:** `Comic Books`, `Graphic Novels`, `BD`, `Bandes dessinées`, `Fumetti`, `Tebeos`, `Historietas`, `Stripboeken`, `US Comics`, `European Comics` and `Eurocomics` are category folders like `Manga`; the language ones prefer records from that country (French, Spanish, Italian, Dutch, English-language).
- **Comics from the Grand Comics Database.** **Identify** has a **Search on: MangaUpdates | Grand Comics Database** switch, starting on the Grand Comics Database for a folder declared Comic or Graphic novel or kept in a comics folder, and accepts a pasted `comics.org/series/...` address. With Automatic matching on, such folders are searched there first (with the start year from a name like `Bone (1991)`), and on MangaUpdates only when nothing close is found; a Grand Comics Database link in `ComicInfo.xml` is used directly. The year, language, shape (single issues or collected books) and `ComicInfo.xml` publisher help pick the right edition - new Review chips **Start year fits**, **Other language**, **Issues or books?** and **Publisher fits**. A linked series credits "Data: Grand Comics Database, CC BY-SA 4.0".
- **Volume lists completed from Wikipedia.** Where MangaDex's volume list has gaps - the newest volumes of a running series, chapters it leaves in no volume, or no list at all - MangaPixer can read the series' English Wikipedia "List of ... chapters" page (found through Wikidata by the MangaUpdates record number, or by the linked series' title) and fill the gap: the Volumes view, the Missing report and the volume covers then use exact volumes instead of estimates. MangaDex stays where it places a chapter; a page that fails its checks is never used. Each volume's English release date and ISBN are kept; the page is re-checked on the series' refresh schedule and downloaded again only when it changed; a small line credits Wikipedia where such a list is shown. In the background it needs **Automatic matching** with **Volume covers from the web**; Wikipedia can be removed from the allowed sites.
- **Grand Comics Database and Metron links** from `ComicInfo.xml` are shown in the series information panel, like Comic Vine links.
- **Scheduled jobs** in Administration: every job MangaPixer runs on its own, with its last and next run, in server time. Choose the hour for library scans (daily or weekly, and the weekday), backups, the cache clean-up and the series information refresh. Scheduled times survive restarts: a job that was due while the server was down runs once when it is back, and a restart no longer runs the refresh or the cache clean-up again.
- **Series information refresh, your way:** check ongoing series every week, every 2 weeks or every month (default), and finished series every month, every 3 months (default) or every 6 months. With **Follow each series' publishing pace** (on by default) series that publish quickly are checked more often, up to weekly, so a new volume shows up sooner; a series on hiatus or with nothing new for six months is checked like a finished one. The pace comes from what MangaPixer already stores - no new kind of request. Refreshes have no separate daily limit any more (it was 100): the daily request budget is the only cap.
- **The reader shows the open archive's name** (without its file extension) while the controls are visible, fullscreen included: next to the page counter in the top bar on wide screens, in a small row just below it on phones and narrow windows.
- **On phones the header's Libraries and Search links are icons** (with labels for screen readers and tooltips) instead of two squeezed words; the link of the current page is highlighted.
- **Reader: confirm before leaving an archive.** At the last page, the next tap, click, swipe or arrow key no longer opens the next archive at once: it names it ("Again to open the next archive: ...") and the same input again within 3 seconds opens it - the same at the first page for the previous archive, and at the very end of a webtoon for a tap, swipe or scroll key. Turning back a page cancels it. Holding an arrow key never leaves the archive, and scrolling a webtoon still never does.
- **Administration is laid out in a grid** like Metadata Manager: cards sit in two columns on a desktop and stack on a phone or tablet, and the scheduled jobs sit side by side, so the page no longer shows a wide empty margin.
- **Automatic trash cleaning is switched on in Scheduled jobs.** The switch and the hour moved from the Trash card to the "Empty trash and clean bundles" row, so each job's time is set in one place. The Trash card keeps the retention and the Empty trash / Clean bundles buttons and shows a one-line status with a link to that row.
- **Per-folder cover preference.** Select a folder and choose **Folder covers...** to make it show **Web covers when available** or **File covers** (each file's own cover; no web cover is shown, and none is downloaded for the series below). It applies to the folder and every folder below it - the nearest folder wins - and overrides the library's **Show saved web covers** switch in both directions; a cover chosen for one card still wins, and moving or renaming the folder keeps it.

### Changed

- **Series information: a new consent (version 4).** Two sites join the allowed sites - the **Grand Comics Database** (comics and graphic novels) and **Wikipedia** (which chapters make up each volume, for series already linked to MangaUpdates) - so fetching from the web, and Automatic matching, pause after the update until an admin reads and accepts the new texts in Metadata Manager. Either site can be removed from the allowed sites like the others.
- **Requests to the allowed sites name MangaPixer.** Their `User-Agent` is now `MangaPixer/<version> (+https://github.com/dixit92/mangapixer)` (was `MangaPixer-Metadata`) - the same on every server of a version, as MangaDex and Wikimedia ask; it does not identify your server.
- **Comics keep their own cover:** a folder linked to the Grand Comics Database never gets a cover from the web. The Grand Comics Database answers anonymous visitors about 25 requests an hour, so MangaPixer sends it at most that many and keeps two for Identify; when none is left, Identify says when to try again and automatic matching moves on (manga never wait behind a comic).
- **The daily request budget starts at midnight server time** (it was midnight UTC - 20:00 in New York); the day of the update counts once, so nothing extra is spent.
- **Completion** (was **Official releases**). The tab in Metadata Manager now gives every linked series one answer - **Finished - you have it all**, **Finished - missing some**, **Everything released so far**, **Missing some** or **Can't tell** - with one sentence that says why ("Ended in Japan, and every chapter is out in English: you have all 172 chapters."), so you can see which series in a library of ongoing series are ready to move to a library of finished ones. A series counts as finished only when it has ended where it comes from (complete or cancelled there); a finished English edition of a series that still runs is not the end. The edition - **Official**, **Official chapters** (an official chapter-by-chapter release such as MANGA Plus), **Chapter-based** or **Original run** - is shown next to the two Finished answers, and **Upgrade available** is a separate mark with an **Upgrades only** switch. Each answer shows how many series have it. Old links to the Official releases tab still open it.
- The completion mark on series pages, in the Missing report and in the Volumes view uses the same words: "finished - you have it all (Official)", "finished - missing some (Official, 12 of 14)".
- A folder of volume files is judged by the volume edition: while the English volumes are still coming, a series whose chapters are all out reads **Everything released so far**, not "missing some". A folder of volumes of a running series whose English volume total is not known yet says **Can't tell** instead of claiming it is up to date.
- A one-shot - one volume, ended - counts as held whole, also when its file name carries no number.
- For a preferred language other than English, a series is finished in that language's chapters when its released chapter list reaches the last chapter of the original run.
- **Matching rules revision 2:** after the update, works waiting in **Needs review** are checked once more under the new rules (comics naming, the Grand Comics Database, Webtoon folders, 1.31.1's "Episode N" rule) - paced, inside the daily budget; works you have linked, confirmed or marked "Don't match" are not touched.
- Series origins gain **Italian** and **Dutch**. A series cancelled at the source now has its web covers re-checked on the slower "finished" schedule (every 90 days), like its refresh.
- **Upgrade note:** this version adds three small database migrations (`AddWikipediaVolumeLists`, `AddJobSchedules`, `AddFolderCoverPreferences`: new tables and new columns only; a snapshot is taken before they run). **Series information needs a new consent (version 4)**: after the update, fetching from the web and Automatic matching pause until an admin reads and accepts the new texts in Metadata Manager (a banner points there). Once accepted, with Automatic matching on: the works waiting in **Needs review** are checked once more under matching rules revision 2 (about two or three MangaUpdates requests each); linked series stored before 1.30.0 read English publisher details are refreshed once; both run inside the daily budget at one request per second. The daily budget now starts at midnight server time (the update day is counted once). The media worker protocol is unchanged (5).

### Fixed

- Album numbers in European file names (`Tome 03`, `Band 03`, `T03`) are read as volumes in the Volumes view, the Missing report and automatic matching, and no longer end up in the searched title.
- A `Webtoon` / `Webtoons` category folder now counts as the hint it was meant to be (Korean or Chinese origin); it was compared in the wrong form and never did.
- A reading-progress save that raced another save of the same item could leave an error line in the server log, and marking an item or a folder read at the same moment as another request could fail with a server error. These races now recover quietly and keep the right state.
- Pages the reader asked for and then abandoned (turning pages quickly while the media worker was still starting) were logged as "busy" or "timed out" warnings and could stop the worker that was starting. An abandoned request is now simply cancelled, and the worker finishes starting for the next page.

## [1.31.1] - 2026-10-01

### Fixed

- **Moves of older archives are recognised again.** Archives first analyzed by a version before 1.5.0 had no content signature, so moving or renaming them - inside a library or to another one - lost their reading state. After the update a slow background pass reads 128 KiB of each such archive (source files are only read) and records its signature; from then on their moves are recognised like any other.
- **Re-attach to a folder in another library.** On the **Missing folders** tab, **Re-attach to…** now has a **Library** choice, so the settings of a series moved to another library can be re-attached there.
- **"Episode N" next to a volume is a part, not a chapter.** A file such as `Title - Episode 3 - Arc Title v01` is volume 1 of part 3; series split into arc folders whose volumes start again at 1 no longer borrow the series' chapter list (false "missing" chapters). `Episode 45` without a volume is still chapter 45.
- **The review shows your own cover.** In Metadata Manager's review rows, **Yours** is always the file's own page 1 (a folder: its first archive's), never the cover chosen or downloaded for the series - which could make both sides of the comparison the same picture.
- Two "First without OrderBy" warnings in the server log after each volume-cover pass.
- **Upgrade note:** no migration, no API change; the signature pass starts a few minutes after the update and logs how many archives it signed.

## [1.31.0] - 2026-10-01

### Added

- **Move series between libraries without losing anything.** Moving a series folder (or archives) from one library's folder to another's on disk now keeps every user's reading progress, read marks, bookmarks, favorites and reader settings, and the folder's series link, settings, cover choice and Volumes view setting - whichever library is scanned first. When the old library is scanned first, the new library's scan moves the existing items (nothing is analyzed again, a **Confirmed** link stays confirmed and no new automatic match is made). When the new library is scanned first, MangaPixer pairs each removed archive with its new copy once both are scanned and analyzed, and copies the state over wherever the new copy has none; an automatic link to the same series becomes **Confirmed**, and the moved copies keep their original **Added** date, so they do not show up as new chapters. State also moves for users who cannot open the new library right now; they see it as soon as they get access. Identical files that were in your libraries at the same time are never merged.
- **Move conflicts** (admins): when the new copy already had its own reading position or reader settings, or the new folder got a different series link, nothing is overwritten - the cases wait on the new **Move conflicts** page (linked from the Libraries card in Administration while any is open), where you choose **Use old** (the state from before the move) or **Keep new**, one by one, for a selection or for all.
- **Moves recognized during the trash retention window.** A removed archive is recognized at its new place - in any library, and also when it comes back to the same library at another path - for as long as the trash keeps it (Daily, Weekly, Monthly, Quarterly or Yearly; Monthly by default). Moves made before the upgrade, inside the window, are recognized on the first start.
- **Trash: Empty trash and Clean bundles.** Files and folders that disappear from a library now go to a trash for a while instead of staying in the database forever. A new **Trash** card in **MangaPixer Administration** shows what is in it per library, and two actions clean up, like Plex: **Empty trash now** removes items past the retention time together with everything stored for them (every user's progress, read marks, bookmarks, reader settings and favorites of them, the folder's series link and cover choice, their thumbnails and cover crops), and **Clean bundles now** removes files in the data folder that nothing uses any more. Both show what they will remove and ask before they do. Series information fetched from the web (records, posters, volume covers) is kept. See [Trash and clean-up](docs/trash.md).
- **Keep removed items for** Daily, Weekly, Monthly (the default), Quarterly or Yearly: one setting for how long a moved or renamed series is recognised (keeping its reading state) and how long removed items stay in the trash.
- **Turn automatic cleaning on** (off by default, also after the upgrade): empties the trash and cleans bundles once a day at the hour you choose in **Run automatic cleaning at** (04:00 server time by default). Turning it on first shows what the first run will remove.
- **Holds.** A library keeps its trash when emptying it could be a mistake - more than half of it would go (often an offline disk), its folder was unreachable at the last scan, or a scan is running. Held libraries are skipped by **Empty trash now** and the automatic run; **Empty this library's trash** empties one anyway after you confirm.
- **Cover check after an automatic link.** Once a linked series' volume covers are stored, MangaPixer compares them with the folder's own volumes - page 1 of each volume file, both halves of a jacket scan - volume by volume. When every compared volume is clearly a different picture from that volume's cover in your preferred language (at least two volumes; the volume 1 cover for a one-shot), and none matches in any language, the automatic link goes back to **Needs review** with the new reason **Cover differs**. Nothing is downloaded for this, confirmed links are never checked, and **Compare covers** switches it off.
- **Covers for estimated volumes.** A `~ Volume N` stack in the Volumes view now shows the web cover of volume N too: the `~` marks the estimate, and when an exact volume list arrives the stacks and their covers regroup.
- **Waiting matches are checked again after an update.** When a MangaPixer update changes how automatic matching scores a folder, the folders still waiting under **Needs review** are looked up once more under the new rules, in the background (only with Automatic matching on, inside the daily request budget, behind everything else). Anything an admin linked, confirmed or marked **Don't match** is never touched; a work whose new score is clear enough is linked automatically, as **Re-run matching** would. **Needs review** says how many items are being checked again, and each of them is marked *Checking again*. The automatic-matching consent text and the privacy page now say that the same folder names can be sent again for folders that are still waiting (unmatched ones after 30, 90 and 180 days, as before; waiting ones once after such an update).
- **Duplicate chapter and volume numbers are shown.** When more than one file of the same folder has the same chapter number (a chapter uploaded twice, or split into two files), the Volumes view marks the stack ("2 duplicates") and the stack page lists them ("Chapter 1: 2 files"), the Missing report and the series page line name them, and the Review row says "2 duplicate chapters". Split chapters (`2.1` + `2.2`) and numbering that starts again in another folder are not duplicates.

### Changed

- **New chapters opens the Volumes view.** Tapping a series on the home page's **New chapters** row opens a linked series in its Volumes view, sorted by name, with the **Continue** row on top showing the chapter to read next - unless you chose **Folders** for it. A series without a link or without a Volumes view opens as before, sorted by **Recently updated**.
- **The Volumes switch is never greyed out.** Under another sort the list is flat and the switch shows **Folders**; picking **Volumes** switches back to the **Name** sort and says so, with **Undo**. The sort is saved when it was your own library sort, and only for this visit when it came from the home page.
- **Filters work in the Volumes view.** The read-state filters and **Favorites only** no longer flatten the list: a volume follows the read badge its card shows (**Read** = every chapter read, **Reading** = some read or in progress, **Unread** = none read) and counts as a favorite when any of its chapters is starred; missing-volume cards are hidden while a filter is on.
- **Renamed or moved folders also keep their Volumes view setting, cover choice and every user's star** (until now only the link, precedence, reading mode, Content and declared facts moved with them).
- API (admin): `GET /api/v1/admin/move-conflicts` (`state=open|resolved`, `cursor`, `limit`), `GET /api/v1/admin/move-conflicts/count`, `POST /api/v1/admin/move-conflicts/resolve` (`ids` or `all` + optional `kind`, `resolution` `Overwrite` / `Keep`).
- Deleting a library now also removes its cover crops from the data folder.
- API (admin): `GET /api/v1/admin/trash` (overview and preview), `PUT /api/v1/admin/trash/settings` (`automaticCleaning`, `retentionDays` - one of 1, 7, 30, 90, 365), `POST /api/v1/admin/trash/empty` (`libraryId`, `releaseHold`), `POST /api/v1/admin/trash/clean-bundles`.
- Developer: the test suite is about five times faster (the server tests went from 24 minutes to under 5 on the maintainer's box). Test hosts no longer share a process-wide logger, so host-booting test classes run in parallel; `Verify-Quick.ps1` runs a real fast tier; `Verify.ps1` has `-SkipWeb` / `-WebOnly` halves that CI runs as two parallel jobs. The server's own logging is unchanged.
- **Upgrade note:** this version adds five small database migrations (`AddTombstoneLifecycle`, `AddCrossLibraryMoves`, `AddTrashSchedule`, `AddMatcherRulesRevision`, `AddTrashRunHour`: new columns and two new tables only; a snapshot is taken before they run). On the first start: series you moved to another library before the update - inside the move window (Monthly by default) - are recognized and their reading state is copied to the new copies; with Automatic matching on, the works waiting in **Needs review** are checked once more under the current rules (about two or three MangaUpdates requests each, inside the daily budget, at one per second); automatic trash cleaning stays off until an admin turns it on. No new consent is needed, and the media worker protocol is unchanged (5).

### Fixed

- A volume stack counted every file toward its "8/9" mark, so a chapter present twice could make a volume look complete; each chapter is now counted once.

### Removed

- An unused internal relink service (never called; no behaviour change).

## [1.30.1] - 2026-10-01

### Changed

- **Chapter-based instead of Fan translation.** A series that MangaUpdates lists as completely released in English, chapter by chapter, is now **Complete collection - Chapter-based** / **Finished - Chapter-based, English**, and the status line says **English chapters** instead of "English scanlation". Such a release can be official (MANGA Plus, for example), so MangaPixer no longer calls it a fan translation. The Official releases filter reads **Any / Official / Chapter-based / Original run**.

### Fixed

- **Complete collection** was shown for some series the folder does not hold whole: a volume counted as held because its chapters were only estimated, or because the volume list (built from translations) named only the translated chapters of the last volume while the original run has more. A volume you have as chapters now counts only when the volume list names its chapters exactly and your chapters reach the last chapter anything knows about. Volume files still count by themselves.
- A MangaUpdates status that gives both totals on one line ("8 Volumes | 40 Chapters (Complete)") is now read for its chapter total too.
- Docs: the Unraid, Windows and backup pages list the `volume-covers` and `cover-crops` folders in the data root, and the README names MangaDex and AniList next to MangaUpdates. The Unraid template's description (in the `dixit92/unraid-templates` repository) now says what leaves your server.

## [1.30.0] - 2026-10-01

### Added

- **What you have.** A linked series now says, above its Volumes view and on its series page, how each kind of release stands - the original run, the official release in your language (publisher, volumes, its own status) and the released chapters - and what your folder holds, with volume files and chapter files merged into one range: "You have volumes 1-14 + chapters 47-65 · up to date". Chapter files that a volume file already holds are counted once and show **Also in Volume N**.
- **Official releases.** A volume released officially in your language that you hold only as chapters is an upgrade, not a gap: its stack says **Available in English**, and the new **Official releases** tab in Metadata Manager lists these series, finished series you don't hold whole, and your complete collections.
- **Complete collection.** A finished series your folder holds whole is marked **Complete collection**; a series finished in your language that you don't hold whole says so, and which release finished: **Finished - Official** (the publisher's edition) or **Finished - Fan translation**. A complete collection says what it is complete by (**Complete collection - Official**, **- Fan translation** or **- Original run**), and the Official releases tab can be filtered the same way.
- **Select inside a volume.** A volume's page now has **Select**, like the folder list: tap chapters (on a phone too), Shift-click or long-press for a range, then **Mark read**, **Mark unread**, **Favorites** or - for admins - the **Series** menu and **Cover...**.
- **Select a whole volume.** In the Volumes view a volume can be selected like a folder or an archive; **Mark read**, **Mark unread** and **Favorites** apply to every archive in it in one step. A missing volume or chapter can't be selected.
- **Card / List switch on a volume's page.** It remembers your choice for volume pages (cards show the covers, the list shows the archive names); until you choose, it follows your library view.
- **List view inside a volume.** A volume's page follows your library view: with **List** chosen it shows the same compact rows as the folder list, with the same column count.
- **Favorites** in the selection bar of the folder list: add or remove the selected items (a volume through all its archives).
- The review dashboard shows the candidates that belong to one series family - a main story with its spin-offs, side stories, prequels or sequels - together, under **Same series family - check which one**, each marked with its part (*Main story*, *Spin-off*, *Prequel*, ...), so a folder is not given the main series' details when it holds a spin-off, or the other way round. New chips: **Spin-off or main story?** (only the subtitle tells them apart) and **Series family** (another series of the family also matched - also on the Auto-linked list).
- Review candidates show whether they fit the folder's declared type (**Fits declared type** / **Not declared type**), and Identify warns when the series you preview is not the declared type.

### Changed

- **The series cover.** A linked series folder shows your **volume 1's** cover - exactly what volume 1's own card shows (its page 1, the front half of its jacket, or the web volume 1 cover when that is clearly a different picture) - wherever volume 1 sits, so a `Chapters` subfolder no longer puts chapter 1's first page (often a credit page) on the series card. Without a volume 1 of your own the series still shows the volume 1 cover from the web.
- **Home shows the series cover.** A New chapters card of a linked series, and a Continue reading card of one of its chapters, show the series cover instead of a chapter's first page (the chapter is named under the card). Volumes keep their own cover.
- **Volume covers for chapter stacks.** The cover of a volume is also downloaded when you have all of its chapters (by MangaDex's volume list), so a complete chapter stack in the Volumes view shows its real volume cover. A volume you have only part of no longer triggers a download.
- The Missing report reads volumes and chapters the same way as the Volumes view: through the series' volume list, so a folder that mixes volume and chapter files gets numbers ("Mixed folder" is gone), chapters inside your volume files count as held, and an official volume you have as chapters is never "behind".
- MangaUpdates' English publisher notes are read per edition: omnibus, 2-in-1 / 3-in-1 and perfect editions no longer count as the English volume total, and the publisher's own status (ongoing, complete, dropped) is stored with it (records pick it up on their next refresh).
- An automatically linked folder whose volumes or chapters go far past everything known about its series moves back to **Needs review** (reason "Reach") when the series' volume list arrives or changes, or its record is refreshed; volume numbers that disagree with the list only add the reason.
- A declared type (manga, manhwa, manhua, webtoon, comic, graphic novel) is now a strong hint for automatic matching instead of a search filter: a series from the country the type names is clearly preferred and one the type contradicts loses the same amount, which settles a tie between two series of the same name - but a wrong declared type no longer keeps the right series from being found or linked. Nothing you declare is sent to MangaUpdates any more.
- Declared types show the country they stand for - Manga (Japan), Manhwa (Korea), Manhua (China), Comic (Western), ... - in the declared-facts editor, the Declared line and the library list.
- A folder named `Series - Subtitle` now ranks the spin-off that carries that subtitle above the main series. When the two are one series family, the folder waits in **Needs review** instead of being linked automatically: the subtitle alone is not enough to tell a spin-off from its main series.
- A series found only through an English title that MangaUpdates tags with its author (`Fly Me to the Moon (HATA Kenjiro)`) is looked up once more, so the author can be checked and the right series ranks first.
- **New chapters on Home** is stacked by the series folder instead of the top-level folder. In a library whose top level is categories (Manga, Manhwa, ...) each card is now a series, not a category: the folder with its own series link, or else the folder that holds the new archives (volume folders such as "Vol 3" stay part of their series).
- **Upgrade note:** this version adds three small database migrations (`AddAutoCoverArchive`, `AddStackViewMode`, `AddCandidateSeriesFamily`: new columns only; a snapshot is taken before they run). Every volume and series cover is decided once more after the update under the new cover rules. No new consent is needed, and the media worker protocol is unchanged (5).

### Removed

- The `Metadata:AutoMatch:DeclaredTypeFilter` setting (the declared-type search filter is gone; an old value is ignored).

### Fixed

- A series folder whose own volume 1 was close to, but not the same as, the web volume 1 cover (another edition) showed the web cover; it now shows your volume 1 like volume 1's own card.
- A series opened from a folder that has no Volumes view (for example a category folder) now shows its Volumes view with the missing-volume cards; they only appeared after a reload.
- A series is never marked **Complete collection** while chapters or volumes released in your language are missing; the last chapter of a finished scanlation is the highest one anything lists.
- **Covers:** a volume cover from the web in the series' original language (used when MangaDex has none in your preferred language) no longer replaces your own volume's cover or its series folder's cover - an English edition's cover differs from the Japanese one by design. Covers already chosen are decided again once after the update.
- Metadata Manager > Volume covers: the progress line no longer counts covers MangaPixer never downloads as waiting - it reads "nothing waiting" when the background pass is done, and the covers MangaDex lists for volumes you don't have (or in another language) are shown as not needed.
- A volume's card in the folder list showed a stale read state and star after you changed its chapters on the volume page or in the reader; it now refreshes.

- **Volumes view:** a chapter that the series' volume list places in two volumes (split across the boundary) is no longer marked missing in the second volume when its file is in the first one's stack.
- **Metadata Manager > Review on a phone:** the covers now sit above each row's text. The series name had been squeezed to one letter per line and the covers ran off the left edge of the screen.

## [1.29.1] - 2026-09-30

### Fixed

- **Volumes view and Missing report:** chapters split into parts on disk (2.1 + 2.2) now count as that chapter when the series' volume list names only the plain number (2). They were shown as extras, with chapter 2 marked missing. A file 3 next to 3.2 is chapter 3 in two parts, and a part missing between two others (4.1 and 4.3 here) is marked. A lone .5 (10.5) is still an extra.
- Two database query warnings in the log at start-up (volume cover decisions), and a "Overriding HTTP_PORTS" warning on every container start.

## [1.29.0] - 2026-09-29

### Added

- **Volumes view.** A series' chapters group into **volume stacks** ("Volume 3 - 10 chapters"), ordered by volume instead of by file name, using the volume in the file names (`Title v03 c012`), ComicInfo, or a series' stored volume list; boundaries that had to be estimated show as "~ Volume 12". A stack with missing chapters wears an amber mark ("8/9") and, inside, a dashed placeholder where each missing chapter belongs; extras such as chapter 45.5 are never missing. A series' `Volumes` and `Chapters` subfolders merge into one list; `Season N` / `Part N` stay folders. A **Volumes | Folders** switch in the series header shows the real folders (remembered per person); admins can set it per folder (**View...**), per library and globally. Works without any network access.
- **Covers:** a jacket spread on page 1 of a volume (back, spine and front side by side) now shows its **front half** on the card, cut on your server (no request); the reading direction decides which half. Turn it off with **Crop jacket spreads** in Metadata Manager > Settings > Covers.
- **Choose cover...** (admins): from **Cover** in the browse selection bar (one item) or **Choose cover...** in an item's admin menu, pick Automatic, this file's cover, either half of page 1, another item's cover, or a stored cover from the web. Works in every library, also under "Don't match".
- Linked series can show their **covers from the web** (see Volume covers): a volume keeps its own cover unless the web cover is clearly a different picture; a series folder shows its volume 1 cover; a one-shot shows the work's cover; a webtoon the series cover. **Show saved web covers** is a new per-library switch, separate from "Show series information", and **Delete stored volume covers** removes them. A volume stack of chapters in the Volumes view shows that volume's cover from the web.
- **MangaDex as an allowed site (volume covers and volume lists).** A series linked to MangaUpdates now finds its MangaDex record on its own - only when MangaDex's own MangaUpdates link names the same series - and reads which chapters make up each volume and which volume covers exist in your **Preferred language** (default English) and the series' original language, and which chapters are released in your preferred language - only chapters and volumes released in your language are ever shown as missing. With Automatic matching on, this runs in the background, a little at a time so new folders are still matched promptly, and downloads the covers of volume 1 and of the volumes you have (for a series MangaDex has no volume 1 cover for, such as many webtoons, its main cover instead); covers are stored on your server and the browser never contacts MangaDex. A cover missing in your language is looked for again on the series' refresh schedule. Everything counts in the daily request budget, at one request per second. MangaDex is credited in Metadata Manager next to its setting.
- **Metadata Manager > Settings > Volume covers:** "Volume covers from the web" (on by default; still needs both consents, MangaDex on the allowed sites and the library's Fetch switch), **Preferred language (covers and releases)**, the progress, and **Delete stored volume covers**. `Metadata__AutoMatch__VolumeCovers=false` switches it off regardless of the setting.
- **Change MangaDex match...** in a linked series' Admin menu: see which MangaDex title is used, paste another MangaDex title address, mark the series **Not on MangaDex**, or **Check again**.
- Opening the Volumes view of a series whose covers are still being downloaded in the background says so in a short message.
- **Missing means released in your language.** The Volumes view and the Missing report mark a chapter or volume missing only when it is released in your **Preferred language (covers and releases)** (English by default) - never an original-language volume that is not translated yet. A linked series shows its status above the list - in the country of origin, what is out in your language (official volumes, released chapters, or the English scanlation) and what is missing: "Complete (Japan) · English: 12 of 14 volumes · 2 volumes missing" -, a missing volume shows as a dashed **Volume N - Missing** card in its place, and folders that hold only volumes get the Volumes view too.
- **Volumes view details:** split chapters (4.1 + 4.2) count as one chapter, and a missing part is marked; a bonus volume (Volume 2.5) closes volume 2's stack; a chapter the volume list places in neither of two neighbouring volumes closes the earlier one; a stack shows a star when any chapter in it is starred.

### Changed

- Cover addresses now carry a version, so browsers keep covers longer and still never show an outdated one; covers are cached privately (never by a shared proxy). Home's Continue reading uses the same covers as browse.
- AniList totals for the chapters <-> volumes conversion are now also looked up automatically (with Automatic matching on) for a linked series that MangaDex has no volume list for - by the AniList number MangaDex links when there is one.
- **Missing report:** "behind" is counted only against what is released in your preferred language; the total in the country of origin is shown as context ("original run: 14").
- **Missing report:** the **Get chapters per volume from AniList** button is not shown while Automatic matching with volume covers already looks these totals up in the background.
- An admin's **Refresh** of a linked series also reads its MangaDex volume list and cover list.
- Each metadata site now has its own request rate and its own pause when it asks MangaPixer to slow down: a busy MangaDex or AniList never pauses MangaUpdates.
- **Upgrade note:** this version adds a database migration (`AddVolumeCoversAndVirtualVolumes`: new tables for volume lists, volume covers and cover choices, and new settings columns; a snapshot is taken before it runs). The media worker protocol is now 5 (the server and the worker ship together; the server refuses an older worker).
- **Accept the metadata consents again after this update.** Both consent texts now cover MangaDex (volume covers, and which chapters make up each volume, for series already linked to MangaUpdates) and AniList's totals as a fallback; fetching from the web and Automatic matching stay off until an admin accepts the new texts in Metadata Manager.
- **Review rows open their folder.** Each row in the Metadata Manager's review lists has a folder button that opens the folder in the library - an archive opens the folder that contains it.
- API (admin): review rows (`GET /api/v1/admin/metadata/review`) return `parentNodeId`, the folder that contains the row's folder or archive.
- Updated dependencies: Angular 22.2.0 and Magick.NET 14.17.2 (ImageMagick 7.1.2-32, which now also bundles zstd); THIRD-PARTY-NOTICES refreshed.

### Fixed

- **Series covers in the review lists no longer show as broken images.** A list with many rows asked MangaUpdates for every selected series' cover at once and most were refused; covers now load a few at a time, only for rows on screen, are retried when MangaUpdates is busy, and a cover that still cannot be loaded shows "No cover" with a tap to try again.
- Removing MangaUpdates from the allowed sites while Automatic matching is on now pauses automatic matching and the background refresh ("MangaUpdates is off the provider allowlist") instead of counting every waiting folder as failed.
- **Missing report: Season and Part subfolders.** A series kept in `Season 1` / `Season 2` (or `Part 1`, `Book 2`, `12` ...) subfolders is now counted, also one level inside another (`Season 1/Volumes`). When the numbering starts again in each subfolder, the series shows **Numbering restarts** instead of a wrong count. A lone chapter 0 or prologue no longer reads "You have chapter 0 of 223".
- **Missing report: extras.** A `.5` chapter or volume never fills a number and is never counted as missing (unless the volume list names it as one part of a split chapter).
- **Identify: the count warning compares like with like.** Volumes are compared with the record's volume total and chapters with its chapter total, and the warning says which ("The record lists 7 volumes; this folder has volumes 1-43."). A folder of chapters gets no warning when the series is counted only in volumes.
- **Automatic matching: counts.** Volume and chapter subfolders count by the numbers in their file names, not by how many files they hold, and the latest tracked chapter no longer counts against a series that is counted in volumes (a chapter folder of a spin-off was held back for review this way).
- **Series found under an author-tagged title.** MangaUpdates adds the author to titles several works share ("Fly Me to the Moon (HATA Kenjiro)"); such a title now counts without the tag - in full when the tag names the record's own author - so a folder named by a series' English title finds the right record, in automatic matching and in the Identify dialog. A title that matches only this way never links on its own: several works share the name, so the folder goes to Needs review with the right record among the candidates.
- **Automatic matching: volume numbers in chapter names.** Chapter archives named like `Title v09 c060` now also tell automatic matching how many volumes the run has reached, compared with the record's volume total.
- **Volumes view switch:** choosing on the Volumes | Folders switch what a folder shows by default clears your own choice, so a later change of the admin's default ("Group chapters into volumes by default", a library's or a folder's setting) reaches you again.
- **Phones:** the top bar no longer makes every page scroll sideways on a narrow screen (about 390 pixels), and the account button is no longer cut off.

## [1.28.0] - 2026-09-28

### Added

- **Card controls on the home page.** Cards in **Continue reading** and **New chapters** now have the same controls as the library view: the favorites star and, when there is series information, the (i) and the summary on hover. A Continue reading card is a single archive, so its (i) shows the series it belongs to - for example the linked series folder it sits in. On a New chapters card the star and the (i) belong to the folder (or to the archive, for a loose archive). Both follow **Show series information**, like everywhere else. See [Summary on hover](docs/series-information.md#summary-on-hover).
- **Declared facts.** Admins can state what the works in a folder or a whole library are - their type (manga, manhwa, manhua, webtoon, comic, graphic novel, novel) and their creators, each with an optional role. A declaration applies to everything below it until a folder declares its own. Set it with **Admin** > **Declared facts…** on a folder's series panel or series page, or with **Edit** next to **Declared:** under a library in **Administration** > **Libraries**. MangaPixer never infers them from folder or library names. The series panel and series page show a **Declared:** line, and a **Conflict** badge with what MangaUpdates says when the linked series disagrees on the type or the creators; a declaration never changes the linked record. See [Declared facts](docs/declared-hints.md).
- **Declared facts help automatic matching.** A declared type prefers records of that origin and declared creators prefer records by them - a folder of several same-titled series now links the one by the declared author. A declared manga, manhwa or manhua type also narrows automatic searches to that type (MangaUpdates is asked to leave the other two out), so check a type before you declare it. Creators and other declared types are never sent. Declared facts follow a renamed or moved folder, like its link and settings.
- **Artist folders by linked authors.** A folder named exactly like an author or artist of a series already linked in the same library is now matched archive by archive as that artist's folder, also when the file names carry no `[Artist]` tag. Only the series information already stored on your server is used; a folder whose archives are volumes or chapters of one work stays a series.
- **Cover comparison in automatic matching.** When two MangaUpdates records tie on the title for a folder of volumes or a one-shot - a series and its anthology, or several series with the same name - MangaPixer compares their covers with the folder's own cover and puts the record with the same cover first in Needs review. It downloads at most two cover images per folder, counted in the daily budget; chapter folders and webtoons are never compared, and a matching cover never links a record on its own. Turn it off with **Compare covers** under Automatic matching.
- **Missing volumes and chapters.** A new **Missing** tab in the Metadata Manager lists every folder linked to a series with its highest volume and chapter number on disk against the series' total - the English publisher's count first, then the count in the country of origin, then the latest chapter - plus the gaps in your numbering and where each total came from. Volumes and chapters are compared separately; a folder that mixes them is left out. The series page shows the same line to everyone who can open the folder ("You have volumes 1-7 of 10 (English) · 3 behind"). Built from stored data: opening it contacts no site. See [Missing volumes and chapters](docs/missing-report.md).
- **Chapters per volume from AniList** (Missing report, admins, on request only): asks AniList for a linked series' volume and chapter totals - by the linked MangaUpdates title, never a folder name - so a total in volumes can be compared with the chapters you have, and the other way round (shown as an estimate).
- **Allowed sites.** Metadata Manager > Settings lists every site MangaPixer may contact for series information (MangaUpdates, AniList), with what each is used for and what is sent. Remove a site and it gets no request of any kind; add it back at any time.

### Changed

- **Compare covers in the review lists.** Each row in the Metadata Manager's review lists now shows your own cover next to the series cover - the candidate you selected, or the linked record's poster - large enough to spot a wrong link at a glance, on any screen. Rest the mouse on the pair or tap it for a larger view. See [Review](docs/series-information.md#review).
- **Accept the metadata consents again after this update.** The consent texts now cover the allowed sites and AniList (Fetch from the web) and the cover downloads and the declared-type search filter (Automatic matching). Until an admin reviews the allowed sites and accepts the new texts in **Metadata Manager** > **Settings**, fetching and automatic matching stay off; a banner on the administration pages says so, and the Automatic matching card shows **Waiting for consent**. Manual Identify needs only the Fetch consent. Stored data stays.
- Records fetched or refreshed from now on keep the English publisher's volume and chapter counts (used by the Missing report).
- **The favorites star sits in the bottom-right corner of every card** - in the library, Search, Favorites and on the home page - so it is in the same place everywhere. In the library, the reading-direction badge of a folder moved to the top-left corner.
- **Series page: "also known as" once.** The header of the full series page shows the first two alternative titles and **+N**; the whole list stays under **Details**.
- **Series descriptions without dead links.** The lists of links at the end of many MangaUpdates descriptions (for example "Original Webtoon: Daum, Kakaopage" or "Official English Translations" with one line per language) are no longer shown as plain text that looks like links; a heading left with nothing under it goes too. Links inside a sentence keep their text. This applies to records fetched or refreshed after the update.
- API: `GET /api/v1/reading/continue` and `GET /api/v1/home/recent-chapters` return `isFavorite` and `hasSeriesInfo` per card, and both now declare their response schema in the OpenAPI contract.
- API (admin): `GET /api/v1/admin/metadata/missing` (`library`, `onlyMissing`, `cursor`, `limit`), `GET .../missing/{nodeId}`, `POST .../missing/{nodeId}/conversion`, `POST .../missing/conversions`; declared facts under `/api/v1/admin/metadata/{folders,libraries}/{id}/declared`. `MetadataSettingsDto` gains `providers`, `consentRenewalNeeded`, `autoConsentRenewalNeeded`, `compareCoversEnabled`, `compareCoversDisabledByConfig`; `UpdateMetadataSettingsRequest` gains `removedProviders`, `compareCoversEnabled`; both consent versions are 2. API (everyone with access): `GET /api/v1/nodes/{nodeId}/declared-facts`, `GET /api/v1/nodes/{nodeId}/missing`.
- **Upgrade note:** this version adds a database migration (`AddDeclaredFacts`: a new table for declared facts and two settings columns; a snapshot is taken before it runs). The media worker protocol is now 4 (the server and the worker ship together; the server refuses an older worker at start-up). After the update, accept the metadata consents again in **Metadata Manager** > **Settings** (see above).

### Fixed

- **Animated pages keep moving.** Crisp and Enhance no longer apply to animated pages (GIF, WebP or PNG with several frames); they drew only one frame, so the animation stopped. Other pages are upscaled as before.
- A folder queued for review that turned out, by the time it was processed, to be an artist's folder (its author was linked earlier in the same run) was skipped without matching its archives; its archives are now matched in the same run.
- **Automatic matching picks in the right order with a long queue.** With more than 200 folders waiting, an admin's **Re-run matching** and new folders could wait behind older retries; the queue is now always taken in priority order, oldest first. This also removes a database warning logged once after every start.

### Security

- The media worker decodes only JPEG, PNG, GIF and WebP when it compares covers; anything else is refused before it is read as an image.

## [1.27.0] - 2026-09-27

### Added

- **Series information on hover.** With a mouse or trackpad, resting the pointer on the cover, the title or the (i) of an item that has series information shows a short summary beside it (title, alternative titles, facts, genres and the start of the description) - in the library's card and list views, in Search and on the Favorites page. **More** under the description opens the series page. A click still opens the item and the (i) still opens the full panel. The summary is loaded from your own server only after the pointer rests on an item (moving across the library sends nothing), and it follows **Show series information**. Touch screens are not affected. It is on by default; turn it off in **Settings** > **Series information** (saved to your account). The (i) now also appears on Search results and on the Favorites page. See [Summary on hover](docs/series-information.md#summary-on-hover).
- **Favorites stack by folder.** When you star two or more archives in the same folder, the Favorites page and the home **Favorites** row show them as one stacked card with the folder's cover, its name and how many favorites it holds, placed where your most recently starred one would be. Selecting the stack opens that folder with **Favorites only** turned on (just for that visit; turning the filter off shows the whole folder again). Starred folders and single favorites keep their own cards, and a stack only counts favorites you can currently see. See [Favorites](docs/library-layout.md#favorites).
- **Near the end counts as finished.** Stopping on page 19 of 20 now counts the same as the last page: a position with at most 5% of the pages after it (at least 1, at most 5 pages) marks the archive read and reopens it on page 1, so 195/200 counts and 190/200 still resumes where you left off. The same rule sets the read mark and the open position, so a read archive that opens at the start always shows as read. **Always open read archives from the start** keeps its meaning and stays off by default. Existing positions and read marks are not changed: an archive left near the end before the update keeps its Reading state until you next reach the end. Arriving at the end through **Previous archive** still does not mark an archive read. See [Where you resume](docs/reader.md#where-you-resume).
- **Phone reader: Bookmark page, Bookmarks and Favorite** in the **Reader options** sheet - the desktop toolbar's bookmark buttons and star, which the phone toolbar has no room for.
- **Documentation website.** The guides in `docs/` are published as a website at https://mangapixer.com: searchable, readable on a phone, and free of trackers, web fonts and third-party scripts. It is built from each release, so it always describes the current version. A new "MangaPixer compared" guide explains how MangaPixer differs from Komga, Kavita and YACReader.

### Changed

- **Metadata Manager, formerly "Series metadata".** Renamed everywhere it is shown to admins - the page title, the account-menu item and its badge, and the docs; the address is unchanged (`/admin/metadata`). Its summary card (to review, open flags, requests today, automatic matching) moved from the Administration page to the top of the Metadata Manager page, in place of the old one-line summary, and its numbers switch straight to the matching tab; Administration no longer shows a metadata card. See [Metadata Manager page](docs/series-information.md#metadata-manager-page-admins).
- **Scores are shown as percents.** Review rows, Identify and the Advanced matching thresholds in Settings show whole percents (for example "Overall 92%") instead of a 0-1 number. A review row's **Overall** score - which also weighs item counts, year, type and origin, not only the title - is labelled separately from Identify's title-only **Title match**, so the two are never confused. The API keeps 0-1 values.
- **Metadata Manager review and Identify show your cover.** Folders in the review lists show their thumbnail (the cover of their first archive, as in browse) instead of a folder icon, and the Identify preview shows your folder's or archive's cover next to the MangaUpdates cover, so the two can be compared at a glance.
- **Identify lists the alternative titles** of the previewed record ("also: …", the first six, then **+N more**) instead of only counting them.
- **Automatic matching: category folders and tall pages only add evidence.** A folder named exactly `Manga`, `Manhwa`, `Manhua` or `Webtoon(s)` still gives a small preference to records of that origin, but a manhwa kept in a `Manga` folder is now linked instead of going to review. MangaPixer still reads nothing else into library or folder names. Likewise, tall webtoon-shaped pages still favour webtoon records but no longer keep a Japanese print record from being linked (vertical manga exist).
- **Automatic matching: author names with plain separators.** `Title by Author`, `Title - Chapter 12 | Author` and `Author Name - Title` now count as a possible author, like names in brackets, and the title part is also searched on its own. An author name only ever adds confidence. A two-word name in brackets at the end links a record automatically only when the folder's own name fits that record too.
- **Search engines are asked to stay away.** Every page of your server now carries an `X-Robots-Tag: noindex, nofollow` header and `/robots.txt` disallows crawling, so a server reachable from the internet does not show up in search results. `MangaPixer__Network__AllowSearchIndexing=true` turns this off. See [Network](docs/configuration.md#network).
- **Privacy:** automatic matching may now read the **second page** of the same search when the first page is a tie or has nothing close enough (same search text, same host, counted in the same daily budget, at most four searches per series as before), and it looks up a record only when that record could match, so it sends fewer requests overall. See [What leaves your server](docs/privacy-and-security.md#what-leaves-your-server).
- API (admin): review items and the identify context's `local` part have a `coverUrl`. API: favorites items carry `favoriteStackCount`; search results and favorites carry `hasSeriesInfo`; the library view preferences have `seriesInfoOnHover`.
- **Upgrade note:** this version adds a database migration (`AddSeriesInfoOnHover`: one column, on for every existing user; a snapshot is taken before it runs). The media worker protocol is unchanged (3). To apply the matching fixes to series already waiting in **Needs review**, use **Re-run matching** there; **Match now** picks up the rest.

### Fixed

- **Automatic matching: webtoons whose chapter numbers restart each season.** The chapter total MangaUpdates states in its status (`195 Chapters`) now counts, so such a folder is no longer sent to review for having "too many" chapters. A record's webtoon flag now reaches the matcher too.
- **Automatic matching: chapter and volume counts.** The count check compares the highest chapter or volume number in the file names, so `12.5` chapters and extras no longer add up, with the largest count MangaUpdates states, including the English publishers' totals. A folder that mixes volumes and chapters is not compared.
- **Automatic matching: one-word titles.** Another record whose alternative title carries a name in parentheses (`Title (Webtoon)`) no longer ties with the right record.
- **Automatic matching: partial names.** A folder named after the first words of a long title, or a record titled `Title ~Subtitle~` or `Title - Subtitle`, is offered for review instead of going unmatched; a number that belongs to the title (`Title Level 99`) is no longer read as a sequel number. When the archives carry the longer name, it is searched second. A spin-off that lists the series name as an alias is no longer linked in place of the series. "Close second" is no longer shown for works without a usable match. See [Automatic matching](docs/series-information.md#automatic-matching).
- **Metadata Manager review tabs:** switching tabs or libraries right after confirming, accepting or otherwise changing a row could leave that row missing from the new tab until a refresh; the list now waits for the change to land. Hovering several reason chips no longer opens overlapping tooltips.
- **Vertical view reopened at page 1** while the page counter showed the saved page, and the first scroll then saved a position near page 1 over it. An archive now opens in vertical mode scrolled to the saved page, also when vertical mode is chosen from the menu or the series' reading mode arrives late, and opening it never moves the saved position.
- **Reading progress is saved more reliably:** a save that overlaps an earlier one is sent again instead of being dropped, letting go of the page slider in vertical mode saves once, and a vertical-mode position waiting to be saved is sent when you switch away from the browser or app.
- Changing the card size, view, sort or page size in a library no longer switches off **Show a Favorites row on the home page** and **Highlight favorited results in search**.

## [1.26.1] - 2026-09-27

### Fixed

- **Automatic matching: chapters with their own subtitles.** A folder of numbered chapters whose names carry a chapter title (`Title 025 Chapter Name.cbz`, `Title 000 Oneshot.cbz`) is now matched as one series instead of chapter by chapter. When a folder has been matched, or an admin links it or marks it **Don't match**, the review and unmatched rows of the archives inside it are removed, since the folder covers them. To clean up a library matched with 1.26.0, run **Match now** on it.
- **Automatic matching: author names in file and folder names.** A name in brackets or parentheses anywhere in the name (`Title [Author]`, `[Author] Title`, `Title [English Title] (Author)`), or before a lone closing bracket (`Author] Title`), counts as a possible author. When a MangaUpdates record's author, or its `(AUTHOR Name)` suffix, matches, that record wins over records with the same title. A trailing author no longer hides an `[English Title]` from the search, and text cut off by a lone bracket is no longer searched as part of the title. Items already waiting in **Needs review** keep their candidates; use **Re-run matching** on them.
- **Automatic matching: records with a long subtitle.** A record titled `Title: Long Subtitle` now ranks first for a folder named `Title`. It is offered for review, never linked on its own.

## [1.26.0] - 2026-09-27

### Added

- **Series information: automatic matching.** A new **Automatic matching** switch (Administration > Series metadata, off by default, with its own consent text) matches series on its own in every library that fetches from the web: after each scan for the new folders and for folders that gained archives (a new doujin in an artist folder is matched on its own; a new chapter of a linked series needs no lookup), or for a whole library with **Match now**, which first shows how many works and requests that is and can send everything to review once. MangaPixer tells series folders, one-shots and collections apart by their names and contents, so category and author folders are never linked themselves; confident matches go live at once, close calls wait in a review list with their candidates, and nothing inside a **Don't match** folder is ever looked up. Automatic lookups use the same daily request budget (no separate limit), send at most one request per second, and leave doujinshi and novels out unless a folder's new **Content** setting says it holds doujinshi. Linked series are refreshed in the background by their MangaUpdates number (at most 100 a day). Three advanced thresholds tune how sure a match must be. See [Automatic matching](docs/series-information.md#automatic-matching) and [What is sent](docs/series-information.md#what-is-sent). **Upgrade note:** a database migration adds the new tables and settings on first start; automatic matching stays off until an admin turns it on.
- **Renamed folders keep their series settings.** When a folder is renamed or moved, its link or **Don't match**, source precedence, default reading mode and **Content** setting move to the new folder; when that is not clear (a folder split in two), they wait under **Missing folders** to be re-attached. See [Renamed and moved folders](docs/series-information.md#renamed-and-moved-folders).
- **Search finds series by their alternative titles.** A folder or archive linked to a series can now be found by any of that series' alternative names (a folder named `Dungeon Meshi` is found by `Delicious in Dungeon`). Those hits appear in a **Series matches** row above the normal results, each card captioned `aka <matched title>`; a folder that also matches by name shows only once. Private libraries stay hidden in Incognito, and libraries with **Show series information** off are not searchable this way. See [Search by alternative title](docs/series-information.md#search-by-alternative-title).
- **Series metadata page.** **MangaPixer Administration** > **Series metadata** now opens its own page, `/admin/metadata`, with four tabs. **Settings** holds everything the admin card had (the two switches and their consent, the daily budget, per-library Fetch / Show / precedence / Delete fetched data, ComicInfo progress, Delete all fetched web data) plus: the global **Automatic matching** switch, which can only be turned on after its own consent text explaining that folder names are then sent automatically, and which lists the libraries it covers (every library with Fetch on); a usage bar for the ONE daily budget, which automatic work shares with Identify (when it is spent, automatic work stops until the next day); **Match now** per library with a local estimate (folders to match, requests, days at the current budget, already linked) and, for a library's first run, "review everything once"; and **Advanced: matching thresholds** (auto-link title score, lead over the runner-up, review floor) with their allowed ranges and **Reset to defaults**. Once agreed, each consent text folds behind a **What is sent?** link so the controls come first. **Runs** shows the background matching status and why it waits, the live run's progress with **Cancel**, the run history with its counters, and how many automatic links an admin later changed. See [Series metadata page](docs/series-information.md#series-metadata-page-admins).
- **Review dashboard.** The **Review** tab lists **Needs review**, **Auto-linked**, **Unmatched**, **Don't match**, **Confirmed** and **Missing folders** with counts and a library filter. A row shows the folder or archive (archives and archive groups are marked), why it is there (chips such as *Close second*, *Count*, *Year*, *Type*) and the stored candidates as a choice; **Accept**, **Identify…**, **Don't match**, or on Auto-linked **Confirm**, **Change…**, **Unlink**. Candidate covers load only when you expand a row, because each one is a request. Select several rows for bulk actions. Every change can be undone from the message that follows: nothing is sent until the message closes. Keyboard: `j`/`k` move, `a` accept, `d` Don't match, `i` identify, `l` later, `c` confirm, `u` unlink, `x` select, `e` covers. **Missing folders** offers **Re-attach to…** with a folder picker, or **Delete**. On a phone, each row is a card and its actions sit in a bar at the bottom; long-press starts selecting.
- **"Wrong series?" reports.** Readers who see series information from MangaUpdates on the series panel or page can report it as wrong (a reason and an optional note of up to 500 characters that only admins see). They see "You reported this" until an admin reviews it, then "Reviewed". Admins find the reports under **Flags** (reports on automatic links first) and resolve them with **Re-identify…**, **Unlink**, **Don't match** (suggested first for "This folder is not one series") or **Dismiss**. When a reader reaches the daily report limit, the dialog says so.
- **Folder Content setting.** The panel's and series page's **Admin** menu and the selection bar's **Series** menu set a folder's **Content**: **Auto**, **Doujinshi & adult one-shots** (automatic matching then also searches doujinshi in this folder and below, and **Identify** starts with **Hide doujinshi & novels** unticked there) or **Not doujinshi**. It is inherited like the folder reading direction; the menu shows whether the value is set here or inherited, and marks MangaPixer's suggestion when it has one. When a change allows or stops doujinshi below the folder, the items there still in Needs review or Unmatched are queued to match again (the message says how many; above 200 it asks first with **Match again**); linked and Don't-match items are never touched. See [Folder Content](docs/series-information.md#folder-content).

### Changed

- **Privacy:** MangaPixer can now look series up on MangaUpdates **automatically**, but only after an admin turns on **Automatic matching** (off by default) and ticks its own consent text, and only in libraries whose **Fetch from the web** is on. It then sends, without anyone reviewing it first, the cleaned name of each new series folder or of an archive that is its own work (in a collection folder, or loose next to other folders), with the fixed list of types to leave out (without doujinshi below a folder whose **Content** is **Doujinshi & adult one-shots**), and the record numbers of linked series to refresh them. Same hosts, same daily budget, at most one request per second; nothing inside a **Don't match** folder is ever looked up. Manual Identify keeps working without the new consent. `Metadata__NetworkDisabled=true` still switches everything off. See [What leaves your server](docs/privacy-and-security.md#what-leaves-your-server).
- **Delete fetched data** (per library or all) and **Unlink** now also let **Match now** look those folders up again.
- API (admin): `POST /admin/metadata/folders/{id}/content/rematch`, and a `rematch` result on the Content PUT / DELETE responses; the identify context has `doujinshiContent` (true below a folder whose Content is Doujinshi & adult one-shots); review lists, bulk actions, runs and estimates, flags, missing-folder re-attach and folder **Content** under `/api/v1/admin/metadata/*`; any signed-in user with access to a node can report it with `POST /api/v1/nodes/{nodeId}/series-info/flags`.
- **Upgrade note:** this version adds a database migration (`AddMetadataAutoMatch`: the match queue, runs, stored candidates, flags and folder Content; a snapshot is taken before it runs). The media worker protocol is unchanged (3).
- **MangaPixer Administration:** the Series metadata card is replaced by a summary tile (items to review, open reports, requests today against the budget, automatic matching on or off) that links to the new page, and the account menu has a **Series metadata** item for admins, with a badge showing the number of items to review plus open reports.

### Fixed

- **MangaPixer Administration on a phone:** library rows showed no name and hid some of their buttons (rename, icon, remove); they now wrap, with every action visible. The version line at the bottom no longer floats over the cards on a phone.
- **Library Direction** showed an empty box for libraries set to **Inherit**; it now reads **Inherit**.

## [1.25.0] - 2026-09-26

### Added

- **Reader: Enhance without HTTPS, and a new Upscaling choice, Crisp.** Browsers only offer WebGPU on secure pages, so over plain `http://<LAN address>` (a LAN install, Docker or Unraid without a reverse proxy, the Windows app with **Allow LAN access**) **Enhance** used to be greyed out ("Enhance needs WebGPU") and a saved Enhance quietly showed Smooth. Enhance now runs on WebGL2 there (the Efficient network; **Max quality** still needs WebGPU), and a new **Crisp** choice (AMD FSR 1: an edge-aware upscale plus light sharpening, much cheaper than Enhance) works on almost any device, over HTTP too, in single page, double page and vertical modes. Upscaling is now **Smooth / Crisp / Enhance**; `e` cycles through the ones this device can run.
- **Reader: no silent Upscaling fallback.** The Upscaling menu names the upscaler under the selected choice (for example "AMD FSR 1", "Anime4K", or "Anime4K (WebGL2)" without WebGPU, where **Max quality** says it needs WebGPU and HTTPS), greys out a choice this device cannot run with the reason ("Needs a secure connection (HTTPS)", "Graphics chip unavailable", ...), and the reader says once per session when a choice you saved cannot run here ("Enhance isn't available here - showing Smooth."). The status line under Upscaling summarises what this device offers (on phones, where the choices are chips, it also names the engine in use). When the browser can only draw WebGL in software (a blocked graphics chip), Enhance is greyed out as "Graphics chip unavailable" - it would take seconds per page - and Crisp still works. **Enhance quality** now appears only while Enhance is selected. See [Image quality](docs/reader.md#image-quality).

### Changed

- **Reader: Crisp is the new default for Upscaling.** On a device where you never picked an Upscaling choice, pages shown larger than their resolution now use Crisp instead of the browser's own scaling; where Crisp cannot run, the page shows Smooth without a notice (you did not choose Crisp), and the menu says why. A choice you picked is kept. Enhance stays available: its line art can be a little cleaner, at several times the graphics work and battery.
- **Reader: "Rendering" is now "Upscaling"**, the counterpart of the Downscale filter in the same menu.
- **Reader: archives are called archives.** The previous/next buttons, their tooltips, the end-of-folder messages, the loading and error messages and the reading help now say "archive" instead of "chapter" (for example **Next archive**, "Preparing this archive…"), because an archive can be a chapter, a volume or anything else. The "New chapters" row on Home keeps its name. See [Moving between archives](docs/reader.md#moving-between-archives).
- **MangaPixer Administration:** **Series metadata** now follows the audit trail, and **Logging**, a debugging tool, is the last card on the page.

## [1.24.2] - 2026-09-26

### Fixed

- **Name sort is no longer case-sensitive.** Folders and archives whose names start with a lower-case letter used to list after every capitalised name (`Zebra` before `apple`), and the A-Z jump rail could not reach them. Name sort now ignores case (`apple`, `Banana`, `cherry`); names that differ only in case sit next to each other, capitalised first. Punctuation such as `[` and `_` now lists before letters. Page order inside archives is unchanged. **Upgrade note:** a data-only database migration updates the stored sort keys on first start (a snapshot is taken before it runs); no rescan is needed. See [Sorting](docs/library-layout.md#sorting).

## [1.24.1] - 2026-09-26

### Fixed

- **Page cache grew past its budget.** The page cache only counted the pages written since the server last started, so every restart (an update, a container restart, a reboot) left the previous run's pages behind, uncounted and never deleted: one server had 4.8 GB in a 1 GiB cache. The cache now starts empty on every start: each run writes into its own folder under the cache root and deletes what earlier runs left (including the old layout) in the background, so reading starts at once. Only cache-shaped files are deleted, so a cache path pointed at the wrong folder loses nothing else. The first read of each page after a restart is regenerated; thumbnails are stored separately and are not affected. See [Configuration](docs/configuration.md#storage).
- **Reader: switching from single or double page to vertical mode** kept the smaller page sizes requested for single page for pages already loaded, so they looked soft in the strip. Switching to vertical now requests every page at strip size.
- **Series information: Identify** sampled a folder's archives, page sizes and ComicInfo links in no fixed order, so the "tall pages" hint and the ComicInfo suggestion could differ between two openings of the dialog for a large folder (EF Core also logged a warning about it). They are now taken in a fixed order.
- **Security:** request and response records that carry a password, token or activation link (sign-in, first-run setup, password change and reset, account activation, user creation, backup settings) now print those values as `[redacted]`, so raising the log level can never write them to the log.

## [1.24.0] - 2026-09-26

### Added

- **Enhance in vertical (webtoon) mode.** Rendering: **Enhance** now also sharpens small-source webtoon strips on your device's graphics chip (WebGPU). Each page is enhanced in horizontal bands as you reach it: the plain page shows while you scroll quickly and the enhanced one fades in a moment after you stop. Changing the page width, rotating or zooming never re-renders anything, graphics memory stays bounded however long the chapter is, and only pages shown more than 1.2 times larger than their original width are enhanced. `e` now switches Rendering in vertical mode too. If the graphics chip resets twice within a minute, vertical-mode Enhance pauses and the page shows normally. See [Rendering](docs/reader.md#image-quality).
- **Series information from ComicInfo.xml.** MangaPixer now reads the `ComicInfo.xml` inside your archives (series, number, volume, title, summary, credits, genres, publisher) when it analyses them, and reads it once, in the background, for archives analysed before this version. Nothing leaves your server: this uses only the files you already have. A card whose folder or archive carries this information shows an **(i)** in the cover's bottom-left corner (in list view, next to the star); it opens a side panel (a bottom sheet on a phone) with the series summary, and **Open series page** leads to the full page at `/series/…` with the list of items. Inside a series folder, a **Series info** button in the top bar opens the same panel. A folder whose items name several different series shows them as a list instead of one series. The series page shows the description once (under About), genres as plain text, and the source precedence only when both web data and ComicInfo exist; a series page for a single archive offers **Read** / **Continue reading** and **Show in folder**. In select mode the card star is hidden, like the (i), so it no longer sits under the selection check. Solid 7z archives are skipped.
- **Series metadata admin controls.** Admins can mark a folder or archive **Don't match** (it is not one series, so nothing is inherited from above) and choose the **source precedence** per folder (web first or ComicInfo first, applying to everything inside), from the panel, the series page, or the new **Series** menu of the selection bar (hidden while **Show series information** is off). Admin API: `/api/v1/admin/metadata/*` (settings, library toggles and precedence, node links, folder precedence, purge); series information for a node: `GET /api/v1/nodes/{nodeId}/series-info`. Looking series up on the web: see **Identify series on MangaUpdates** below.
- **Identify series on MangaUpdates (optional, off by default).** Admins can link a folder or archive to a [MangaUpdates](https://www.mangaupdates.com) series: **Identify…** in the series panel's or series page's **Admin** menu (or **Series** > **Identify…** with one item selected) searches with text the admin confirms, or reads the series number from a pasted MangaUpdates address or `mu:` shortcode, ranks the results against the folder name, previews the record next to your folder (with warnings such as "this is a novel") and links it with **Undo**. **Hide doujinshi & novels** (on by default) filters those types out of the results. Inside a folder without information, admins find **Identify…** in the top bar. The card (i) and the top-bar button follow Link, Undo, Unlink and Don't match immediately, without reloading the page. The panel and series page then show MangaUpdates' description, authors, genres, publication status and cover art, credited to MangaUpdates; **Refresh** fetches it again. Nothing happens automatically. See [Series information](docs/series-information.md).
- **Series metadata admin card.** **MangaPixer Administration** > **Series metadata**: the two switches (**Show series information**; **Fetch from the web**, which can only be turned on after a consent text explaining exactly what is sent), requests used today and any wait MangaUpdates asked for, the **Daily request budget** (5000 by default), per-library Fetch / Show / precedence / **Delete fetched data**, ComicInfo progress, and **Delete all fetched web data**.
- **Enhance quality: Efficient or Max quality.** A new choice under Rendering. **Efficient** (the new default) runs a lighter Anime4K network that uses much less graphics memory and battery; **Max quality** keeps the heavier network Enhance used until now, for single and double page. Vertical mode always uses Efficient, so the choice is shown only in single and double page.

### Changed

- CI now fails when the shipped sets in `THIRD-PARTY-NOTICES.md` (sections 1 and 3) drift from the published .NET packages or the web runtime dependency tree (`web/scripts/check-notices-drift.mjs`, new `notices` job).
- Rendering: Enhance in single and double page now uses **Efficient** by default; choose **Max quality** for the previous look.
- **Privacy:** MangaPixer can now contact a second internet service, only when an admin enables it: `api.mangaupdates.com` and `cdn.mangaupdates.com`, only for admin Identify / Look up / Refresh actions, sending only the confirmed search text, record numbers, the fixed "Hide doujinshi & novels" type list when ticked, and a generic `User-Agent: MangaPixer-Metadata`. At most 2 requests per second (5 for cover images), a daily budget, persisted backoff when MangaUpdates asks for it, and no redirect or other host is ever followed. Covers are stored on your server and served by MangaPixer, so browsers never contact MangaUpdates. `Metadata__NetworkDisabled=true` switches it off regardless of the UI. See [What leaves your server](docs/privacy-and-security.md#what-leaves-your-server).
- API (admin): `GET /api/v1/admin/metadata/nodes/{nodeId}/identify`, `POST .../nodes/{nodeId}/search`, `.../lookup`, `.../preview`, `.../refresh`, `GET .../candidates/{token}/image`; `PUT .../nodes/{nodeId}/link` now fetches a series that is not stored yet. Any signed-in user with access to the node: `GET /api/v1/nodes/{nodeId}/series-info/image` (the poster; 404 without access).
- **Upgrade note:** this version adds a database migration (series metadata tables and settings; a snapshot is taken before it runs) and a new server/worker protocol version (3). The server and the media worker ship together; a mismatched pair refuses to start the worker.

### Fixed

- **Reader: loading feedback.** While a page is still loading (for example when the library's drive is spinning up), vertical mode now shows a spinner in each page's reserved space instead of plain black, and single and double page show a spinner that is easier to see on the dark background. Both add **Loading…** when a page takes longer than about 3 seconds. A vertical-mode page that fails to load offers **Tap to retry**. See [Loading and prefetch](docs/reader.md#loading-and-prefetch).
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
