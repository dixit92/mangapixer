# Tracker sync

*New in 1.43.0.*

**Tracker sync** sends your reading progress to your own list on **MyAnimeList** or **MangaUpdates** while you read in MangaPixer. When you finish a chapter or a volume of a series, MangaPixer updates how many chapters and volumes you have read there - with your own account, and only for you.

It is off until two things happen:

1. an admin **allows** the tracker for the server (see [Tracker setup](tracker-setup.md));
2. you **connect** your own account in **Settings** > **Connected accounts**.

Other users of the server are not affected by your connection, and nothing is sent for a user who has not connected an account.

## Connect your account

Open **Settings** and find the **Connected accounts** card. It lists the trackers your admin allows.

### MyAnimeList

1. Read the text under **MyAnimeList**: it says what is sent and what MangaPixer will not do.
2. Tick **I understand - connect my MyAnimeList account** and press **Connect MyAnimeList**.
3. Your browser opens **myanimelist.net**. Sign in there (if you are not already) and allow access.
4. MyAnimeList sends you back to MangaPixer, which finishes the connection and says **Your MyAnimeList account is connected**. Press **Back to Settings**.

You type your MyAnimeList password only on myanimelist.net - MangaPixer never sees it. MangaPixer keeps the sign-in token MyAnimeList gives it, encrypted on the server.

If the page says **This sign-in is not valid any more**, the sign-in was used already, took longer than 10 minutes, or was started in another browser or another MangaPixer sign-in. Start again from **Settings**.

### MangaUpdates

MangaUpdates has no other way to connect an app, so MangaPixer asks for your **MangaUpdates username and password once**:

1. Read the text under **MangaUpdates**.
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
| Dates | no | no |
| Your sign-in | the token MyAnimeList gave MangaPixer | the token MangaUpdates gave MangaPixer |

**Never sent:** titles, file or folder names, paths, which files you read, page positions, scores, your other activity on this server, anything about other users, or anything that identifies the server. Every request names MangaPixer and its version (the same for every MangaPixer server).

The requests come from the **server**, not from your browser: your browser only talks to MangaPixer (and to myanimelist.net once, when you sign in there).

## When MangaPixer syncs

- **While you read.** Finishing an archive in the reader, or marking one archive read, updates the tracker right away.
- **Folders marked read at once, and imported reading, wait for you.** A jump like that shows in your list as **Waiting for you to press Send**: press **Send** for one series, or **Send all**.
- **Nothing happens while nobody reads**, except what you start yourself: **Sync now**, **Send**, **Send all**, or **Check my list**.
- **Marking something unread never lowers the tracker.**

MangaPixer counts the highest whole chapter you have read (extras and half chapters do not count) and the volumes you have read as volume files. Volumes are counted only for series whose volume list MangaPixer knows exactly.

## What MangaPixer will not do

- **Lower a number** on the tracker without asking you. Only **Set MyAnimeList to...** / **Set MangaUpdates to...** in your list can lower it, after a warning.
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

A series without a number shows as **No MyAnimeList number for this series - an admin can set it**. MangaPixer never searches a tracker by title.

## Your list

Under each connected account, **Your series on MyAnimeList** (or MangaUpdates) lists the series MangaPixer has synced or looked at, the ones that need a look first. Each row shows your numbers **Here**, a jump **Waiting for Send**, what was **Sent** last, and the tracker's numbers when MangaPixer last read them.

| State | Meaning |
|---|---|
| Up to date | The tracker has what you read here. |
| *Tracker* is ahead - nothing sent | The tracker already has more; nothing was changed. |
| Lower here than what was sent | You have less marked here than MangaPixer sent earlier (for example after marking something unread). The tracker is not lowered. |
| Waiting for you to press Send | A folder marked read at once, or imported reading. |
| No *tracker* number for this series | See [Which series sync](#which-series-sync). |
| Not synced (your choice) | You pressed **Don't sync** for this series. |
| Left as is | The series is on hold, dropped, completed or being reread on the tracker. |
| Not on your list | The series is not on your list and **Add series I start** is off. |
| *Tracker* asked to slow down | It is tried again soon (MangaUpdates allows one list change every few seconds). |
| Last sync failed | The tracker refused or could not be reached; the reason is shown. |
| Not checked yet | MangaPixer has not read this series on the tracker yet. |

Actions on a series:

- **Sync now** - read the tracker and send what syncs on its own (a waiting jump stays waiting).
- **Send** - send a waiting jump. It still never lowers the tracker.
- **Set *tracker* to...** - send the numbers you type. This **replaces** what the tracker has, also when the tracker has more.
- **Don't sync** / **Sync again** - leave this series out of sync on this tracker.

**Check my list** (the first time you connect, or any time): MangaPixer reads your tracker entries for the series here, **50 at a time** - press again for the next 50 - and shows where MangaPixer is ahead. It changes nothing on the tracker. Then **Send all** sends every waiting jump, after a confirmation.

MangaPixer makes at most **300 requests per day** to each tracker for your account (reads, writes and sign-in renewals). When the limit is reached, syncing resumes the next day (UTC).

## Disconnect

Press **Disconnect** under the account in **Settings** > **Connected accounts**. MangaPixer deletes its sign-in token and your series list for that tracker from the server. Nothing on the tracker is changed - your list there stays as it is.

- **MangaUpdates:** disconnecting also signs MangaPixer out on MangaUpdates.
- **MyAnimeList:** MyAnimeList offers apps no way to end their own sign-in; disconnecting deletes MangaPixer's sign-in from the server.

An admin can see **that** you connected an account (and its state) and can disconnect it - for example when you leave the server - but cannot see or use your sign-in. If an admin disconnected your account, the card says so; connect again to resume.

If a tracker stops accepting MangaPixer's sign-in (for example after you changed your password there), the card says **Connect again**. If what MangaPixer sends changes in a later version, the card asks you to read the new text and connect again; syncing pauses until you do.

## See also

- [Tracker setup](tracker-setup.md) - for admins: allowing a tracker and registering MangaPixer with MyAnimeList.
- [Privacy and security](privacy-and-security.md) - everything that may leave your server.
