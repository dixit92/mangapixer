# Reverse proxy and HTTPS

The server speaks **plain HTTP only**, on port 8080 inside the container. It has no TLS support, no HTTP-to-HTTPS redirect and no HSTS. For HTTPS, put a reverse proxy in front of it and let the proxy handle certificates.

The canonical Compose file already prepares for this. It publishes the port on `127.0.0.1` only, so the server is unreachable from the network except through a proxy running on the same host. (The Unraid file publishes port 6266 on all interfaces instead; point your proxy at that.)

## Requirements for the proxy

- **Give MangaPixer its own hostname** (or its own port). Serving it under a sub-path such as `https://example.lan/mangapixer/` is not supported: the app expects to live at `/`.
- **Pass the original `Host` header through, and send `X-Forwarded-For` and `X-Forwarded-Proto`.** The server uses them for [activation links](#activation-links), secure cookies and sign-in rate limiting (see [What the server sees behind a proxy](#what-the-server-sees-behind-a-proxy)). Caddy sends all three by default.
- **Allow uploads up to 128 MiB** if you want to restore backups through the proxy (see [Backup and restore](backup-and-restore.md#restoring-a-backup)).
- **Allow slow responses.** The first page of an archive on a drive that has to spin up can take a minute or more. Page requests stay open until the page is ready.
- No WebSocket or streaming configuration is needed; everything is ordinary HTTP requests.

## Caddy

Caddy obtains and renews certificates by itself, redirects HTTP to HTTPS, passes the `Host` and `X-Forwarded-*` headers through, and has no upload limit or short read timeout by default. For a name on your LAN that the public internet cannot reach, use Caddy's internal certificate authority:

```caddyfile
comics.home.arpa {
    tls internal
    reverse_proxy 127.0.0.1:8080
}
```

With a real public domain whose DNS points at the proxy, drop the `tls internal` line and Caddy uses Let's Encrypt instead. With `tls internal`, each device needs to trust Caddy's root certificate once; Caddy's documentation explains how.

## nginx

```nginx
server {
    listen 80;
    server_name comics.example.lan;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl;
    http2 on;
    server_name comics.example.lan;

    ssl_certificate     /etc/ssl/comics.example.lan.crt;
    ssl_certificate_key /etc/ssl/comics.example.lan.key;

    client_max_body_size 128m;   # backup restore uploads
    proxy_read_timeout   300s;   # first pages from sleeping drives

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_set_header Host              $host;
        proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

The three `proxy_set_header` lines matter. Without `Host`, nginx sends `127.0.0.1:8080` as the host and activation links point there. Without the `X-Forwarded-*` lines, the server thinks every request is plain HTTP from the proxy itself (see below).

Use the `http2 on;` directive only with nginx 1.25.1 or later. On older versions, write `listen 443 ssl http2;` instead.

## What the server sees behind a proxy

The server reads the `X-Forwarded-For`, `X-Forwarded-Proto` and `X-Forwarded-Host` headers, but **only from a trusted proxy**. By default it trusts connections from loopback (`127.0.0.0/8`, `::1`) and the private IPv4 ranges (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`). That covers a proxy on the same machine, on a Docker network, or elsewhere on your LAN. Headers from any other address are ignored, so a client on the internet cannot pretend to be someone else.

With a trusted proxy that sends these headers:

- **Cookies are marked `Secure`** when the original request was HTTPS, so the browser never sends them over plain HTTP. They are also `HttpOnly` and `SameSite=Strict`. Plain-HTTP access on your LAN keeps working with non-secure cookies.
- **Sign-in rate limiting sees each client's real address**, so one person mistyping a password does not lock out everyone behind the proxy.
- **Activation links** use your public address and `https://`.

If your proxy connects from a public or other untrusted address (for example a proxy on another server reaching MangaPixer over the internet or a VPN range), add it with [`MangaPixer__Network__KnownProxies` or `MangaPixer__Network__KnownNetworks`](configuration.md#network). Only one proxy in front of MangaPixer is supported; with two chained proxies, the server sees the address of the one in front of it.

If the proxy does *not* send the headers, every request looks like plain HTTP from the proxy: cookies are not marked `Secure`, the per-IP sign-in limit (10 failures in 5 minutes) is shared by everyone, and activation links start with `http://`.

Whatever you set up, you can add HSTS at the proxy for extra safety, for example `header Strict-Transport-Security "max-age=31536000"` in Caddy. Logs never contain client IP addresses; see [Troubleshooting](troubleshooting.md#logs).

### Activation links

When an admin creates a user without a password, the activation link is built from the address the admin's browser used. Behind a proxy that sends the headers above, it starts with your public `https://` address.

- If the link starts with `http://`, your proxy is not sending `X-Forwarded-Proto`, or it is not trusted (see above). Change `http://` to `https://` before you send it, or fix the proxy.
- If the link shows `127.0.0.1` or another internal address, your proxy is not passing the `Host` header (see the nginx note above).

## Exposing the server to the internet

The server is built for a home network: a small number of known users, accounts created by an admin, and no public sign-up. If you still make it reachable from the internet:

- Always use HTTPS via a proxy, as shown above.
- Keep the container's port bound to loopback (the canonical default) so the proxy is the only way in.
- Use strong passwords, and keep the admin account for administration.
- Keep the server updated.
