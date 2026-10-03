# API tokens

*New in 1.33.0.*

An **API token** lets another program read MangaPixer's series information without your password. The first program that uses one is [MangaList](https://github.com/dixit92/MangaList), which reads which of your folders are linked to which series, their volume lists and what is missing, so it can find gaps and file new downloads.

## What a token can do

A token can do **one thing: read the metadata export** - the pages under `/api/v1/export/`. Nothing else:

- it **cannot change anything**: only reading requests (`GET`) are accepted; any other request with a token is refused;
- it **cannot open any other page** of MangaPixer - not your libraries, not the reader, not Administration. Sent anywhere else, a token is ignored, exactly as if the program had sent nothing;
- it sees the series information of **every library**, like an admin does. It does not see reading progress, favorites or other per-user data.

A token belongs to the admin who created it, and works only while that account is an active admin. It stops working when you **revoke** it, when it **expires**, or when its admin is **deleted**, **disabled** or **no longer an admin** (it works again if that admin is made an admin again, unless it was revoked or has expired). A token keeps working when its admin changes their own password; after another admin **resets** that password, it pauses until the new password is set.

## Create a token

1. Open **MangaPixer Administration** and find the **API tokens** card (next to **Users**).
2. Under **New token**, give it a **Name** you will recognise later, for example *MangaList*.
3. Choose when it **expires**: after **30 days**, **90 days**, **1 year** (the default), or **Never**.
4. Press **Create token**.

MangaPixer shows the token **once**. It starts with `mpx_`. Press **Copy** and paste it into the other program's settings, then press **Done**. You will not see it again: MangaPixer keeps only a fingerprint of it (a SHA-256 hash), never the token itself. If you lose it, revoke it and create a new one.

The program sends the token with every request, in the `Authorization` header:

```text
Authorization: Bearer mpx_...
```

To check a connection, a program can ask `GET /api/v1/export/ping`; it answers with `"ok": true` and the server's time.

## The list

The **API tokens** card lists every token with its name, its first characters (for example `mpx_Ab3x…`, enough to tell tokens apart but not to use one), the admin who created it, when it was created, when it expires and when it was **last used** (updated about once a minute). Every admin sees every token.

## Revoke a token

Press **Revoke** next to the token and confirm. It stops working at once; the program that used it gets "unauthorized" from then on. A revoked token stays in the list, marked **Revoked**, and cannot be turned back on - create a new one instead. Creating and revoking a token is recorded in the **Audit trail**.

## Keep tokens safe

- Treat a token like a password: anyone who has it can read your series information.
- **Use HTTPS** when the program reaches MangaPixer from outside your home network (see [Reverse proxy and HTTPS](reverse-proxy-and-https.md)). On plain HTTP the token travels unencrypted. A program on the same server or in your home network can use plain HTTP.
- Give each program its own token, so you can revoke one without touching the others.
- Prefer an expiry. A token that never expires is convenient for a program on your own server; revoke it when you stop using the program.

## Limits

- A token can make up to **600 requests a minute**. Above that, MangaPixer answers `429 Too Many Requests` with a `Retry-After` header telling the program how many seconds to wait. This only stops a program stuck in a loop; normal syncing never comes near it. An admin's own browser is not limited.
- After **20 requests with a wrong token within 5 minutes** from one address, MangaPixer answers every token request from that address with `429` and `Retry-After` until those 5 minutes are over - also requests with a valid token, so a guessing program cannot try further.

The server's admin can change both numbers in the configuration: `MangaPixer__Security__ApiTokens__RequestsPerMinute` and `MangaPixer__Security__ApiTokens__FailedAttemptsPerIp` (environment variables, see [Configuration](configuration.md)).

## Privacy

Tokens never leave your server: MangaPixer does not send them anywhere, and it does not write them to its logs - log lines name a token only by its public id. The `Authorization` header is not logged either. See [Privacy and security](privacy-and-security.md).
