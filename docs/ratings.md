# Ratings

*New in 1.52.0.* Give any series or archive your own rating, from half a star to five stars. Ratings are yours alone: no other user of the server sees them, and nothing about them leaves MangaPixer.

## Rate something

You can rate anything you can open - a folder (normally a series) or a single archive (a volume or a one-shot):

- **Info sheet:** open **Info** - the (i) on a card, or **Info** in a folder's top bar (both show where there is series information or tags). Under the series details, **Your rating** shows five stars.
- **Series page:** the same stars sit under the series details at the top.
- **Reader:** choose **Rate** (the half-star button in the top bar; on a phone, **Rate** in **Reader options**). A small sheet lists **This archive** and **Its folder**, each with its own stars, so you can rate the volume you just finished and the series it belongs to without leaving the reader. This is also the way to rate an archive, or its folder, that has no **Info**.

Tap or click the **left half** of a star for a half star, the **right half** for the whole star: the right half of the fourth star is four stars, its left half three and a half. To change a rating, tap another star. To remove it, choose **Clear rating** next to the stars.

With a keyboard, Tab to the stars and use the arrow keys (half a star at a time), Page Up / Page Down (a whole star), Home (half a star) and End (five stars); Delete or Backspace clears the rating. A screen reader announces the value as, for example, "3.5 of 5 stars".

On a touch screen each star is a 40 x 40 pixel target, so a half star is easy to hit with a thumb.

Rating a series rates that folder only - its volumes keep their own ratings, and rating a volume does not change the series. A rating and a favorite are independent: you can star something without rating it, and rate it without starring it.

## Where ratings show

- **Cards:** a rated folder or archive shows a small star with the number (for example **4.5**) on its cover, just above the favorite star - in the library view, on search results and on the Favorites page. Unrated items show nothing.
- **List view:** the same star and number in the row, next to the read badges.
- On the Favorites page a stack of starred archives shows its **folder's** rating.

## Sort and filter by rating

- **My rating** in the library view's **View** > **Sort by** lists the folders and archives at the current level by your rating, highest first (equal ratings by name); the ones you have not rated follow, by name. There is no jump rail under this sort.
- The Favorites page has **My rating** in **Sort by** too, and a **Rating** choice in **Filter**: **Rated**, **Not rated**, or **Rated or not**.
- In search, type `rating:` followed by a number of stars to find what you rated at least that high: `rating:4` finds four stars and up, `rating:3.5` three and a half and up. `rating:none` keeps only what you have not rated. It combines with the other search words: `berserk rating:4`, `rating:4 -dragon`, `library:manga rating:4.5`, `is:unread rating:4`. On its own, `rating:4` lists everything you rated four stars or more; `rating:none` needs another word to search for. The search page's **?** panel lists the term.

## Private libraries and Incognito

You can rate something in one of your Private libraries, also while Incognito is on, just as reading progress is still saved then. While Incognito is on, rated items in your Private libraries are left out of lists, sorts, the Favorites page and search, like everything else in them.

## Privacy

A rating is stored on your MangaPixer server, for your account only. It is never shown to another user or to an admin; the server's log records only ids and the score, at debug level, never a name or a path. It is not part of the [metadata export](metadata-export.md), and never sent to a tracker: [tracker sync](tracker-sync.md) sends no scores. Deleting your account deletes your ratings. They are in the database, so the server's backups keep them, as they keep your favorites.
