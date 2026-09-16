# Security Policy

MangaPixer is a self-hosted server that people point at their own media libraries and often expose on a home network. Security reports are welcome and are handled privately until a fix is available.

## Supported versions

Security fixes are made on the **latest minor release line only**. A fix ships as a new patch release on that line (for example `1.13.x`), and older lines are not patched. To stay covered, upgrade to the newest release.

| Version | Supported |
|---|---|
| Latest minor line (currently 1.13.x) | Yes |
| Any older minor line | No, please upgrade |

The newest version is always listed on the Releases page and in [CHANGELOG.md](CHANGELOG.md).

## Reporting a vulnerability

**Do not open a public issue, discussion, or pull request for a security problem.**
Please report it privately through GitHub's private vulnerability reporting:

1. Open the repository's **Security** tab.
2. Choose **Report a vulnerability**.

3. Fill in the advisory form. Only the maintainers can see it.


<!-- TODO(owner): enable private vulnerability reporting under
Settings > Code security > Private vulnerability reporting, or the
"Report a vulnerability" button above will not appear. -->

If you cannot use GitHub, e-mail the maintainer instead: [smitdixit92@gmail.com](mailto:smitdixit92@gmail.com).

A useful report includes:

- the affected MangaPixer version (`GET /api/v1/system/info`, or the app footer);
- how the server is deployed (Docker/Compose, Unraid, Windows, reverse proxy in front or not);
- the steps or a proof of concept needed to reproduce the issue;
- the impact as you understand it (what an attacker can read, change, or reach).

Please use a disposable test instance and synthetic media. Do not include real library paths, titles, credentials, or other personal data in the report.

## What to expect

| Step | Target |
|---|---|
| Acknowledgement of your report | within 7 days |
| Initial assessment (confirmed, needs more info, or out of scope) | within 14 days |
| Fix released for a confirmed issue | as fast as severity warrants, normally within 90 days |

MangaPixer is maintained by volunteers, so these are good-faith targets rather than guarantees. You will be kept informed as the report moves forward. Once a fix is released, the advisory is published and you are credited, unless you prefer to stay anonymous. Please keep the details private until then.

## Scope

In scope is the code in this repository: the server (`src/MangaPixer.Server`), the media worker (`src/MangaPixer.MediaWorker`), the web reader (`web/`), and the container image and Compose files built from `deploy/`.

These are the security properties MangaPixer is designed to hold. A way to break any of them is a vulnerability:

- **Authentication.** There are no default credentials. A fresh instance has zero users, and the first administrator can be created only once, through the first-run setup flow. Additional users join through one-time activation tokens. Passwords are stored with the ASP.NET Core Identity password hasher, and login attempts are rate limited per IP address and per username.
- **Session cookies.** The auth cookie is `HttpOnly` and `SameSite=Strict`. It is marked `Secure` whenever the request arrives over HTTPS.
- **CSRF.** State-changing API requests require an antiforgery token that is tied to an `HttpOnly`, `SameSite=Strict` cookie.
- **Authorization.** Non-admin users can reach only the libraries an administrator has granted them. That applies to browse, search, home, and direct item, page, and thumbnail requests. (The per-user "Private" library flag with Incognito mode is a convenience that hides a library from the user's own listings. It is not an access control, and direct links still work for that user by design.)
- **Source media is never modified.** The server and worker only read library directories. Nothing is written, moved, renamed, deleted, or extracted into them. Thumbnails and caches live in the server's own storage. Any path that leads to a write, or to reading a file outside a configured library (path traversal, for example through archive entry names), is in scope.
- **Privacy.** API responses never expose server file paths. Logs are designed not to contain paths, titles, passwords, tokens, cookies, or archive entry names. The app makes no telemetry, analytics, or third-party network calls.
- **Archive handling.** Archives are opened in a separate worker process. Crafted archives or images that crash the server, escape the worker, or exhaust resources far beyond their size are in scope.

### Out of scope

- Deployments that ignore the documented setup. Examples are exposing the server to the internet without a TLS-terminating reverse proxy, mounting media read-write, or running with a weakened container configuration.
- Findings that require an attacker who already has administrator access, or shell access to the host.
- Vulnerabilities in third-party dependencies with no demonstrated impact on MangaPixer. Please report those upstream. Dependency updates arrive through Dependabot.
- Denial of service through sheer request volume, social engineering, and physical attacks.

## Hardening tips for operators

- Put MangaPixer behind a reverse proxy that terminates HTTPS. The Compose files publish the port on loopback (`127.0.0.1`) only, by design.
- Keep the media mounts read-only (`:ro`), as the shipped Compose files do.
- Upgrade to new releases promptly. Only the latest minor line receives fixes.
