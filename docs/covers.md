# Covers

Every card in MangaPixer shows a cover: an archive shows page 1 of the archive, and a folder shows the cover of its first archive (by name). Page 1 is not always the right picture, though. A volume scanned with its dust jacket unfolded shows back, spine and front side by side, a scanlated one-shot often starts with a credit page, and a webtoon's page 1 is a thin strip of the first panel.

MangaPixer 1.29.0 adds a **cover layer** on top of the file covers. It picks a better picture where it can, and admins can choose a cover for any folder or archive. **Your files are never changed**: the file cover stays what it is, and every other cover is stored in MangaPixer's own data folder.

## What decides a card's cover

The first of these that applies wins:

1. **An admin's choice** for this folder or archive (see [Choose a cover](#choose-a-cover-admins)).
2. **"This file's cover"**, when an admin pinned it. It also switches off everything automatic for that item.
3. **The automatic cover**, described below.
4. **The file cover**: page 1 of the archive, or of a folder's first archive.

A folder that has no cover of its own shows the cover of its first archive, including that archive's automatic cover. So when volume 1's jacket is cropped, the series card shows the cropped front too.

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
| A series folder | The **volume 1** cover from the web - also when the folder holds chapters or does not have volume 1. If your own volume 1 already has that cover, nothing changes. If you have volume 1 but the web has no volume 1 cover, your own cover stays. |
| A webtoon folder | The series cover from the web, else the stored series poster. |
| A `Season` / `Part` subfolder | The cover of the volume its first chapter belongs to, when that is known. |
| A volume stack of chapters (the [Volumes view](volumes.md)) | The cover of that volume from the web. A stack that holds the volume archive itself shows that archive's cover. No web cover for the volume: its first chapter's cover. |

The preferred language (**Preferred language (covers and releases)**) is set in **Metadata Manager** > **Settings**. When a volume has no cover in that language yet, the cover from the country of origin is used and MangaPixer looks again later.

Two switches hide the stored web covers without deleting them:

- **Volume covers from the web** (Metadata Manager > Settings) for the whole server;
- **Show saved web covers** per library (Metadata Manager > Settings > **Covers**). This is separate from **Show series information**: you can hide descriptions and keep the covers, or the other way round.

**Delete stored volume covers** (Metadata Manager > Settings > **Covers**) removes the downloaded covers and every automatic or chosen cover that used them. Those items show their own covers again.

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
