# Missing volumes and chapters

Which series are you behind on, and which volumes are missing from your shelf? The **Missing** report answers that for every folder linked to a series. It compares the volume and chapter numbers in your archive names with the totals the linked series record states.

The report is built from what MangaPixer has already stored: your archive names and the series records it fetched when the folders were linked or last refreshed. **Opening it never contacts a website.** Totals update when records refresh.

## Where to find it

- **MangaPixer Administration** > **Metadata Manager** > **Missing**. Admins only.
- On a series page, admins also see a short line under the buttons, for example "You have volumes 1-7 of 10 (English) · 3 behind", with a link to the report.

The report shows **Behind or with gaps** by default. Switch to **All linked series** to see everything, and use **Library** to narrow it to one library. The line above the list counts every linked series: how many are behind, how many have gaps, how many are up to date, how many have no known total and how many can't be compared. Each series name opens its series page, and the folder button browses the folder.

## What counts

- **Linked folders only.** A folder counts if it is linked to a series, either by an admin (**Identify…** or an accepted review) or by automatic matching. A folder that only inherits a link from a parent folder, a folder marked **Don't match**, a folder still waiting in **Review** and a linked single archive are not listed.
- **The archives in the folder**, plus those in its `Volumes` / `Chapters` subfolders (names like `Volumes`, `Vol 1-10`, `Chapters`, `Ch 1-50`). Other subfolders (`Extras`, `Season 2`, ...) are not read. A subfolder linked to a series of its own belongs to that series.
- **The numbers in the archive names**, read the same way automatic matching reads them: `Title v03` and `Title Vol. 3` are volume 3, `Title - Chapter 012` and `Title c012` are chapter 12, and `Title Vol. 01-05` covers volumes 1 to 5. Extras such as `v02.5` never raise the count. Archives whose names state no number are left out.
- **Volumes and chapters are counted separately** and never converted into each other. A folder with both volume and chapter archives gives no answer; it is shown as **Mixed folder**. A `Volumes` subfolder next to a `Chapters` subfolder is fine: each is compared with its own total. Chapters kept next to volumes usually carry on from the last volume, so only numbers from the lowest chapter you have upward are checked for gaps.

## Which total it compares with

MangaPixer uses the first total the linked record states, in this order:

| Total | Confidence | Shown as |
|---|---|---|
| The English publisher's count ("10 Volumes / 60 Chapters; Ongoing") | high | (English) |
| The count in the country of origin ("14 Volumes (Complete)", "195 Chapters") | medium: the English edition may differ | (original run) |
| The latest released chapter, for chapters only | low: scanlation releases, not an official total | (latest release) |

The (i) next to each line says which total was used. If a total is lower than the highest number you have (your copies follow another edition, or the English release is still catching up), MangaPixer moves on to the next total that covers what you have. If no total does, the series counts as up to date against the first one.

Records linked before MangaPixer 1.28.0 don't have the English total stored yet. Until the record is refreshed, the report uses the total in the country of origin and says so under the series ("An English edition is listed; its total is read on the record's next refresh").

## Reading a line

- **You have volumes 1-7 of 10 (English) · 3 behind**: you have volumes 1 to 7 with none missing, and the English edition has 10.
- **You have 2 volumes (up to 16) of 18 (English) · 2 behind · missing 2-15**: your highest volume is 16, but only 2 volume numbers are on disk.
- **You have chapters 1-6 of 10 (latest release) · 4 behind · missing 3, 5**: gaps are listed number by number (the first 50, then "+N more").

A series is **Behind** when the total is higher than your highest number, **Gaps** when it isn't behind but numbers below your highest are missing, **Up to date** when neither is true, **No total known** when the record states no total, and **Mixed folder** or **No numbers** when there is nothing to compare.

The report only knows what the names and the record say. A wrong link gives a wrong answer: if a series looks far behind or ahead, check that the folder is linked to the right series (**Identify…** on its series page).
