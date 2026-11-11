# Ratings

*New in 1.52.0.* Give any series or archive your own rating, from half a star to five stars. Nothing about your ratings leaves MangaPixer. *New in 1.53.0:* when an administrator turns on [shared ratings](#shared-ratings), other users of the same server see an **average** of everyone's ratings - never who rated what.

## Rate something

You can rate anything you can open - a folder (normally a series) or a single archive (a volume or a one-shot):

- **Info sheet:** open **Info** - the (i) on a card, or **Info** in a folder's top bar (both show where there is series information or tags). Under the series details, **Your rating** shows five stars.
- **Series page:** the same stars sit under the series details at the top.
- **Reader:** choose **Rate** (the circled-star button in the top bar; on a phone, **Rate** in **Reader options**). A small sheet lists **This archive** and **Its folder**, each with its own stars, so you can rate the volume you just finished and the series it belongs to without leaving the reader.
- **Library view, a folder with no Info:** *(1.53.0)* where a folder's top bar has no **Info** button, a **Rate** button sits in its place (on a phone just the circled star). It opens a small sheet with **This folder** and its stars; once you have rated the folder the button shows your rating, for example **4.5**.
- **Library view, any card:** *(1.53.0)* choose **Select**, select one folder or archive and choose **Rate...** (right after **Favorites**; on a phone under **More actions**). The same small sheet opens for that one item.

Every **Rate** action uses the same circled-star icon, so it is never mistaken for the favorite star next to it.

Tap or click the **left half** of a star for a half star, the **right half** for the whole star: the right half of the fourth star is four stars, its left half three and a half. To change a rating, tap another star. To remove it, choose **Clear rating** next to the stars. The caption ("Your rating", "This folder") always sits above the stars, and the stars stay where they are when you rate.

With a keyboard, Tab to the stars and use the arrow keys (half a star at a time), Page Up / Page Down (a whole star), Home (half a star) and End (five stars); Delete or Backspace clears the rating. A screen reader announces the value as, for example, "3.5 of 5 stars".

On a touch screen each star is a 40 x 40 pixel target, so a half star is easy to hit with a thumb.

Rating a series rates that folder only - its volumes keep their own ratings, and rating a volume does not change the series. A rating and a favorite are independent: you can star something without rating it, and rate it without starring it.

## Where ratings show

- **Cards:** a rated folder or archive shows a small star with the number (for example **4.5**) on its cover, just above the favorite star - in the library view, on search results and on the Favorites page. Unrated items show nothing.
- **List view:** the same star and number in the row, next to the read badges.
- On the Favorites page a stack of starred archives shows its **folder's** rating.

## Sort and filter by rating

- **My rating** in the library view's **View** > **Sort by** lists the folders and archives at the current level by your rating, highest first (equal ratings by name); the ones you have not rated follow, by name. There is no jump rail under this sort.
- **Server rating** *(1.53.0, only while shared ratings are on)* in the same menu lists them by the server average, highest first; at an equal average the one more people rated comes first, then by name; what nobody who shares has rated follows, by name. No jump rail either. If an administrator turns shared ratings off, the library goes back to the name order.
- The Favorites page has **My rating** in **Sort by** too, and a **Rating** choice in **Filter**: **Rated**, **Not rated**, or **Rated or not**.
- In search, type `rating:` followed by a number of stars to find what you rated at least that high: `rating:4` finds four stars and up, `rating:3.5` three and a half and up. `rating:none` keeps only what you have not rated. It combines with the other search words: `berserk rating:4`, `rating:4 -dragon`, `library:manga rating:4.5`, `is:unread rating:4`. On its own, `rating:4` lists everything you rated four stars or more; `rating:none` needs another word to search for. The search page's **?** panel lists the term.
- *(1.53.0)* `rating:` works everywhere you search: in the **quick results** under the search box while you type (`saga rating:4` lists only what you rated four stars and up), and in the **tag view** - a search with tag chips such as a genre or a creator - where it keeps the works you rated at least that high (`rating:none`: the works you have not rated). It is always **your own** rating, never the server average.

## Shared ratings

*New in 1.53.0.* An administrator can let the users of a server see each other's ratings as an **average**. It is off until an administrator turns it on.

**For administrators.** In **Administration**, the **Shared ratings** card has **Share ratings between users**. Ticking it first shows what it does - *"Every user's ratings will count in an average and a count on each series and volume - never who rated what. Ratings in a user's Private libraries never count. Each user can turn sharing off for themselves in Settings."* - and turns it on only after **Turn on shared ratings**. Unticking it hides every average at once; no rating is deleted, and turning it on again brings them back. Each change is written to the **Audit trail**. Administrators never see who rated what: this switch is their only control.

**For everyone.** In **Settings**, the **Ratings and recommendations** card has **Share my ratings with other users of this server**. It is **on** for every account - existing ratings count from the moment the administrator turns shared ratings on - and you can turn it off at any time: your ratings then stop counting in every average on the next page you open, and a short message says *"Your ratings are no longer shared."* While the administrator has not turned shared ratings on, the box is greyed out with *"Your administrator has not turned on shared ratings."* The same card holds **Show a "Recommended for you" row on the home page**.

**What others see.** On the **Info sheet** and the **series page**, under your own stars, a line such as **SERVER ★ 4.0 · 3 ratings**: one star, the average in stars (rounded to one decimal) and how many people's ratings it is made of. Your own rating counts in it too while you share. The line is missing when nobody who shares has rated that folder or archive. It is never shown on cards or list rows, and there is no list of who rated. As with your own rating there is no rollup: a series' average is made of ratings of the series folder, not of its volumes.

**What never counts.** A rating on something in a library that the **person who rated it** marked Private never counts in any average, whoever looks and whether or not Incognito is on - marking a library Private takes your ratings there out of the averages at once. Ratings of people who turned sharing off never count either.

**On a small server**, an average can show what someone rated: with two people sharing, the one who knows their own rating can work out the other's, and an average that changes right after someone rates shows how they rated. If that matters to you, turn sharing off in Settings.

## Private libraries and Incognito

You can rate something in one of your Private libraries, also while Incognito is on, just as reading progress is still saved then. While Incognito is on, rated items in your Private libraries are left out of lists, sorts, the Favorites page and search, like everything else in them.

## Privacy

A rating is stored on your MangaPixer server, for your account. On its own it is never shown to another user or to an admin; with [shared ratings](#shared-ratings) on, other users see only the average and the count of everyone's shared ratings. The server's log records only ids and the score, at debug level, never a name or a path (an average's log line has the node id and the count, never the average). It is not part of the [metadata export](metadata-export.md), and never sent to a tracker: [tracker sync](tracker-sync.md) sends no scores. Deleting your account deletes your ratings. They are in the database, so the server's backups keep them, as they keep your favorites.
