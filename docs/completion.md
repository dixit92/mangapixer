# Completion

*New in 1.32.0 (the **Official releases** tab of 1.30.0, reworked).* Has each of your series ended, and do you have all of it? The **Completion** tab gives every folder linked to a series one answer, with one sentence that says why. Use it to find the series you can move from a library of ongoing series to a library of finished ones.

It is built from what MangaPixer has already stored: your archive names, the series records and the volume lists it fetched when the folders were linked or last refreshed. **Opening it never contacts a website.** Answers change when records refresh (on each series' [refresh schedule](scheduled-jobs.md#how-often-series-information-is-refreshed)), or when an admin refreshes a series. MangaPixer never moves, renames or deletes your folders: it only tells you what is ready.

## Where to find it

**MangaPixer Administration** > **Metadata Manager** > **Completion**. Admins only. Old links to the Official releases tab (`?tab=official`) open it too.

## The answers

Every linked series gets exactly one of these:

- **Finished - you have it all**: the series has ended where it comes from (complete, or cancelled there), and your folder holds the whole edition it collects. For example "Ended in Japan, and every chapter is out in English: you have all 172 chapters." These are the series you can move.
- **Finished - missing some**: the series has ended, but something is not in your folder - part of the finished edition, or volumes or chapters that are out in your language. For example "Ended in Japan, and the English edition is complete: you have 12 of 14 volumes."
- **Everything released so far**: you have everything that is out in your language, and more is still to come - the series is still running or on hiatus, it ended but is not all out in your language yet ("English volumes are still coming (10 of 12)"), or the publisher of your language's edition stopped. A series whose record does not say whether it has ended also lands here.
- **Missing some**: the series is still running, and volumes or chapters that are out in your language are not in your folder (the [Missing](missing-report.md) report lists them).
- **Can't tell**: MangaPixer cannot compare, and says why: the file names carry no volume or chapter numbers, the numbers start again in subfolders, nothing is known about what is out in your language, or your folder holds volumes of a running series and MangaPixer does not know yet how many volumes are out in your language.

"Missing" always means released in your preferred language, as everywhere in MangaPixer: a volume that exists only in the original language is never missing.

A one-shot - a series of one volume that has ended - counts as held whole even when its file name carries no number.

## Which edition

The two **Finished** answers name the edition the series is finished in:

- **Official**: the official edition in your language is complete, and the comparison is volume by volume.
- **Official chapters**: every chapter is out in your language from an official publisher that releases chapter by chapter (MANGA Plus, for example).
- **Chapter-based**: every chapter is out in your language - MangaUpdates lists the series as completely released in English chapter by chapter. That can be a fan translation or an official chapter release, so MangaPixer does not say which unless the publisher is known.
- **Original run**: you have every volume (or chapter) of the original run.
- **One-shot**: see above.

A folder of volume files is judged by the volume edition: while your language's volumes are still coming, a series whose chapters are all out reads **Everything released so far**, not "missing some". A folder of chapter files is judged by the chapter release.

## Upgrade available

A volume that is out officially in your language, but that you have only as chapter files, is an **upgrade**, never a gap. It never changes the answer - a series can be ready to move and still have an upgrade - so it is a separate mark, **Upgrade available**, with a line such as "Volumes 15-19 available in English - you hold them as chapters". Turn on **Upgrades only** to list just those series. The same volumes show **Available in English** on their stacks in the [Volumes view](volumes.md#what-you-have).

## Using it

- The filter at the top shows one answer at a time, with how many series have it: **Have it all** (the default), **Finished, missing some**, **Everything so far**, **Missing some**, **Can't tell** or **All**.
- **Library** narrows the list to one library. To see what is ready to move, pick your library of ongoing series and **Have it all**. To check a library of finished series, pick it and look at **Finished, missing some**, **Everything so far** and **Can't tell**. Bookmark the page: the address keeps the library.
- **Edition** (shown with the two Finished answers) narrows them to one edition.
- Each row shows the folder (it opens the series page), the answer and its edition, why, the linked record and library, the trackers - the original run, the official release in your language with its publisher and status, and the released chapters - and what you have. The folder button browses the folder. A series linked by automatic matching says **automatic link**: check the link before you move it.
- Have it all and Everything so far are sorted by name; the two missing answers list the series closest to whole first.

## Moving a finished series

Move the folder yourself, on disk, to the other library's folder. On the next scan MangaPixer recognizes it there: its series link, reading progress and folder settings follow it. See [Moving series between libraries](library-layout.md#moving-series-between-libraries).

## Your preferred language

Everything follows **Preferred language (covers and releases)** in **Metadata Manager** > **Settings** (English by default). Today MangaPixer knows official volume totals only for English, from the English publishers on the MangaUpdates record. For another language, a series is finished in that language's chapters when the released chapter list reaches the last chapter of the original run.

Omnibus, 2-in-1 / 3-in-1 and perfect editions are shown in the trackers but never compared volume by volume: their volume numbers are not the original's. A publisher that stopped the series ("Defunct" or "Dropped" on the record) shows as **dropped**; the volumes it released still count as released.

## When it is wrong

- **Can't tell** for a folder of volumes usually means the series record does not carry the English volume total yet. Records fetched before 1.30.0 learn it on their next refresh, or when an admin refreshes the series.
- A file name without a number ("Special Edition") cannot be placed. Numbered names (`v01`, `c012`) or ComicInfo volume / number fields fix it.
- The tab only knows what the names, the volume list and the record say. An official volume counts as volume N of the original: that is true for regular editions. A wrong link gives a wrong answer - if a series looks far ahead or behind, check that its folder is linked to the right series (**Identify…** on its series page).
