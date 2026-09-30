# Missing volumes and chapters

Which series are you behind on, and which volumes are missing from your shelf? The **Missing** report answers that for every folder linked to a series. It compares the volumes and chapters in your folders - volume files and chapter files merged into one range through the series' volume list, as the [Volumes view](volumes.md#what-you-have) does - with what the linked series record says is released in your language.

The report is built from what MangaPixer has already stored: your archive names, the series records and volume lists it fetched when the folders were linked or last refreshed. **Opening it never contacts a website.** Totals update when records refresh.

## Where to find it

- **MangaPixer Administration** > **Metadata Manager** > **Missing**. Admins only.
- On a series page, admins also see a short line under the buttons, for example "You have volumes 1-7 of 10 (English) · 3 behind", with a link to the report.

The report shows **Behind or with gaps** by default. Switch to **All linked series** to see everything, and use **Library** to narrow it to one library. The line above the list counts every linked series: how many are behind, how many have gaps, how many are up to date, how many have no known total and how many can't be compared, and - *new in 1.30.0* - how many have official volumes you hold only as chapters (the link opens the [Official releases](official-releases.md) tab). Each series name opens its series page, and the folder button browses the folder. Each row also says what you have ("You have volumes 1-14 + chapters 47-65") and shows the **Complete collection** mark of a finished series you hold whole.

## What counts

- **Linked folders only.** A folder counts if it is linked to a series, either by an admin (**Identify…** or an accepted review) or by automatic matching. A folder that only inherits a link from a parent folder, a folder marked **Don't match**, a folder still waiting in **Review** and a linked single archive are not listed.
- **The archives in the folder**, plus those in its volume and chapter subfolders: names like `Volumes`, `Vol 1-10`, `Chapters`, `Ch 1-50`, `Season 2`, `Part 3`, `Book 1` or just `12`, also one inside another (`Season 1/Volumes`, up to three levels down). Subfolders of side material (`Extras`, `Specials`, `Side Stories`, `Oneshots`, `Omake`, `Raws`, `Colored`) and subfolders with another name (another work) are not read. A subfolder linked to a series of its own, or marked **Don't match**, belongs to itself.
- **The numbers in the archive names**, read the same way automatic matching reads them: `Title v03` and `Title Vol. 3` are volume 3, `Title - Chapter 012` and `Title c012` are chapter 12, `Title v03 c012` is chapter 12 (of volume 3), and `Title Vol. 01-05` covers volumes 1 to 5. In a `Volumes` folder a bare `01.cbz` is volume 1; elsewhere a bare number is a chapter. Archives whose names state no number are left out.
- **Extras never count as missing.** A `.5` chapter or volume (`c045.5`, `v02.5`, usually an extra) is shown where it belongs but never fills a number and is never missing: `c045.5` without `c045` still leaves chapter 45 missing.
- **Chapter 0 alone is not progress.** A lone `000.cbz` or prologue does not make "You have chapter 0 of 223"; with no other numbered archive the series has **No numbers**.
- **Numbering that starts again.** When `Season 1` and `Season 2` (or `Part 1` and `Part 2`) both start from chapter 1, or the same numbers appear in two subfolders, the numbers on disk can't be compared with one total. The series gets no verdict (**Numbering restarts**) instead of a wrong count. Seasons that carry on the numbering (`Season 2` starts at 101) count as one run.
- **Volume files and chapter files are merged** (*new in 1.30.0*). With the series' volume list, the chapters inside your volume files count as held, so chapter files that repeat them are counted once and nothing inside a volume file is ever missing. This works for a folder that mixes volume and chapter files as well as for separate `Volumes` and `Chapters` subfolders (a mixed folder was not compared before 1.30.0). Without a volume list, chapters kept next to volumes usually carry on from the last volume, so only numbers from the lowest chapter you have upward are checked for gaps.
- **An official volume you hold as chapters is an upgrade, never missing.** When the English edition has volume 15 out and you have its chapters as chapter files, the series is not behind; the row says "Volume 15 available in English (an upgrade, not missing)".
- **Chapters are compared only when you keep chapter files.** A folder of volume files is never "behind" a scanlation.

## Which total it compares with

**Behind means behind what is released in your preferred language** - **Preferred language (covers and releases)** in **Metadata Manager** > **Settings** (English by default). A volume that exists only in the original language is never counted as missing.

MangaPixer uses the first of these totals that applies to your language:

