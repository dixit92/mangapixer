# Changelog

All notable changes to MangaPixer are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Dates are the release tag dates. Before 1.0.0 the project used an internal `0.1.0-dev.N` pre-release series that is not listed here.

## [Unreleased]

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
