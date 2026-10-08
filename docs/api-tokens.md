# API tokens

*New in 1.33.0.*

An **API token** lets another program read MangaPixer's series information without your password. The first program that uses one is [MangaList](https://github.com/dixit92/MangaList), which reads which of your folders are linked to which series, their volume lists and what is missing, so it can find gaps and file new downloads.

## What a token can do

When you create a token you tick what it may do (its **scopes**). There are two, and a token has at least one:

- **Read the metadata export** (`metadata:read`, ticked by default): read the pages under `/api/v1/export/`. It sees the series information of **every library**, like an admin does. It does not see reading progress, favorites or other per-user data.
- **Request library scans** (`library:scan`, *new in 1.36.0*, not ticked by default): ask MangaPixer to scan a library, so files the program has just put into a library folder show up without waiting for the next automatic scan. See [Request a library scan](#request-a-library-scan).

Nothing else:

- it **cannot change anything**: apart from asking for a library scan (with that scope), a token is refused for anything but reading requests (`GET`);
- it **cannot open any other page** of MangaPixer - not your libraries, not the reader, not Administration. Sent anywhere else, a token is ignored, exactly as if the program had sent nothing.

A token can do only what was ticked when it was created; the scopes cannot be changed later. To change them, create a new token and revoke the old one. Tokens created before 1.36.0 can read the export and cannot request scans.

A token belongs to the admin who created it, and works only while that account is an active admin. It stops working when you **revoke** it, when it **expires**, or when its admin is **deleted**, **disabled** or **no longer an admin** (it works again if that admin is made an admin again, unless it was revoked or has expired). A token keeps working when its admin changes their own password; after another admin **resets** that password, it pauses until the new password is set.

## Create a token

1. Open **MangaPixer Administration** and find the **API tokens** card (next to **Users**).
2. Under **New token**, give it a **Name** you will recognise later, for example *MangaList*.
3. Under **The token may**, tick what the program needs: **Read the metadata export** (ticked by default) and / or **Request library scans**. Tick only what it needs; at least one is required.
4. Choose when it **expires**: after **30 days**, **90 days**, **1 year** (the default), or **Never**.
5. Press **Create token**.

MangaPixer shows the token **once**. It starts with `mpx_`. Press **Copy** and paste it into the other program's settings, then press **Done**. You will not see it again: MangaPixer keeps only a fingerprint of it (a SHA-256 hash), never the token itself. If you lose it, revoke it and create a new one.

The program sends the token with every request, in the `Authorization` header:

```text
Authorization: Bearer mpx_...
```

To check a connection, a program can ask `GET /api/v1/export/ping` (needs **Read the metadata export**); it answers with `"ok": true` and the server's time.

## Request a library scan

*New in 1.36.0.* A token with **Request library scans** may ask for a **full scan of one library** - the same scan as **Scan now** in Administration:

```text
POST /api/v1/export/libraries/{id}/scan
Authorization: Bearer mpx_...
```

`{id}` is the library's id as `GET /api/v1/export/libraries` lists it (a token may scan any library). The request has no body. MangaPixer answers:

| Answer | Meaning |
|---|---|
| `202 Accepted` with `{ "scanRunId": "..." }` | The scan has started. It runs in the background; new files show up when it is done. |
| `409 Conflict` (`scan_in_progress`) with `Retry-After: 60` | A scan of that library is already running. Ask again after the given seconds: the running scan may already have passed the folder with the new files. |
| `429 Too Many Requests` (`scan_cooldown`) with `Retry-After` | A token already started a scan of that library less than **5 minutes** ago. Ask again after the given seconds. |
| `404 Not Found` (`library_not_found`) | There is no library with that id. |
| `403 Forbidden` | The token does not have **Request library scans**. |
| `401 Unauthorized` | No valid token. A browser sign-in does not work here: this address accepts only tokens. |

What it does and does not do:

- It is always a **full** scan of the library, never a scan of a single folder, and it changes nothing else: no files (your library folders stay read-only), no series links, no settings. It sends nothing to the internet.
- **One scan per library every 5 minutes** for scans requested by tokens (a full scan is cheap, but this stops a program stuck in a loop). Scans you start in Administration and automatic scans do not count. The server's admin can change the 5 minutes with `MangaPixer__Security__ApiTokens__ScanCooldownMinutes` (see [Configuration](configuration.md#sign-in-protection); `0` turns the wait off).
- A started scan counts as the library's last scan, like **Scan now**, so it also pushes the next automatic scan back.
- Every started scan is recorded in the **Audit trail** as `library.scan.request`, with the token's admin and the token's id. Refused requests are not recorded there.

## The list

The **API tokens** card lists every token with its name, its first characters (for example `mpx_Ab3x…`, enough to tell tokens apart but not to use one), what it **may** do (its scopes), the admin who created it, when it was created, when it expires and when it was **last used** (updated about once a minute). Every admin sees every token.

## Revoke a token

Press **Revoke** next to the token and confirm. It stops working at once; the program that used it gets "unauthorized" from then on. A revoked token stays in the list, marked **Revoked**, and cannot be turned back on - create a new one instead. Creating and revoking a token is recorded in the **Audit trail**.

## Keep tokens safe

- Treat a token like a password: anyone who has it can read your series information (and, with **Request library scans**, start library scans).
- **Use HTTPS** when the program reaches MangaPixer from outside your home network (see [Reverse proxy and HTTPS](reverse-proxy-and-https.md)). On plain HTTP the token travels unencrypted. A program on the same server or in your home network can use plain HTTP.
- Give each program its own token, so you can revoke one without touching the others, and tick only the scopes it needs.
- Prefer an expiry. A token that never expires is convenient for a program on your own server; revoke it when you stop using the program.

## Limits

- A token can make up to **600 requests a minute** (scan requests included). Above that, MangaPixer answers `429 Too Many Requests` with a `Retry-After` header telling the program how many seconds to wait. This only stops a program stuck in a loop; normal syncing never comes near it. An admin's own browser is not limited.
- After **20 requests with a wrong token within 5 minutes** from one address, MangaPixer answers every token request from that address with `429` and `Retry-After` until those 5 minutes are over - also requests with a valid token, so a guessing program cannot try further.

- Scan requests: one started scan per library every **5 minutes** (see [Request a library scan](#request-a-library-scan)).

The server's admin can change these numbers in the configuration: `MangaPixer__Security__ApiTokens__RequestsPerMinute`, `MangaPixer__Security__ApiTokens__FailedAttemptsPerIp` and `MangaPixer__Security__ApiTokens__ScanCooldownMinutes` (environment variables, see [Configuration](configuration.md)).

## Privacy

Tokens never leave your server: MangaPixer does not send them anywhere, and it does not write them to its logs - log lines name a token only by its public id. The `Authorization` header is not logged either. See [Privacy and security](privacy-and-security.md).
