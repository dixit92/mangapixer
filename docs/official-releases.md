# Official releases

*New in 1.30.0.* Which of your series have a new official volume out in your language, and which finished series do you already have whole? The **Official releases** tab answers that for every folder linked to a series.

It is built from what MangaPixer has already stored: your archive names, the series records and the volume lists it fetched when the folders were linked or last refreshed. **Opening it never contacts a website.** It changes when records refresh (on each series' [refresh schedule](scheduled-jobs.md#how-often-series-information-is-refreshed) - every month for an ongoing series unless you chose more often), or when an admin refreshes a series.

## Where to find it

**MangaPixer Administration** > **Metadata Manager** > **Official releases**. Admins only. The [Missing](missing-report.md) tab links to it when some series have official volumes you hold only as chapters.

## What it lists

- **Upgrades**: volumes released officially in your preferred language that your folder holds only as chapter files (scanlations), for example "Volumes 15-19 available in English - you hold them as chapters". These are never counted as missing: you can already read them; the official volume is an upgrade. The same volumes show **Available in English** on their stacks in the [Volumes view](volumes.md#what-you-have).
- **Finished, not complete**: series finished in your language - the official edition is complete, or every chapter is out in English - that your folder does not hold whole, for example "Finished - Official, English (14 volumes) - you have 12" or, for a finished chapter release, "Finished - Chapter-based, English (172 chapters) - you have 6".

Below the list filter, **Any / Official / Chapter-based / Original run** narrows finished series and complete collections to what they are finished by: the official edition in your language, every chapter released in English, or the original run. The mark says the same: **Complete collection - Official**, **Complete collection - Chapter-based** or **Complete collection - Original run**. **Chapter-based** means MangaUpdates lists the series as completely released in English chapter by chapter; that can be a fan translation or an official chapter release such as MANGA Plus, so MangaPixer does not say which.
- **Complete collections**: finished series your folder holds whole. A volume you have as a complete run of chapters counts as held; the series still appears under Upgrades while an official volume is available for it.

The filter at the top shows **To act on** (upgrades and finished series not held whole) by default; switch to **Upgrades**, **Finished, not complete**, **Complete collections** or **All**, and use **Library** to narrow it. The line above the list counts every linked series. Upgrades come first (the most volumes to get first), then finished series you don't hold whole, then complete collections.

Each row shows the folder (it opens the series page), the linked record and library, the trackers - the original run, the official release in your language with its publisher and status, and the released chapters - and what you have. The folder button browses the folder.

## Your preferred language

Everything follows **Preferred language (covers and releases)** in **Metadata Manager** > **Settings** (English by default). Today MangaPixer knows official volume totals only for English, from the English publishers on the MangaUpdates record, so with another language there are no upgrades yet and only finished series can appear.

Omnibus, 2-in-1 / 3-in-1 and perfect editions are shown in the trackers but never compared volume by volume: their volume numbers are not the original's. A publisher that stopped the series ("Defunct" or "Dropped" on the record) shows as **dropped**; the volumes it released still count as released.

## When it is wrong

The tab only knows what the names, the volume list and the record say. An official volume counts as volume N of the original: that is true for regular editions. A wrong link gives a wrong answer - if a series looks far ahead or behind, check that its folder is linked to the right series (**Identify…** on its series page).
