# Users and access

## First-run setup

A new server has no accounts and **no default password**. The first time you open it, the **Welcome to MangaPixer** screen asks you to create the administrator account. See [Install with Docker](install-docker.md#step-5-create-the-admin-account).

Once that account exists, the setup screen is gone for good. Every later account is created by an admin.

Account rules:

- **Usernames** can contain letters, digits and `- . _ @ +`. No spaces. Case is ignored, so `Alice` and `alice` are the same account.
- **Passwords** need at least 8 characters, including at least one lowercase letter.

## Admins and readers

There are two roles, shown as **Admin** and **Reader** in the user list.

| | Reader | Admin |
|---|---|---|
| Read libraries | Only the libraries an admin has granted | Every library |
| Own settings, password, reading progress | Yes | Yes |
| **MangaPixer Administration** (libraries and their icons, scans, users, backups, analytics, audit trail, update checker, log level, YACReader import) | No | Yes |
| Set reading direction for libraries and folders | No | Yes |

Admins open the admin page from the account menu (**MangaPixer Administration**). The server always keeps at least one active admin: it refuses to disable or demote the last one.

## Creating users

In **MangaPixer Administration** > **Users** > **Create New User**:

1. Enter a **Username**.
2. Choose how the person gets their password:
   - **Leave Password (optional) blank** to get an activation link. After **Create User**, a box shows the link. Select **Copy link** and send it to the person. The link is single-use and **expires after 48 hours**. On the **Activate your account** page they choose a password and select **Set password & activate**, which signs them in.
   - **Enter a password** to create the account with a temporary password. At first sign-in they must set a new one on the **Set a new password** screen before they can do anything else.
3. Tick **Admin role** only for people who should administer the server.

If a link expires or gets lost before it is used, select **Reissue activation link** on that user's row to get a fresh one (only for accounts that have not been activated yet). Until the account is activated, sign-in attempts get the normal "Invalid username or password." message.

The link uses the address you used to open the admin page. If you run behind a reverse proxy, check that the link points at your public address; see [Reverse proxy and HTTPS](reverse-proxy-and-https.md#activation-links).

## Library access

**New readers see no libraries until you grant them.** In the **Users** list, select **Library access** (the books icon) on a reader's row and tick the libraries they may read. Changes take effect immediately. Admins always see every library, so the panel only explains that for them.

## Managing accounts

| Task | How |
|---|---|
| Reset a forgotten password | **Reset password** (the key icon) on the user's row. A temporary password appears in a message for 10 seconds; copy it and pass it on. The user must choose a new password at next sign-in, and all their sessions are signed out. |
| Change your own password | **Settings** > **Account Settings** > **Change Password**. This signs out every session, including the one you are using, so sign in again afterwards. |
| Disable or re-enable an account | API only: `POST /api/v1/admin/users/<id>/update` with `{"isActive": false}` or `true`. Disabling signs the user out everywhere. |
| Make someone an admin, or remove admin | API only: the same endpoint with `{"isAdmin": true}` or `false`. The user is signed out of every session, so the new role applies as soon as they sign back in. |
| Sign a user out everywhere | API only: `DELETE /api/v1/admin/users/<id>/sessions`. |
| Delete an account | **Delete user** (the bin icon) on the user's row, then confirm. This removes the account together with its reading progress, read marks, bookmarks, favorites, settings, library access and sessions. Your files are not touched. The last active admin cannot be deleted. |

User IDs come from `GET /api/v1/admin/users`. For how to call the admin API from a script, see [Backup and restore](backup-and-restore.md#calling-the-admin-api-from-a-script).

## Analytics

**MangaPixer Administration** shows an **Analytics** section: instance-wide totals (libraries, content, analysis backlog, users, engagement) and a per-user table (role, active/pending status, last login, chapters completed/in progress, bookmarks, favorites, last reading activity). It is admin-only, computed on demand (no data is pre-aggregated or stored beyond what already exists), and refreshes when you open it or select **Refresh**.

Admins see counts and timestamps only, never chapter or item **titles**, **file or folder names**, or **paths**. The same rule applies to every other admin page (backups, audit trail, diagnostics).

Reading in a library a user marked **Private** (see below) is left out of that user's row. The totals at the top still include it, so the rows do not always add up to the totals.

## Private libraries and Incognito

These two features work together to keep some libraries out of sight. For example, you can keep a library off the home screen when you show someone your tablet.

- **Private** is a personal flag. In **Settings** > **Private Libraries**, tick the libraries you want to mark. It only affects your own account. Other users are not affected, and it does not restrict access.
- **Incognito** is a switch in the account menu (**Incognito: On** / **Incognito: Off**). While it is on, your Private libraries are hidden from the library list and sidebar, Home (**Continue reading** and new chapters), search, and browsing.

Things to know:

- **Incognito starts On** every time you open the app in a new tab or window. Reloading a tab keeps your current choice. Switching it reloads the page.
- A direct link to an archive in a Private library still opens.
- **Incognito does not stop tracking.** Reading progress and read marks are still saved, including for Private libraries. They are just not shown on Home while Incognito is on.
- If you mark nothing Private, Incognito changes nothing.
- The **Private Libraries** list in Settings always shows every library you can access, so you can unmark one while Incognito is on.

## Your settings

Settings saved to **your account** follow you to every device:

- **Settings** page:
  - **Always open read archives from the start** (**Reading** card)
  - **Private Libraries**
  - **New Chapters**: how many **Days** count as new (default 30, 1–365) and which libraries contribute to the home row (**Show new chapters from**)
  - **Items per load** (**Performance** card): 25, 50 (default), 100 or 200
  - **Favorites**: **Show a "Favorites" row on the home page** and **Highlight favorited results in search**, both off by default
- **Library view:** view mode, sort, sort order, card size and list columns.
- **Favorites** themselves (see [Library layout](library-layout.md#favorites)).

Saved **per archive, for everyone** who can read it: the double-page pairing (see [Fixing double-page pairing](reader.md#fixing-double-page-pairing)).

Settings saved **in the current browser only**:

- Reader layout (Auto / Single / Double / Double shifted), and the cover-alone default for archives nobody has adjusted
- Page transition
- Page quality, downscale filter and rendering (see [Image quality](reader.md#image-quality))
- Vertical-mode page width and **Tap to scroll** step
- Whether the reader help has been shown
- Whether the library sidebar is collapsed
- Incognito (per tab)

See [Reader](reader.md) for what each reader setting does.

## Sessions and security

- **Sign-in lasts 7 days from your last activity.** While you keep using MangaPixer it stays signed in; after 7 days without using it you are asked to sign in again. Closing the browser does not sign you out. **Logout** in the account menu does, and ends that session on the server too.
- Password changes, password resets and disabling an account end all of that user's sessions.
- **Cookies:** the sign-in cookie (`.MangaPixer.Auth`) and the request-protection cookie (`.MangaPixer.Csrf`) are `HttpOnly` and `SameSite=Strict`. Scripts on a page cannot read them, and other sites cannot use them.
- **Request protection:** every change (anything except a plain read) must carry a token from `GET /api/v1/auth/csrf` in the `X-MangaPixer-Csrf` header. The web app handles this for you. Scripts must do it themselves.
- **Brute-force protection:** after 5 failed sign-ins for one username, or 10 from one IP address, within 5 minutes, further attempts are refused for a while ("Too many login attempts. Please try again later."). Separately, 5 wrong passwords lock that account for 15 minutes. See [Configuration](configuration.md#sign-in-protection).
- **Sign-in keys** are stored in the `keys` folder of the data root. They are what keeps you signed in across restarts. If they are lost, everyone simply signs in again. On Linux they are stored unencrypted with owner-only permissions, so protect your data folder.
- The server speaks plain HTTP. On anything other than a trusted home network, put it behind [HTTPS](reverse-proxy-and-https.md).
