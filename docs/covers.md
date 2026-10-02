# Covers

Every card in MangaPixer shows a cover: an archive shows page 1 of the archive, and a folder shows the cover of its first archive (by name). Page 1 is not always the right picture, though. A volume scanned with its dust jacket unfolded shows back, spine and front side by side, a scanlated one-shot often starts with a credit page, and a webtoon's page 1 is a thin strip of the first panel.

MangaPixer 1.29.0 adds a **cover layer** on top of the file covers. It picks a better picture where it can, and admins can choose a cover for any folder or archive. **Your files are never changed**: the file cover stays what it is, and every other cover is stored in MangaPixer's own data folder.

## What decides a card's cover

The first of these that applies wins:

1. **An admin's choice** for this folder or archive (see [Choose a cover](#choose-a-cover-admins)).
2. **"This file's cover"**, when an admin pinned it. It also switches off everything automatic for that item.
3. **The automatic cover**, described below. Its **web** part (a stored volume cover, the series cover, the poster) is used only where the folder's [cover preference](#choose-what-a-folder-shows-admins) and the library allow it.
4. **The file cover**: page 1 of the archive, or of a folder's first archive.

A folder that has no cover of its own shows the cover of its first archive, including that archive's automatic cover. So when volume 1's jacket is cropped, the series card shows the cropped front too. A folder linked to a series shows **its volume 1** instead of its first archive by name (see the table below), so a `Chapters` subfolder that sorts first does not put chapter 1's page on the series card.

## Automatic covers

### Jacket spreads (on this server, no request)

When page 1 of a volume is clearly wider than tall (a jacket spread), MangaPixer shows the **front half** instead. Which half is the front depends on the reading direction: a left-to-right book has its front on the right, a right-to-left manga on the left. MangaPixer takes the direction from:

1. the folder's or the library's reading direction (**Right to left** or **Left to right**), when an admin set one;
2. the archive's ComicInfo `Manga` value;
3. the country of origin of the linked series (Japan reads right to left);
4. otherwise left to right.

The crop is made by MangaPixer itself when the cover thumbnail is created, so it works in libraries that never go online. It applies to volumes (`Title v03`, or a ComicInfo volume), not to chapters: a chapter that opens with a double page is not a jacket.

Turn it off with **Crop jacket spreads** in **Metadata Manager** > **Settings** > **Covers**.

### Covers from the web

For a folder linked to a series, MangaPixer can also use covers it downloaded from the web, together with the series' volume list: see [Series information](series-information.md) for when these are fetched, from which site, and what is sent. Once stored, they are used like this:

