# Tracker setup

*New in 1.43.0.* For admins.

[Tracker sync](tracker-sync.md) lets each user of your server send their reading progress to their own **MyAnimeList** or **MangaUpdates** list. It is off by default. You allow each tracker once for the server; after that, every user decides for themselves whether to connect an account - on their **Tracking** page (account menu > **Tracking**).

Tracker sync is separate from the series information MangaPixer fetches ("Fetch from the web" in Metadata Manager): it has its own switches and texts, and works whether or not that is on.

## Allow a tracker

1. Open **Administration** and find the **Trackers** card.
2. Under the tracker, read the text: it says what is sent, for which users, and to which site.
3. Tick **I have read this - allow MyAnimeList on this server** (or MangaUpdates).
4. Turn on **Allow users to sync to MyAnimeList** (or MangaUpdates).

The text folds away after you accept it; **What is sent?** shows it again. If a later MangaPixer version changes what is sent, the tracker pauses until you accept the new text.

Turning a tracker **off** stops all syncing to it at once. Users keep their connection, and syncing resumes when you turn it back on.

**MyAnimeList also needs this server's own app registration** (next section) - users cannot connect until it is saved. **MangaUpdates needs no registration.**

### Daily limit per user

**Requests per user per day** (under each tracker; 100 to 20000, **2000** by default) caps how many requests MangaPixer makes to that tracker for one user in a day - reads, writes and sign-in renewals. A user who reads many short works in a day needs more than one would think; when the limit is reached, that user's syncing waits for the next day (UTC). Users see their own count on their Tracking page.

## MyAnimeList: register this server

MyAnimeList lets an app connect to a user's account only when the app is registered with MyAnimeList. MangaPixer does not ship a shared registration: each server registers its own, once. On a personal server you are both the admin and the user. A second MangaPixer server - for example a test instance next to your main one - has its own address, so its redirect URL must be registered too: add it to the same app (MyAnimeList takes several redirect URLs, one per line) or register a separate app for it.

**For personal servers only.** MyAnimeList's API terms are for personal, non-commercial use. Allow MyAnimeList only on a personal, self-hosted server or a personal desktop install - not on a server you run for the public or for a business.

### 1. Find this server's redirect URL

In **Administration** > **Trackers** > **MyAnimeList**, the **Redirect URL** field already shows the address MyAnimeList has to send users back to, built from the address your browser uses right now:

```text
https://<the address of your MangaPixer>/trackers/callback/mal
```

It must be the address **your users open MangaPixer at**, exactly - the same `http` or `https`, host name and port. If your server is reached under several names (for example a LAN address at home and a domain name through a reverse proxy), use the one your users' browsers open, and open Administration under that name so the field shows it. Press **Copy redirect URL**.

### 2. Create the app on MyAnimeList

1. Open [myanimelist.net/apiconfig/create](https://myanimelist.net/apiconfig/create) and sign in with your MyAnimeList account. (You can also find it in your MyAnimeList account settings, under **API**.)
2. Fill in the form. The fields may differ slightly; at the time of writing they are:
    - **App Name** - for example *MangaPixer (my server)*;
    - **App Type** - **web** (MyAnimeList then also gives a client secret) or **other**. Both work with MangaPixer;
    - **App Description** - at least 50 characters, without special characters, for example *Syncs my reading progress from my own MangaPixer server*;
    - **App Redirect URL** - paste the redirect URL from step 1, **exactly** (several are allowed, one per line);
    - **Homepage URL** - a full address, for example `https://github.com/dixit92/mangapixer`;
    - **Commercial / Non-Commercial** - choose **non-commercial**;
    - **Name / Company Name** - your name;
    - **Purpose of Use** - for example *hobbyist*.
3. Agree to the API License and Developer Agreement and submit.
4. Open the app again (**Edit**): it shows the **Client ID** and, for some app types, a **Client Secret**.

Keep the Client ID and the secret to yourself: MyAnimeList's terms do not allow sharing them.

### 3. Enter it in MangaPixer

Back in **Administration** > **Trackers** > **MyAnimeList**:

1. Paste the **Client ID**.
2. Paste the **Client secret** if MyAnimeList gave you one (optional).
3. Check that the **Redirect URL** is exactly the one you registered.
4. Press **Save**.

The card then says **Allowed**. The client secret is **write-only**: MangaPixer stores it encrypted and never shows it again ("A client secret is saved"). Type a new one to replace it, or press **Remove it**.

If users see **An admin has not finished setting up MyAnimeList on this server yet**, the Client ID or the redirect URL is missing. If MyAnimeList shows an error about the redirect when a user connects, the URL registered at MyAnimeList and the one saved in MangaPixer differ. If the browser shows **its own sign-in dialog** for myanimelist.net ("Sign in - https://myanimelist.net") that keeps asking, or MyAnimeList's approval page names **another app** than this server's, the Client ID saved here belongs to a different MyAnimeList app - easy to mix up when you have several. Do not press **Allow** on a page that names another app. Open this server's app on MyAnimeList (the one whose App Redirect URL is this server's redirect URL), copy its Client ID (and secret), paste them here and save.

## MangaUpdates

Nothing to register. Each user connects with their own MangaUpdates username and password, which MangaPixer sends to MangaUpdates once to get a sign-in token and never stores.

## Series without a tracker number

MangaPixer syncs only series whose number on the tracker it knows; it never searches a tracker by title:

- **MangaUpdates:** the MangaUpdates series a folder is linked to.
- **MyAnimeList:** the MyAnimeList number the series' MangaDex record names. It fills in as MangaDex records are refreshed. *1.43.0:* on a server that already refreshes MangaDex records in the background, MangaPixer refreshes once, early, every stored MangaDex record that names no MyAnimeList number yet - the same request by record number, within the daily request budget of series information (what does not fit waits for the next day).

When a number is missing or wrong, open the series' **Admin** menu (on the series page or the series information panel) and choose **Set MyAnimeList id** or **Set MangaUpdates id**. Paste the number or the series' address on the tracker's site (for example `https://myanimelist.net/manga/2/...` or `https://www.mangaupdates.com/series/...`); MangaPixer reads the number from the address without contacting the site. Your number applies to every user's sync of that series. Leave the field empty and save to use the series' own number again.

## Connected accounts

The **Connected accounts** list at the end of the **Trackers** card shows each user who connected an account: the user, the tracker and its state (**Syncing**, **Needs to connect again**, **Disconnected by an admin**). You never see a user's account on the tracker or their sign-in.

**Disconnect** deletes that user's sign-in and stops their syncing - for example when someone leaves the server. The user sees that an admin disconnected the account and can connect again. Deleting a user deletes their tracker connections too.

## Turning it off in the configuration

`Trackers:NetworkDisabled=true` (environment variable `Trackers__NetworkDisabled=true`) disables tracker sync for the whole server, whatever the switches say: no tracker is contacted and users cannot connect. See [Configuration](configuration.md).

## See also

- [Tracker sync](tracker-sync.md) - for users: connecting, what is sent, Private libraries, the list.
- [Privacy and security](privacy-and-security.md) - everything that may leave your server.
