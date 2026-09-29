# Volumes view

A manga that comes as loose chapters, or as a `Volumes` folder next to a `Chapters` folder, is easier to read as **volumes**. The **Volumes view** groups a series' chapters into **volume stacks** ("Volume 3 - 10 chapters"), orders everything by volume, and shows where a chapter is missing. Your files are never touched: the view is built from your archive names, their embedded ComicInfo and, for linked series, the volume list stored with the series record.

It works without any network access. Names like `Series v03 c012` group on their own; a stored volume list only adds the volumes your file names don't state.

## Where to find it

Open a series folder that has something to group. In the top bar a **Volumes | Folders** switch appears next to the series information button. **Volumes** is the grouped view; **Folders** is the real folder list with every file and subfolder as it is on disk. Your choice is remembered for you.

The switch is only shown where a Volumes view exists. It is greyed out while another sort (Recently added, Recently read, Recently updated), a read-state filter or **Favorites only** is active: those views are about single items, so they always list them flat. **Hide empty folders** works in both views.

## How chapters are placed in a volume

For every chapter archive, the first rule that applies decides:

1. **The file name or ComicInfo says so.** `Title v03 c012` is chapter 12 of volume 3. An archive named only `Chapter 12` with ComicInfo *Volume* 3 is in volume 3 too (a ComicInfo volume that is a year, such as 2019, is ignored).
2. **The volume list places it.** For a series linked to a record, the volume list stored with it says which chapters make up each volume.
3. **Between two known volumes.** When exactly one volume is missing from the list between two known ones, the chapters in between belong to it. A chapter inside a known volume's own span belongs to that volume.
4. **An estimate.** When several volumes are unknown, their chapters are split evenly; after the last known volume, chapters are placed in steps of the average chapters per volume, never past the last volume the series is known to have. A stack that uses an estimate is shown as **~ Volume 12**.
5. **Otherwise it stays loose**, listed after the volumes as a normal chapter ("not in a volume yet"). That is where the chapters after the newest published volume of a running series end up.

Extras (a fractional chapter such as `c045.5`) follow their whole chapter into its volume.

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

## Missing chapters

When the volume list says a volume holds chapters 37 to 46 and one is not on your shelf, the stack gets an **incomplete mark** ("8/9": whole chapters you have out of whole chapters the volume holds) and, inside, a **dashed placeholder** ("Ch. 39, Missing") where the chapter belongs. Placeholders aren't clickable; they only show what is missing.

- **Extras are never missing.** `c045.5` is shown between 45 and 46 but never fills chapter 45 and is never counted as missing.
- **The last volume of a running series** doesn't mark the chapters after your last one: they may not exist yet. Inside any other volume they are marked.
- A volume file covers its whole volume, so a stack with a volume file has no placeholders.
- Estimated volumes mark missing chapters against their estimated range, so treat those marks as hints.

The numbers agree with the [Missing volumes and chapters](missing-report.md) report: both use the same rules for extras, ranges and restarts.

## Opening a stack

Tap a stack to open the volume: its cover, "Volume 3", how many chapters you have of how many (and how many extras), where the grouping came from, **Previous** and **Next** volume, and the chapters in reading order with the read state, favorite star and series information button of a normal card. Tapping a chapter opens the reader as usual; the reader's previous / next chapter follow the folder, not the stack.

A stack can't be selected or starred: open it to select or star its chapters.

## Turning it on and off

The Volumes view is on wherever there is something to group. Who decides, first match wins:

1. **You**, with the Volumes | Folders switch.
2. **An admin, per folder**: select one folder in the browse list, choose **View…** in the selection bar, then **Automatic**, **On** or **Off**.
3. **An admin, per library**: **Metadata Manager** > **Settings** > **Libraries**, the **Volumes view** column (**Default**, **On**, **Off**).
4. **An admin, globally**: **Metadata Manager** > **Settings** > **Volumes view**, **Group chapters into volumes by default** (on by default).

The Volumes view only reads what is already stored, so it keeps working when fetching from the web is off or a consent has to be renewed; a series simply has no stored volume list until one is fetched.

## Where the volume list comes from

For a series linked to a record, MangaPixer can fetch the volume list, and the covers of each volume, from [MangaDex](https://mangadex.org) when **Fetch from the web** and **Automatic matching** are on. That is described under [Series information](series-information.md); the credit for those covers and lists is in **Metadata Manager** > **Settings**. Nothing in this page needs it: file names and ComicInfo group on their own.