| Total | Language | Confidence | Shown as |
|---|---|---|---|
| The English publisher's count ("10 Volumes / 60 Chapters; Ongoing") | English | high | (English) |
| The other unit's English total converted with AniList's chapters per volume (see below) | English | medium | ~ (estimate) |
| The chapters the series' volume list names as released in your language, for chapters only | any | medium: includes fan translations | (released in your language) |
| The latest released chapter, for chapters only | English | low: scanlation releases, not an official total | (latest release) |

The count in the country of origin ("14 Volumes (Complete)", "195 Chapters") is **never** used for Behind; it is shown next to the line as context ("original run: 14"). For another language than English there is no volume total yet, so the volumes of a series get no Behind verdict - only their gaps.

The (i) next to each line says which total was used. If a total is lower than the highest number you have (your copies follow another edition, or the English release is still catching up), MangaPixer moves on to the next total that covers what you have. If no total does, the series counts as up to date against the first one.

Gaps are always counted: a number below your highest one is missing whatever the language, because a later one exists.

Records linked before MangaPixer 1.28.0 don't have the English total stored yet. Until the record is refreshed, the series shows no English total and says so under the series ("An English edition is listed; its total is read on the record's next refresh").

## Chapters per volume from AniList

English editions are often counted only in volumes ("18 Volumes"), while you keep chapters, or the other way round. To compare them anyway, MangaPixer can ask [AniList](https://anilist.co) how many chapters and volumes the series has and convert with that ratio. This is the only thing AniList is used for (the Missing report and, from 1.29.0, [volume lists](series-information.md#volume-covers-and-volume-lists-mangadex)); it is never used to match a folder. It happens when you ask:

- **Chapters per volume (AniList)** under a series asks for that series.
- **Get chapters per volume from AniList** above the list asks for up to 20 linked series that don't have it yet (one request each, at most one per second). A series AniList had no match for is not asked again for a day. With **Automatic matching** and **Volume covers from the web** on, this happens in the background for every linked series MangaDex has no volume list for, so the button is not shown.

and, *new in 1.29.0*, on its own with **Automatic matching** and **Volume covers from the web** on: for a linked series that MangaDex gives no volume list for, the background volume-cover work asks AniList for its totals once, and again on the series' refresh schedule (every 30 days while it is ongoing, every 90 days once complete). Those requests count in the same daily budget and are paced at one per second.

MangaPixer sends the MangaUpdates title of the linked series (never a folder or file name), or its AniList number once it is known - an AniList number an admin entered, one already stored, or the one the series' MangaDex record links to - and keeps an entry only when its title matches the series and its start year is within a year. The entry's totals are stored on your server; the series line then shows them, for example "AniList: 116 chapters in 27 volumes, 4.3 per volume", with a link to the entry. Only a finished entry gives a ratio, because a running series' totals are not final.

With a ratio, a missing total is estimated from the other unit, marked **~** and **(estimate)**: "You have chapters 1-3 of ~116 (estimate)". The order is then: the English total of the same unit, the English total of the other unit converted, then for chapters the chapters released in your language and the latest release.

The buttons appear only while **Fetch from the web** is on and AniList is one of the [allowed sites](series-information.md#allowed-sites); with AniList removed from the list, the automatic lookups stop too. Every request counts in the daily request budget.

## Reading a line

- **You have volumes 1-7 of 10 (English) · 3 behind**: you have volumes 1 to 7 with none missing, and the English edition has 10.
- **You have 2 volumes (up to 16) of 18 (English) · 2 behind · missing 2-15**: your highest volume is 16, but only 2 volume numbers are on disk.
- **You have chapters 1-6 of 10 (latest release) · 4 behind · missing 3, 5**: gaps are listed number by number (the first 50, then "+N more").

- **You have volumes 1-3; no total known (original run: 14)**: your preferred language has no volume total, so only gaps would count.

A series is **Behind** when the total is higher than your highest number, **Gaps** when it isn't behind but numbers below your highest are missing, **Up to date** when neither is true, **No total known** when nothing states what is released in your language, and **No numbers** or **Numbering restarts** when there is nothing to compare.

From 1.30.0 the volume line counts a volume you hold as chapters as held ("You have volumes 1-19 of 15 (English)" can mean volumes 1-14 as files and 15-19 as chapter files), and the chapter line counts the chapters inside your volume files. An official volume's chapters count as released in your language.

The report only knows what the names and the record say. A wrong link gives a wrong answer: if a series looks far behind or ahead, check that the folder is linked to the right series (**Identify…** on its series page).
