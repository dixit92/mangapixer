# Recommendations

*New in 1.53.0.* MangaPixer suggests what to read next from the series already on your server: a **Recommended for you** row on Home, and a **More like this** row on every series page. Both are worked out on your MangaPixer server from what you rated, starred and finished, and from the tags, genres, authors and magazines of the series on it. Nothing leaves the server.

## Recommended for you (Home)

The row sits on Home after **Continue reading**. It shows up to twelve series you have not touched yet, best first, each with one short line saying why it is there.

What it learns from - only your own account:

- **Your ratings.** Four and a half or five stars count most; three and a half or four stars count too. A series you rated two stars or lower counts *against* what it has in common with others, so similar series rank lower or drop out of the row - a series by the same author can disappear even when it shares tags with one you rated highly, because a shared author weighs more than a shared tag or genre. **More like this** is not affected. Two and a half or three stars are neutral.
- **Your favorites.** A starred series counts like a good rating.
- **Series you finished** - every archive of the series read - count a little, unless you rated them (then the rating decides).

A rating, star or read mark on a single volume counts for its series. What it leaves out:

- anything you rated, starred, read or started - the row is for what is new to you;
- libraries you hid from Home (Settings > New Chapters, the same choice as the New chapters row);
- your Private libraries while Incognito is on;
- libraries whose series information an admin turned off, and libraries you cannot open.

Until you have rated something three and a half stars or more, starred a series, or finished one, the row has nothing of yours to learn from. *New in 1.54.0:* it then shows the best rated series on your server you have not started, by their [community score](series-information.md#community-scores) on MangaUpdates (or MangaDex) - "Well rated on MangaUpdates: 9.0". With one or two such series of yours, those picks come after the ones matching your taste; from three on, only your taste counts. Without community scores (no series information from the web) the row stays hidden until you have rated, starred or finished something, and also when nothing on the server has enough in common with what you liked.

## More like this (series page)

On a series page the row sits after **About**. It lists up to twelve series that have the most in common with this one - the same author, shared tags and genres, the same magazine, or a sequel, prequel, side story or spin-off. Series you have already read are included, after the ones you have not. When nothing on the server is similar enough, there is no row.

## Why a pick appears

Each card says why, in one line under its title - the biggest reason it was picked:

- **Same author: ...** - a writer or artist of one of your favorites (or of this series).
- **Like *series*: horror, school life** - the series of yours it shares the most with, and the two strongest things they share. On a series page it is just the shared things, for example **Horror, School Life**.
- **Sequel of ...**, **Prequel of ...**, **Side story of ...**, **Main story of ...**, **Spin-off of ...**, **Related to ...** - the two series' web records name each other (this needs [series information](series-information.md) from the web for both).
- **Well rated on MangaUpdates: 9.0** (*new in 1.54.0*) - a pick from the best rated series, while you have rated, starred or finished fewer than three series.

How the score works: every series is described by its authors, tags, genres, magazine, type and publisher. Authors count most, then tags, then genres and magazine; type and publisher only a little. A tag almost every series on the server has counts for little; a rare tag or an author counts for much. A pick's score is how closely its description matches yours (or this series'), plus a bonus for a related series. Reasons only name series, authors and tags that you can see on this server. *New in 1.54.0:* a series' community score from MangaUpdates (else MangaDex) nudges its score by at most 15 % - up when it is rated above the middle of the series you could be offered, down when below - so between two equally close matches the better rated one comes first; it never lifts a series that has little in common with yours. MyAnimeList's score is only shown, never used here.

The rows need tags: a series without series information or ComicInfo has nothing to match on and is never picked. The more series carry web data or ComicInfo, the better the suggestions.

## Hide the Home row

Settings > **Home** > **Show** next to **Recommended for you** (*1.54.0*; it was under Ratings and recommendations in 1.53.0). It is on by default and applies to your account on every device; the same card moves the row up or down the home page. The series page's **More like this** row has no switch; it is only shown when there is something to show.

## Privacy

- Everything is computed on your MangaPixer server, when you open Home or a series page. No rating, favorite, reading history, title or library data is sent anywhere, and no web site is asked for anything: only series already on this server are recommended.
- Your recommendations come from your own ratings, favorites and reading only. No other user sees them or influences them, and an admin has no view of them.
- The server keeps a computed answer in memory for at most ten minutes, for your account only; any new rating, star or read mark of yours replaces it at once. Nothing new is stored in the database.
- The log records only ids, counts and timings, at debug level - never a title, tag or reason.
