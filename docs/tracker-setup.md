# Tracker setup

*New in 1.43.0.* For admins.

[Tracker sync](tracker-sync.md) lets each user of your server send their reading progress to their own **MyAnimeList** or **MangaUpdates** list. It is off by default. You allow each tracker once for the server; after that, every user decides for themselves whether to connect an account.

Tracker sync is separate from the series information MangaPixer fetches ("Fetch from the web" in Metadata Manager): it has its own switches and texts, and works whether or not that is on.

## Allow a tracker

1. Open **Administration** and find the **Trackers** card.
2. Under the tracker, read the text: it says what is sent, for which users, and to which site.
3. Tick **I have read this - allow MyAnimeList on this server** (or MangaUpdates).
4. Turn on **Allow users to sync to MyAnimeList** (or MangaUpdates).

The text folds away after you accept it; **What is sent?** shows it again. If a later MangaPixer version changes what is sent, the tracker pauses until you accept the new text.

Turning a tracker **off** stops all syncing to it at once. Users keep their connection, and syncing resumes when you turn it back on.

**MyAnimeList also needs this server's own app registration** (next section) - users cannot connect until it is saved. **MangaUpdates needs no registration.**

## MyAnimeList: register this server

MyAnimeList lets an app connect to a user's account only when the app is registered with MyAnimeList. MangaPixer does not ship a shared registration: each server registers its own, once. On a personal server you are both the admin and the user. A second MangaPixer server - for example a test instance next to your main one - needs its own app too: MyAnimeList sends users back only to the exact redirect URL registered for an app, and each server has its own address.

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
    - **App Description** - for example *Syncs my reading progress from my own MangaPixer server*;
    - **App Redirect URL** - paste the redirect URL from step 1, **exactly**;
    - **Homepage URL** - for example `https://github.com/dixit92/mangapixer`;
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

If users see **An admin has not finished setting up MyAnimeList on this server yet**, the Client ID or the redirect URL is missing. If MyAnimeList shows an error about the redirect when a user connects, the URL registered at MyAnimeList and the one saved in MangaPixer differ.

## MangaUpdates

Nothing to register. Each user connects with their own MangaUpdates username and password, which MangaPixer sends to MangaUpdates once to get a sign-in token and never stores.

## Series without a tracker number

MangaPixer syncs only series whose number on the tracker it knows; it never searches a tracker by title:

- **MangaUpdates:** the MangaUpdates series a folder is linked to.
- **MyAnimeList:** the MyAnimeList number the series' MangaDex record names (it fills in as MangaDex records are refreshed).

When a number is missing or wrong, open the series' **Admin** menu (on the series page or the series information panel) and choose **Set MyAnimeList id** or **Set MangaUpdates id**. Paste the number or the series' address on the tracker's site (for example `https://myanimelist.net/manga/2/...` or `https://www.mangaupdates.com/series/...`); MangaPixer reads the number from the address without contacting the site. Your number applies to every user's sync of that series. Leave the field empty and save to use the series' own number again.

## Connected accounts

The **Connected accounts** list at the end of the **Trackers** card shows each user who connected an account: the user, the tracker and its state (**Syncing**, **Needs to connect again**, **Disconnected by an admin**). You never see a user's account on the tracker or their sign-in.

**Disconnect** deletes that user's sign-in and stops their syncing - for example when someone leaves the server. The user sees that an admin disconnected the account and can connect again. Deleting a user deletes their tracker connections too.

## Turning it off in the configuration

`Trackers:NetworkDisabled=true` (environment variable `Trackers__NetworkDisabled=true`) disables tracker sync for the whole server, whatever the switches say: no tracker is contacted and users cannot connect. See [Configuration](configuration.md).

## See also

- [Tracker sync](tracker-sync.md) - for users: connecting, what is sent, Private libraries, the list.
- [Privacy and security](privacy-and-security.md) - everything that may leave your server.