| Item | Cover |
|---|---|
| A volume archive | Its own page 1 (or the front half of a spread) - unless the web cover of that volume is **clearly a different picture**. Close but not the same (another edition's logo) keeps your own cover. No web cover for that volume: your own cover, never another volume's. |
| A one-shot (a work of one archive) | The web cover of the work by default, because scanlated one-shots often start with a credit page. Your page 1 is kept when it is already the same picture. No crop. |
| A series folder | *Changed in 1.30.0.* When you have **volume 1** (a volume 1 archive anywhere in the series folder, also next to a `Chapters` subfolder): exactly the cover volume 1's own card shows - its page 1, the front half of its jacket, or the web volume 1 cover when that is clearly a different picture (see the volume row above). Without a volume 1 of your own: the **volume 1** cover from the web, else the series cover from the web, else the stored series poster, else the first archive's cover. |
| A webtoon folder | The series cover from the web, else the stored series poster. |
| A `Season` / `Part` subfolder | The cover of the volume its first chapter belongs to, when that is known. |
| A volume stack of chapters (the [Volumes view](volumes.md)) | The cover of that volume from the web. A stack that holds the volume archive itself shows that archive's cover. No web cover for the volume: its first chapter's cover. *New in 1.30.0:* the cover of a volume is also downloaded when you have **all of its chapters** (every chapter MangaDex's volume list gives that volume), so complete chapter stacks get their real volume cover. *New in 1.31.0:* an **estimated** `~ Volume N` stack (its chapters placed by the average chapters per volume, because MangaDex's volume list does not name them) shows the web cover of the volume it is labelled with too: the `~` marks the estimate, and when an exact volume list arrives later the stacks and their covers regroup. If the estimate is off by one, the stack shows the neighbouring volume's cover until then. A volume the list names that you have only part of keeps its first chapter's cover. |

*New in 1.32.0:* **comics keep their own cover.** A folder or archive linked to a [Grand Comics Database](series-information.md#comics-the-grand-comics-database) series never gets a cover from the web: Western comics and albums almost always start with their real cover, and GCD's cover scans may only be used to identify a comic. Its thumbnail is shown in Identify's preview while you choose, and is never stored as a card, series or volume cover.

The preferred language (**Preferred language (covers and releases)**) is set in **Metadata Manager** > **Settings**. When a volume has no cover in that language yet, the cover from the country of origin is used and MangaPixer looks again later.

Three settings decide whether the stored web covers are shown, without deleting them:

- **Volume covers from the web** (Metadata Manager > Settings) for the whole server;
- **Show saved web covers** per library (Metadata Manager > Settings > **Covers**). This is separate from **Show series information**: you can hide descriptions and keep the covers, or the other way round;
- *New in 1.32.0:* the **cover preference of a folder** (below), which overrides the library switch for that folder and everything below it.

**Delete stored volume covers** (Metadata Manager > Settings > **Covers**) removes the downloaded covers and every automatic or chosen cover that used them. Those items show their own covers again.

### Choose what a folder shows (admins)

*New in 1.32.0.* A library often mixes shelves that should show web covers with shelves that should not. A folder of loose single-archive series, for example, may look better with each file's own cover than with covers matched from the web. Give such a folder a **cover preference** and it applies to the folder and **every folder below it**, the same way a folder's reading direction does.

Open it with **Library** > **Select**, select exactly **one folder**, then **Folder covers…** in the selection bar. Choose:

- **Inherit** (the default): follow the nearest folder above that has a preference, and above all of them the library's **Show saved web covers** switch. The line tells you what that gives, for example *Inherit (File covers from Collection)*.
- **Web covers when available**: show the saved web covers of the linked series below this folder, **even where the library hides them**.
- **File covers**: show each file's own cover. No web cover, series cover or poster is shown below this folder, and none is downloaded for it from now on.

The **nearest** folder with a preference wins, so a subfolder can say *Web covers when available* inside a *File covers* shelf, and the other way round. The library switch is the root: with **Show saved web covers** off, a folder that says *Web covers when available* still shows them, and with it on, a folder that says *File covers* does not.

What it does and does not touch:

- A cover an admin **chose** for one card (see [Choose a cover](#choose-a-cover-admins), **Covers from the web** included) still wins over the folder's *File covers*. The picker still lists web covers there. Only the library's **Show saved web covers** switch off (with no folder saying *Web covers when available*) hides a chosen web cover, as before.
- *Jacket crops* are a different setting (**Crop jacket spreads**) and are made from your own files, so they stay under *File covers*; so does a series folder showing its own volume 1.
- Under *File covers* MangaPixer stops downloading volume covers for series whose folders are all under it, so nothing new is fetched. The covers already stored stay and are shown again if you switch back. The volume **lists** (the [Volumes view](volumes.md) and the Missing report) are not affected, and neither is **Refresh** or **Choose cover** when an admin asks for it. A series linked from several folders keeps downloading while one of them shows web covers, because the covers are shared.
- The change takes effect at once for what is shown; MangaPixer then updates its automatic covers below the folder in the background. If you switch back to *Web covers when available*, the web covers come back once that has run, within moments in a small folder.
- Moving or renaming the folder keeps its preference, like its reading direction.

### On the home page

*New in 1.30.0.* A **New chapters** card of a linked series shows the series cover described above, not the newest chapter's page 1. A **Continue reading** card of a chapter in a linked series shows that series cover too (the chapter's name is under the card), when the series has a cover of its own - your volume 1, or a cover from the web. A volume keeps its own cover, and so does everything in a series that is not linked, or linked series without either.

### Nothing automatic

- Under a folder marked **Don't match** nothing automatic happens, not even the jacket crop. Choosing a cover by hand still works.
- Folders that are not linked get the jacket crop only, for volume archives.
- The reader is never affected: it always shows your pages as they are.

## Choose a cover (admins)

Open the picker in either of two ways:

- **Library** > **Select**, select exactly **one** item, then **Cover** in the selection bar. This works everywhere, also under **Don't match**, in libraries that are not linked to series and with series information hidden.
- The **(i)** of a folder or archive > **Admin** > **Choose cover…**.

The picker shows what the card uses now and why, and lets you pick:

- **Automatic** - back to the automatic cover (and remove your choice).
- **This file's cover** - always page 1 of the file (for a folder: of its first archive); switches off the automatic covers for this item.
- **Left half of page 1** / **Right half of page 1** - for a jacket spread the automatic crop got wrong.
- **Another item's cover** - for a folder: any of its archives, for example volume 4 when you like that cover best; for an archive: one of its neighbours.
- **Covers from the web** - the stored covers of the linked series, grouped by volume and language (including other editions of a volume). A cover that is not downloaded yet is shown but cannot be picked.

**Use this cover** changes the card at once. A choice is the same for every user and stays until an admin changes it.

## Cover addresses and caching

A card's cover address changes whenever its cover changes, so browsers and apps can keep a cover for a long time and still never show an old one. Covers are private: they are only cached by the browser of a signed-in user, never by a shared proxy. An old address still returns the current cover.
