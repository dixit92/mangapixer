# Trash and clean-up

*New in 1.31.0.*

When a file or folder disappears from a library, MangaPixer does not forget it at once. It moves it to the **trash**: the item no longer shows in the library, but its reading progress, read marks, bookmarks and favorites are kept for a while. If the file comes back, or turns up somewhere else (a series you moved or renamed), it is recognised and everything is still there.

Like Plex's "Empty trash" and "Clean bundles", two admin actions then clean up:

- **Empty trash** removes items that have been in the trash for longer than the retention time, together with everything MangaPixer stored for them.
- **Clean bundles** removes files in MangaPixer's data folder that nothing uses any more (old thumbnails and covers).

Both are in **MangaPixer Administration** > **Trash**. They only touch MangaPixer's own database and data folder; **your comics and manga are never touched** (see [Read-only guarantee](library-layout.md#read-only-guarantee)).

## How long removed items are kept

**Keep removed items for** sets one time for two things:

- how long a moved or renamed series is **recognised** - it keeps its reading state when it reappears within this time;
- how long removed items **stay in the trash** - after it, they can be emptied.

Choose **Daily** (1 day), **Weekly** (7 days), **Monthly** (30 days, the default), **Quarterly** (90 days) or **Yearly** (365 days). The time counts from the scan that found the item missing. A longer time is safer if disks or shares are sometimes offline for a while; a shorter one keeps the database smaller.

## Automatic cleaning

**Turn automatic cleaning on** runs **Empty trash** and then **Clean bundles** once a day at 04:00 (server time). It is **off** by default, also after an upgrade: nothing is removed automatically until an admin turns it on. When you turn it on, MangaPixer first shows what the first run will remove.

If the server was off at 04:00, the run is made up shortly after it starts again (but never for a day before the switch was turned on).

## Empty trash now

**Empty trash now** removes everything that is past the retention time in every library without a **hold** (below). Before it does, it shows what will go: the number of items, files and folders, the reading-state entries of all users that go with them (progress, read marks, bookmarks, reader settings and favorites), and the size of their files. This cannot be undone, except by [restoring a backup](#trash-and-backups).

What is removed for each item:

- the item itself, its pages list and its search entry;
- every user's reading progress, read marks, bookmarks, reader settings and favorites of it;
- the folder's series link, cover choice and folder settings;
- its thumbnail and cover crops in the data folder.

What is kept: the series information fetched from the web (the MangaUpdates record, its poster, MangaDex volume covers). It belongs to the series, not to one folder, and is used again if the series comes back.

A folder is only removed together with everything in it; a folder that still has a present item, or an item removed more recently, stays.

### Holds

A library keeps its trash, and is listed as **Held** with the reason, when emptying it could be a mistake:

| Hold | Why | What to do |
|---|---|---|
| A scan is running | The scan is changing the library right now. | Nothing: empty it after the scan, or the next automatic run does. |
| The folder was not reachable | The last scan could not reach the library's folder (a disk or share was offline). | Check the disk or share and scan the library. |
| More than half would go | More than half of the library is in the trash at once - often an offline disk or a subfolder that was not mounted during a scan. | Check the disk and scan again; the items come back with their reading state. |

**Empty trash now** and the automatic run skip held libraries. If the removal is what you want (you really deleted most of a library), use **Empty this library's trash** on that library: it asks you to confirm and then empties it despite the hold.

Items that a series move is still waiting on are never removed, whatever their age.

## Clean bundles now

**Clean bundles now** removes files in the data folder that no longer belong to anything: thumbnails and cover crops of items that are gone or changed, volume covers and series posters whose record no longer uses them, and temporary files left by an interrupted write. Files written in the last hour are skipped, and files MangaPixer did not write are never touched. The card shows how many files and how much space would be freed.

## Last runs

The card shows when the trash was last emptied and the bundles last cleaned, whether that was the automatic run or an admin, and what was removed. Every run is also recorded in the audit trail.

## Trash and backups

The [automatic database backups](backup-and-restore.md) run every day and keep the last 7 by default, so a backup taken while an item was still in the trash usually exists. Restoring it brings back the removed items with their reading state; their thumbnails are made again if the files are back. If you want a guaranteed copy right before emptying the trash, choose **Back up now** first.
