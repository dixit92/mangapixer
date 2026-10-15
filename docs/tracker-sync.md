# Tracker sync

*New in 1.43.0.*

**Tracker sync** sends your reading progress to your own list on **MyAnimeList** or **MangaUpdates** while you read in MangaPixer. When you finish a chapter or a volume of a series, MangaPixer updates how many chapters and volumes you have read there - with your own account, and only for you.

It is off until two things happen:

1. an admin **allows** the tracker for the server (see [Tracker setup](tracker-setup.md));
2. you **connect** your own account on the **Tracking** page (account menu > **Tracking**).

Other users of the server are not affected by your connection, and nothing is sent for a user who has not connected an account.

## The Tracking page

*New in 1.43.0.* Everything about your tracker lists is on one page: open the account menu (top right) and choose **Tracking**. The entry shows a count when series need a look (waiting for you, or a tracker is ahead). **Settings** links there too.

The page has a card for each tracker your admin allows - **MyAnimeList** and **MangaUpdates** - and, once you connected an account, the list of your series on that tracker (one tab per tracker; see [Your series](#your-series)).

## Connect your account

On the **Tracking** page, press **Connect MyAnimeList…** or **Connect MangaUpdates…** on the tracker's card.

### MyAnimeList

1. Read the text that opens: it says what is sent and what MangaPixer will not do.
2. Tick **I understand - connect my MyAnimeList account** and press **Connect MyAnimeList**.
3. Your browser opens **myanimelist.net**. Sign in there (if you are not already) and allow access.
4. MyAnimeList sends you back to MangaPixer, which finishes the connection and says **Your MyAnimeList account is connected**. Press **Back to Tracking**.

You type your MyAnimeList password only on myanimelist.net - MangaPixer never sees it. MangaPixer keeps the sign-in token MyAnimeList gives it, encrypted on the server.

If the page says **This sign-in is not valid any more**, the sign-in was used already, took longer than 10 minutes, or was started in another browser or another MangaPixer sign-in. Start again from the **Tracking** page.

### MangaUpdates

MangaUpdates has no other way to connect an app, so MangaPixer asks for your **MangaUpdates username and password once**:

1. Read the text that opens.
2. Tick **I understand - connect my MangaUpdates account**.
3. Enter your MangaUpdates username and password and press **Connect MangaUpdates**.

MangaPixer passes them to MangaUpdates to get a sign-in token and **never stores or logs them**. The password field is emptied as soon as you press the button. Only the token is kept, encrypted on the server.

## What is sent - and what is never sent

For a series you read, MangaPixer first **reads** your entry for that series on the tracker, to compare, and then - only when MangaPixer is ahead - **writes**:

| Sent | MyAnimeList | MangaUpdates |
|---|---|---|
| Which series | its MyAnimeList number | its MangaUpdates number |
| Your progress | chapters and volumes read | chapters and volumes read |
| A status | Reading, or Completed (see below) | the Reading list, or Complete |
| Dates | the day you started and the day you finished the series (see [Dates](#dates-on-myanimelist)) | no |
| Your sign-in | the token MyAnimeList gave MangaPixer | the token MangaUpdates gave MangaPixer |

**Never sent:** titles, file or folder names, paths, which files you read, page positions, scores, your other activity on this server, anything about other users, or anything that identifies the server. Every request names MangaPixer and its version (the same for every MangaPixer server).

The requests come from the **server**, not from your browser: your browser only talks to MangaPixer (and to myanimelist.net once, when you sign in there).

## When MangaPixer syncs

- **While you read.** Finishing an archive in the reader, or marking one archive read, updates the tracker right away.
- **Folders marked read at once, and imported reading, wait for you.** A jump like that shows in your list as **Waiting for you**: press **Set progress from MangaPixer to *tracker*** for one series, or **Send all**.
- **Nothing happens while nobody reads**, except what you start yourself: **Sync now**, the two **Set progress** buttons, **Send all**, or **Check my list**.
- **When a series' volume list changes** (MangaPixer refreshed it), the series is checked again, so volumes it now knows count.
- **Marking something unread never lowers the tracker.**

MangaPixer counts the highest whole chapter you have read (extras and half chapters do not count) and the volumes you have read - as volume files, or as chapter files when you read every chapter of that volume. Volumes are counted only for series whose volume list MangaPixer knows exactly.

## What MangaPixer will not do

- **Lower a number** on the tracker without asking you. Only **Set MyAnimeList to a number… (can lower it)** / **Set MangaUpdates to a number…** in your list can lower it, after a warning.
- **Unmark anything here.** **Set progress from *tracker* to MangaPixer** only ever marks chapters and volumes read.
- **Change a series you put on hold, dropped, completed or are rereading** there. It shows as **Left as is**.
- **Mark an ongoing series Completed.** A series is set Completed only when it is finished and you reached the tracker's own total.
- **Sync your Private libraries or doujinshi** - unless you turn sync on for that folder or series (next section).
- **Guess a series by its title.** Only series the tracker knows by number are synced (see [Which series sync](#which-series-sync)).

**Add series I start reading to my list** (on by default): a series you start that is not on your list yet is added as Reading. Turn it off to sync only series already on your list.

## Private libraries, doujinshi and "Sync to trackers"

By default, MangaPixer does **not** sync:

- archives in libraries you marked **Private** (in **Settings** > **Private Libraries**);
- **doujinshi**.

You can change that for a folder or for chosen series:

1. Open the library and press **Select**.
2. Select the folders or series.
3. Press **Sync** (the sync icon) and choose:
    - **On** - sync them, also inside a Private library and for doujinshi;
    - **Off** - never sync them, wherever they are;
    - **As default** - remove your setting again.

A folder's setting **covers everything inside it**. When a series and a folder above it both have a setting, the nearest one wins. The **Sync** button shows only when you have a connected account. These settings are yours: other users are not affected.

## Which series sync

A series syncs when the tracker's own number for it is known:

- **MangaUpdates:** the MangaUpdates series a folder is linked to.
- **MyAnimeList:** the MyAnimeList number that the series' **MangaDex** record names. It fills in as MangaPixer refreshes MangaDex records.
- An admin can set the number for a series (series **Admin** menu > **Set MyAnimeList id** / **Set MangaUpdates id**).

A series without a number shows as **No MyAnimeList id** (an admin can set it). MangaPixer never searches a tracker by title.

## Your series

Below the cards, the **Tracking** page lists your series on each tracker you connected - the series MangaPixer has synced, looked at, or found on your list when you pressed **Check my list**. It is built for long lists: 50 series at a time (**Load more** shows the next 50), a search by title (**Find a series**), and chips that show only the series in one state, each with its count:

| Chip | Meaning |
|---|---|
| All | Every series in the list. |
| MangaPixer ahead | You read more here than the tracker has, and it was not sent yet (for example the tracker could not be reached, or today's limit was reached). |
| *Tracker* ahead | The tracker already has more than you read here (for example you read elsewhere). Nothing is lowered. |
| Left as is | The series is on hold, dropped, completed or being reread on the tracker - MangaPixer does not change it. |
| Waiting for you | A folder marked read at once, or imported reading, waits for your click. |
| No *tracker* id | See [Which series sync](#which-series-sync). |
| Not synced | You pressed **Don't sync this series**, or the series is not on your list and **Add series I start reading** is off. |
| In sync, Not checked yet, Sync problem | Shown when they hold a series: both sides agree; MangaPixer has not read the tracker for it yet; the last attempt failed (the reason is shown). |

Each row shows the series' cover and title (open it to go to the series), its state, **MangaPixer**'s chapters and volumes read (of how many, when known), the **tracker**'s numbers and status as MangaPixer last read them, and for MyAnimeList the start and finish dates.

### Bring the two sides in line

Each row has two buttons; the one that cannot move anything is greyed out, and a short line under the buttons says why (for example *MyAnimeList is not ahead of MangaPixer - nothing to bring in*; with a mouse, its tooltip says it too):

- **Set progress from *tracker* to MangaPixer** - marks chapters and volumes **read here** up to the tracker's numbers, for example after you read elsewhere. MangaPixer first asks (*Mark chapters 1-51 read in MangaPixer?*), then says how many files it marked. It only marks - nothing is unmarked - and these marks are not sent back to the tracker.
- **Set progress from MangaPixer to *tracker*** - sends MangaPixer's numbers, including a jump waiting for you. It never lowers the tracker.

The **⋯** menu of a row has more:

- **Sync now** - read the tracker and send what syncs on its own (a waiting jump stays waiting).
- **Set dates on MyAnimeList…** - see [Dates](#dates-on-myanimelist).
- **Set *tracker* to a number… (can lower it)** - send the numbers you type. This **replaces** what the tracker has, also when the tracker has more; it warns you first. Not offered for a series left as is.
- **Don't sync this series** / **Sync this series again** - leave this series out of sync on this tracker.
- **Open the series**.

A greyed-out **Sync now** or **Set *tracker* to a number…** says why under its name.

**Check my list on *tracker*** (the first time you connect, or any time): MangaPixer reads your tracker entries for the series here, **50 at a time** - press again for the next 50 - and shows where each side is ahead. It changes nothing on the tracker. **Send all** sends every series waiting for you, after a confirmation.

### From a series

When you open a series MangaPixer tracks for you, a gold **Tracking** button sits next to **Series info**. It shows, per tracker, the series' state, MangaPixer's numbers next to the tracker's, the dates, and **Open in Tracking**, which opens the Tracking page at that series.

In the library, a thin **gold border around a cover** means **every tracker this series syncs to** counts that chapter or volume as read (by the trackers' numbers as MangaPixer last read them). With one tracker that is simply that tracker; with MyAnimeList and MangaUpdates both connected, a chapter one of them has not reached yet has no border - open the series' **Tracking** button or the Tracking page to see which one is behind. A tracker the series does not sync to (not on your list there, or **Don't sync this series**) does not count. MangaPixer's own **Read** badge is separate: the two can differ, for example after you read on another device - **Set progress from *tracker* to MangaPixer** brings MangaPixer in line.

MangaPixer is the hub: each tracker follows MangaPixer's own progress on its own, never another tracker, and nothing comes back from a tracker unless you press **Set progress from *tracker* to MangaPixer**.

### Dates on MyAnimeList

MyAnimeList keeps a start and a finish date for each series on your list. MangaPixer fills in only the dates MyAnimeList does not have yet, with its updates: the **start** is the day you first read the series here (never later than a finish date MyAnimeList already has), the **finish** the day the series was completed. The **Dates on MyAnimeList** column shows the dates MyAnimeList has; a date marked **(auto)** is one MyAnimeList does not have yet, which MangaPixer would send. **Set dates…** (under the dates, or in the **⋯** menu) lets you enter your own dates instead, or put either back to **Automatic**. Your dates go to MyAnimeList with the next update of that series. MangaUpdates keeps no dates.

### Daily limit

MangaPixer makes at most a set number of requests per day to each tracker for your account - reads, writes and sign-in renewals; **2000** unless your admin changed it. Your card shows how many were used today. When the limit is reached, syncing resumes the next day (UTC).

## Disconnect

Press **Disconnect** on the tracker's card on the **Tracking** page. MangaPixer deletes its sign-in token and your series list for that tracker from the server. Nothing on the tracker is changed - your list there stays as it is.

- **MangaUpdates:** disconnecting also signs MangaPixer out on MangaUpdates.
- **MyAnimeList:** disconnecting deletes MangaPixer's sign-in from the server, so it can no longer be used. MyAnimeList has no page where you can remove an app's access, and offers apps no way to end their own sign-in; the old sign-in simply expires there.

An admin can see **that** you connected an account (and its state) and can disconnect it - for example when you leave the server - but cannot see or use your sign-in. If an admin disconnected your account, the card says so; connect again to resume.

If a tracker stops accepting MangaPixer's sign-in (for example after you changed your password there), the card says **Connect again**. If what MangaPixer sends changes in a later version, the card asks you to read the new text and connect again; syncing pauses until you do.

## See also

- [Tracker setup](tracker-setup.md) - for admins: allowing a tracker and registering MangaPixer with MyAnimeList.
- [Privacy and security](privacy-and-security.md) - everything that may leave your server.
