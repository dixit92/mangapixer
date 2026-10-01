# Volumes view

A manga that comes as loose chapters, or as a `Volumes` folder next to a `Chapters` folder, is easier to read as **volumes**. The **Volumes view** groups a series' chapters into **volume stacks** ("Volume 3 - 10 chapters"), orders everything by volume, and shows where a chapter is missing. Your files are never touched: the view is built from your archive names, their embedded ComicInfo and, for linked series, the volume list stored with the series record.

It works without any network access. Names like `Series v03 c012` group on their own; a stored volume list only adds the volumes your file names don't state.

## Where to find it

Open a series folder that has something to group. In the top bar a **Volumes | Folders** switch appears next to the series information button. **Volumes** is the grouped view; **Folders** is the real folder list with every file and subfolder as it is on disk. Your choice is remembered for you.

The switch is only shown where a Volumes view exists: a folder whose files group into volumes, or a folder linked to a series that holds volumes (its Volumes view shows the series status, see [Missing volumes and chapters](#missing-volumes-and-chapters)). It is greyed out while another sort (Recently added, Recently read, Recently updated), a read-state filter or **Favorites only** is active: those views are about single items, so they always list them flat. **Hide empty folders** works in both views.

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
- If the numbering restarts, for example two folders that both start at chapter 1, nothing is merged, and a `Season` folder whose numbering restarts is never grouped.
- A folder that is not linked (or is marked **Don't match**) never merges its subfolders; it only groups its own files by their names.

## Missing volumes and chapters

"Missing" means **released in your preferred language** - **Preferred language (covers and releases)** in **Metadata Manager** > **Settings** (English by default). A volume or chapter that exists only in the original language is never marked missing.

**Chapters.** When the volume list says a volume holds chapters 37 to 46 and one is not on your shelf, the stack gets an **incomplete mark** ("8/9": complete chapters you have out of the chapters the volume holds) and, inside, a **dashed placeholder** ("Ch. 39, Missing") where the chapter belongs. Placeholders aren't clickable; they only show what is missing.

- **A gap below your highest chapter** is missing: a later chapter is here, so this one exists.
- **After your highest chapter**, a chapter is missing only when the series' volume list says it is released in your preferred language. When nothing says so, nothing after your last chapter is marked.
- **Extras are never missing.** `c045.5` next to a listed chapter 45 is shown between 45 and 46 but never fills chapter 45 and is never counted as missing.
- **Parts of a split chapter** are: with `5.1` and `5.3` here, `5.2` is missing, and chapter 5 is not complete. The same holds for parts on disk of a chapter the list names whole: with `4.1` and `4.3` here, `4.2` is missing.
- A volume file covers its whole volume, so a stack with a volume file has no placeholders.
- Estimated volumes mark missing chapters against their estimated range, so treat those marks as hints.

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

**Official volumes you hold as chapters.** A volume that is out officially in your preferred language, but that you have only as chapter files, is an **upgrade**, not a gap: its stack shows **Available in English** and the second line says "Volume 15 available in English". It is never counted as missing and never makes a series "behind". Metadata Manager lists these series in the [Official releases](official-releases.md) tab.

**Complete collection.** When a series is finished and your folder holds all of it, the line says **Complete collection** instead of "up to date". Finished means, in this order: the official edition in your language is complete and you have every volume of it; every chapter is out in English and you have every chapter (**Chapter-based**); or the original run has ended and you have all of its volumes. A volume you have as a complete run of chapters counts as held when the series' volume list names its chapters exactly - not from an estimate - and your chapters reach the last chapter anything knows about (a volume list made from translations can name only the translated chapters of the last volume). A series finished in your language that you don't hold whole says "finished in English" next to what is missing.

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

For a series linked to a record, MangaPixer can fetch the volume list, and the covers of each volume, from [MangaDex](https://mangadex.org) when **Fetch from the web** and **Automatic matching** are on. That is described under [Series information](series-information.md); the credit for those covers and lists is in **Metadata Manager** > **Settings**. Nothing in this page needs it: file names and ComicInfo group on their own. While some of a series' covers are still being downloaded in the background, opening its Volumes view says so in a short message; the covers appear as they arrive.
