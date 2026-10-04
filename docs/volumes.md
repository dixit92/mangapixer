# Volumes view

A manga that comes as loose chapters, or as a `Volumes` folder next to a `Chapters` folder, is easier to read as **volumes**. The **Volumes view** groups a series' chapters into **volume stacks** ("Volume 3 - 10 chapters"), orders everything by volume, and shows where a chapter is missing. Your files are never touched: the view is built from your archive names, their embedded ComicInfo and, for linked series, the volume list stored with the series record.

It works without any network access. Names like `Series v03 c012` group on their own; a stored volume list only adds the volumes your file names don't state.

## Where to find it

Open a series folder that has something to group. In the top bar a **Volumes | Folders** switch appears next to the series information button. **Volumes** is the grouped view; **Folders** is the real folder list with every file and subfolder as it is on disk. Your choice is remembered for you.

The switch is only shown where a Volumes view exists: a folder whose files group into volumes, or a folder linked to a series that holds volumes (its Volumes view shows the series status, see [Missing volumes and chapters](#missing-volumes-and-chapters)). *New in 1.34.0:* for a webtoon, manhwa or manhua without a volume list the switch reads **Chapters | Folders** (see [Webtoons, manhwa and manhua](#webtoons-manhwa-and-manhua)). Under another sort (Recently added, Recently read, Recently updated) the list is flat and the switch shows **Folders**; *new in 1.31.0:* picking **Volumes** then switches the sort back to **Name** and says so ("Sorted by name for the Volumes view", with **Undo**). The read-state filters, **Favorites only** and **Hide empty folders** work in both views: in the Volumes view a volume follows the same read badge its card shows (**Read** = every chapter read, **Reading** = some read or in progress, **Unread** = none read) and counts as a favorite when any of its chapters is starred; missing-volume cards are hidden while a filter is on.

*New in 1.31.0:* tapping a series on the home page's **New chapters** row opens a linked series in its Volumes view (sorted by name, with the **Continue** row on top showing the chapter to read next), unless you chose **Folders** for it. A series without a link, or without a Volumes view, opens as before, sorted by **Recently updated**.

## How chapters are placed in a volume

For every chapter archive, the first rule that applies decides:

1. **The file name or ComicInfo says so.** `Title v03 c012` is chapter 12 of volume 3. An archive named only `Chapter 12` with ComicInfo *Volume* 3 is in volume 3 too (a ComicInfo volume that is a year, such as 2019, is ignored).
2. **The volume list places it.** For a series linked to a record, the volume list stored with it says which chapters make up each volume.
3. **Between two known volumes.** When exactly one volume is missing from the list between two known ones, the chapters in between belong to it. A chapter inside a known volume's own span belongs to that volume.
4. **An estimate.** When several volumes are unknown, their chapters are split evenly; after the last known volume, chapters are placed in steps of the average chapters per volume, never past the last volume the series is known to have. A stack that uses an estimate is shown as **~ Volume 12**.
5. **Between two neighbouring volumes.** A chapter the list places in neither of two consecutive volumes (volume 1 ends at chapter 10, volume 2 starts at 12, and you have chapter 11) goes at the end of the first one.
6. **Otherwise it stays loose**, listed after the volumes as a normal chapter ("not in a volume yet"). That is where the chapters after the newest published volume of a running series end up.

Extras (a fractional chapter such as `c045.5`) follow their whole chapter into its volume. A **split chapter** is different: when the volume list names only parts of a chapter (`4.1`, `4.2`) and not chapter 4 itself, the parts are chapter 4, and it is complete when all its parts are here (a `c004` file also covers them). It works the other way round too: when the volume list names chapter 4 but your files are its parts (`4.1`, `4.2`, `4.3`), those files are chapter 4, not extras. A file `c003` next to `c003.2` is chapter 3 in two parts. A lone `.5` (`c010.5`) stays an extra. Without a volume list, a fractional chapter is always an extra.

A fractional **volume** (`Series v02.5`, a bonus book) goes at the end of volume 2's stack; if volume 2 has nothing here, it keeps its own card in its place.

A folder that has no list and no linked series groups by names alone, and only when at least half of its chapters state their volume: one `v01` among two hundred bare chapters is not a grouped folder.

*New in 1.34.0:* files numbered with a running index in front of the chapter - `0003 [0001 - Chapter Title].cbz`, `0002 [0000.5].cbz` - are read by the number **in the brackets**: chapter 1, and chapter 0.5 (an extra). The title inside the brackets is just a title: `0168 [0166 - Chapter Title (Part 2)]` is chapter 166. A name whose brackets hold only a title (`001 [Chapter Title]`) is still chapter 1, and a year in the brackets (`001 [2019]`) is not a chapter.

## What the list shows

Entries are ordered **by volume**, not by file name: a chapter named `Series c019` no longer sorts before `Series v01`. The order is:

1. volume entries by volume number: a real volume file, a volume file with its chapters (one stack), or a stack of chapters;
2. subfolders that are not merged (see below);
3. loose chapters and other archives.

**Name descending** reverses the whole list; the chapters inside a stack always stay in reading order. The count above the list is the number of entries, so a stack counts once, and a stack is never split across pages.

- A real volume file with no chapters of its own volume is just its card, no stack.
- A volume file **and** chapters of the same volume become one stack; the volume file comes first and nothing is marked missing in it, because the volume holds all its chapters.
- A range file such as `Vol. 01-05` belongs to volume 1 and takes the chapters of volumes 1 to 5 with it.

## One list for a series with Volumes and Chapters folders

When a folder is linked to a series, its generic unit subfolders are merged into the **one** volume-ordered list: `Volumes`, `Vol 1-10`, `Chapters`, `Ch 1-50` (also one inside another). Their own cards are hidden in the Volumes view; the **Folders** view still shows them, and the breadcrumbs, **open containing folder** and the reader's previous / next chapter always follow the real folders.

- `Season 2`, `Part 3` and other named parts are **not** merged: they stay folders, and each groups inside itself using the series' volume list.
- A subfolder linked to a series of its own, side material (`Extras`, `Specials`, `Colored`...) and a subfolder that holds another folder stay folders.
- If the numbering restarts, for example two folders that both start at chapter 1, nothing is merged, and a `Season` folder whose numbering restarts is never grouped. *1.31.1:* `Episode 3` (or `Ep 3`) in a file name that also states a volume - `Title - Episode 3 - Arc Title v01` - names a part of the series like `Part 3`, not a chapter, so arc folders whose volumes start again at 1 count as a restart too.
- A folder that is not linked (or is marked **Don't match**, or is a [collection about a series](series-information.md#collections-about-a-series)) never merges its subfolders; it only groups its own files by their names.

## Webtoons, manhwa and manhua

*New in 1.34.0.* Webtoons rarely come in volumes, and the sites MangaPixer reads seldom say which chapters a webtoon volume holds. So for a folder linked to a series that MangaUpdates marks as a **webtoon**, or to a **manhwa** or **manhua**, the view lists the **chapters** instead of guessing volumes - unless a real volume list exists (two or more volumes on MangaDex, or a Wikipedia chapter list):

- The switch reads **Chapters | Folders**. **Chapters** lists the series' chapter files in chapter order (files without a chapter number at the end), merged `Chapters` subfolders included.
- No **Volume N - Missing** cards, no estimated `~ Volume N` stacks, and the series status never says "volumes missing": it says which chapters you have and which are missing ("You have chapters 1-311 · 2 chapters missing"), where MangaPixer knows what is released.
- Files whose names state their volume (`Title v02 c015`) still group into volumes, as above.
- The completion mark works as before, by chapters.

A series with a real volume list (a manhwa that MangaDex lists in volumes, for example) keeps its volume stacks.

## Missing volumes and chapters

"Missing" means **released in your preferred language** - **Preferred language (covers and releases)** in **Metadata Manager** > **Settings** (English by default). A volume or chapter that exists only in the original language is never marked missing.

**Chapters.** When the volume list says a volume holds chapters 37 to 46 and one is not on your shelf, the stack gets an **incomplete mark** ("8/9": complete chapters you have out of the chapters the volume holds) and, inside, a **dashed placeholder** ("Ch. 39, Missing") where the chapter belongs. Placeholders aren't clickable; they only show what is missing.

- **A gap below your highest chapter** is missing: a later chapter is here, so this one exists.
- **After your highest chapter**, a chapter is missing only when the series' volume list says it is released in your preferred language. When nothing says so, nothing after your last chapter is marked.
- **Extras are never missing.** `c045.5` next to a listed chapter 45 is shown between 45 and 46 but never fills chapter 45 and is never counted as missing.
- **Parts of a split chapter** are: with `5.1` and `5.3` here, `5.2` is missing, and chapter 5 is not complete. The same holds for parts on disk of a chapter the list names whole: with `4.1` and `4.3` here, `4.2` is missing.
- A volume file covers its whole volume, so a stack with a volume file has no placeholders.
- Estimated volumes mark missing chapters against their estimated range, so treat those marks as hints.

**Duplicate chapters** (*new in 1.31.0*). When two files of a volume state the same chapter - the same chapter uploaded twice - the stack counts it once ("8/9" is eight different chapters, never eight files) and says so: a small **2 duplicates** mark on the card, and under the counts on the stack page, for example "2 duplicate chapters: Chapter 1: 2 files, Chapter 2: 2 files". Each file keeps its own card, marked "Ch. 1 · 2 files". Nothing is hidden, merged or removed. The parts of a split chapter (`5.1` and `5.2`) are not duplicates. See [Duplicate numbers](missing-report.md#duplicate-numbers).

**Volumes.** In a folder linked to a series, a whole volume with neither a volume file nor any chapter here shows as a dashed **Volume N - Missing** card in its place: the gaps below your highest volume, and the volumes after it that are released in your preferred language. Today MangaPixer knows the volume total only for English (the English publisher's total on the series record), so in another language only the gaps are shown. A `Season` or `Part` subfolder never shows missing volumes (the rest of the run is elsewhere).

**Series status.** Above the list of a linked series the Volumes view says how the series stands and what you have - see [What you have](#what-you-have) below. "Up to date" is said only when MangaPixer knows what is released in your language. The counts include gaps between loose chapters that no volume holds yet.

The numbers agree with the [Missing volumes and chapters](missing-report.md) report: both are worked out the same way, with the same rules for extras, ranges, restarts and your preferred language.

## What you have

*New in 1.30.0.* Above the list of a linked series, two lines say how the series stands and what your folder holds:

- **Ongoing (Japan): 22 volumes · English (Yen Press): 14 volumes, ongoing · English chapters: to chapter 65**
- **You have volumes 1-14 + chapters 47-65 · up to date**

The first line keeps track of each kind of release separately:

- **The original run**: the publication status and the volume (or chapter) total in the country of origin. It is context only: a volume that exists only in the original language is never missing.
- **The official release in your preferred language**: the publisher, how many volumes (or, for a publisher that releases chapter by chapter, chapters) it has out, and its own status - ongoing, complete, on hiatus, or **dropped** when the publisher stopped. Today MangaPixer knows this only for English (the English publishers on the MangaUpdates record). Omnibus, 2-in-1 / 3-in-1 and perfect editions are named but not compared volume by volume, because their volume numbers are not the original's. "English: not licensed" is shown when MangaUpdates says so.
- **The released chapters**: for English, the latest released chapter from MangaUpdates (", complete" once MangaUpdates lists every chapter as released in English, whether by a fan translation or an official chapter release); for another language, the chapters MangaDex lists as released in it ("French: to chapter 87").

The second line merges your **volume files and chapter files into one range** through the series' volume list. If Volume 10 holds chapters 81 to 90 and you also have chapter files 85 to 120, you have "volumes 1-10 + chapters 91-120": chapters 85 to 90 count once. Those chapter files show **Also in Volume 10** on their cards, in both the Folders and the Volumes view. When the volume list only estimates which chapters a volume holds, the overlap is not claimed and every chapter file is listed.

**Official volumes you hold as chapters.** A volume that is out officially in your preferred language, but that you have only as chapter files, is an **upgrade**, not a gap: its stack shows **Available in English** and the second line says "Volume 15 available in English". It is never counted as missing and never makes a series "behind". Metadata Manager marks these series **Upgrade available** in the [Completion](official-releases.md) tab.

**Finished.** When a series has ended where it comes from (complete or cancelled there) and your folder holds all of it, the line says **finished - you have it all** with the edition instead of "up to date" - for example "You have volumes 1-14 · finished - you have it all (Official)". The edition is, in this order: the official edition in your language, complete, with every volume here; every chapter out in your language, with every chapter here (**Official chapters** when an official chapter-by-chapter publisher covers it, else **Chapter-based**); or the whole original run. A volume you have as a complete run of chapters counts as held when the series' volume list names its chapters exactly - not from an estimate - and your chapters reach the last chapter anything knows about (a volume list made from translations can name only the translated chapters of the last volume). A series that has ended and that you don't hold whole says **finished - missing some** with how many you have ("Official, 12 of 14"). A finished English edition of a series that still runs in the original is not "finished": only the original run says nothing more will come. The [Completion](official-releases.md) tab gives every linked series its answer.

Records fetched before 1.30.0 don't carry the English publisher's own status yet; it is read on the record's next refresh (or when an admin refreshes the series). Until then MangaPixer treats an English edition as complete when the series is complete in the original language and every original volume is out in English.

## Opening a stack

Tap a stack to open the volume: its cover, "Volume 3", how many chapters you have of how many (and how many extras), where the grouping came from, **Previous** and **Next** volume, and the chapters in reading order with the read state, favorite star and series information button of a normal card. Tapping a chapter opens the reader as usual; the reader's previous / next chapter follow the folder, not the stack.

The chapters follow your library view: as cards, or - when your library view is **List** (the **View** menu on the folder list) - as the same compact rows the folder list uses, with the columns you chose there. A missing chapter is a compact dashed row in the list.

### Selecting in a volume

Choose **Select** on the volume page to pick chapters, exactly as in the folder list:

- Tap a chapter (on a phone too) to select it instead of opening it. **Shift**-click selects a range; on a touch screen a long press offers **Select to here**. In **List** view the checkbox on any row selects without choosing **Select** first.
- The bar shows what you picked and offers **Select** (**Select all**, **Select all unread**, **Select all read**), **Mark read**, **Mark unread**, **Favorites** (**Add to favorites** / **Remove from favorites**) and **Done**. Admins also get the **Series** menu and **Cover…** for the archives.
- A missing chapter can't be selected.

### Selecting a whole volume

In the folder list, **Select** also lets you pick a stack: tap it (or tick its row in **List** view). **Mark read**, **Mark unread** and **Favorites** then apply to **every archive in the volume** - chapters, a volume file and extras - in one step, and the stack's badge and star follow. A stack shows a star when any chapter in it is starred. Actions that belong to a real folder or archive (**Direction**, **Series**, **Cover…**, **View…**) skip stacks; open the volume to use them on its chapters. A missing-volume card can't be selected.

## Turning it on and off

The Volumes view is on wherever there is something to group. Who decides, first match wins:

1. **You**, with the Volumes | Folders switch. Your choice applies to every series until you pick what the folder shows by default: then it is cleared, and the admin's settings below decide for you again.
2. **An admin, per folder**: select one folder in the browse list, choose **View…** in the selection bar, then **Automatic**, **On** or **Off**.
3. **An admin, per library**: **Metadata Manager** > **Settings** > **Libraries**, the **Volumes view** column (**Default**, **On**, **Off**).
4. **An admin, globally**: **Metadata Manager** > **Settings** > **Volumes view**, **Group chapters into volumes by default** (on by default).

The Volumes view only reads what is already stored, so it keeps working when fetching from the web is off or a consent has to be renewed; a series simply has no stored volume list until one is fetched.

## Where the volume list comes from

For a series linked to a record, MangaPixer can fetch the volume list, and the covers of each volume, from [MangaDex](https://mangadex.org) when **Fetch from the web** and **Automatic matching** are on. That is described under [Series information](series-information.md); the credit for those covers and lists is in **Metadata Manager** > **Settings**. Nothing in this page needs it: file names and ComicInfo group on their own. While some of a series' covers are still being downloaded in the background, opening its Volumes view says so in a short message; the covers appear as they arrive. *New in 1.31.0:* an estimated `~ Volume N` stack shows the web cover of volume N as well - the cover follows the label, so an estimate that is off by one shows the neighbouring volume's cover until MangaDex's volume list places those chapters (see [Covers](covers.md)).

*New in 1.34.0:* a MangaDex list that is nearly empty - at most one real volume (a volume "0" does not count) while more chapters are "not in a volume yet" than placed - is treated as **no volume list**: nothing groups from it, and the other sources apply as when MangaDex has no list (Wikipedia, or with **Automatic matching** the AniList totals). This is decided when the stored list is read, so it applies at once to lists already stored.

### Completed from Wikipedia

*New in 1.32.0.* MangaDex's list sometimes has gaps: the newest volumes of a running series, or chapters it leaves "not in a volume yet". Where it does, MangaPixer can read the series' English Wikipedia "List of ... chapters" page and fill the gap - a volume MangaDex lacks, or the chapters of a volume it lists only in part. The stacks that were estimated (`~ Volume 16`) become exact where the page places their chapters.

- **Only where it can add something.** A series whose MangaDex list is complete is never asked; neither is a folder that is not linked to a series.
- **Where MangaDex places a chapter, MangaDex stays.** Wikipedia only fills what MangaDex does not place, and a list is used only after it passes its checks: the chapter numbers increase from volume to volume, it names no more volumes than the series has, and nearly all the chapters both lists place sit in the same volume. A page that fails them (a spin-off's list, a vandalised edit) is not used; the last good list stays.
- **How it is found.** The series' MangaUpdates record number goes to [Wikidata](https://www.wikidata.org), which names the English article; or, when Wikidata has none, the series' MangaUpdates title is tried as a "List of ... chapters" page. Never a folder or file name.
- **When.** With **Fetch from the web** and **Automatic matching** on, in the background with the volume-cover work (it pauses when **Volume covers from the web** is off), and when an admin refreshes the series. After the first time it asks one cheap question - has the page changed? - on the series' refresh schedule (every 30 days while the series is ongoing, every 90 once it is finished) and reads the page again only when it has.
- **What is kept.** Volume and chapter numbers, each volume's English release date and ISBN, the page title and its revision. No chapter titles or summaries.
- **Credit.** Wherever such a list is shown - the series status line of the Volumes view, a volume's page, the [Missing report](missing-report.md) - a small line says "Volume list completed from Wikipedia" and links the page. Wikipedia is one of the allowed sites in **Metadata Manager** > **Settings**; removing it there stops all of it.
