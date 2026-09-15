# Reverse proxy and HTTPS

The server speaks **plain HTTP only**, on port 8080 inside the container.
It has no TLS support, no HTTP-to-HTTPS redirect and no HSTS. For HTTPS,
put a reverse proxy in front of it and let the proxy handle certificates.

The canonical Compose file already prepares for this. It publishes the port
on `127.0.0.1` only, so the server is unreachable from the network except
through a proxy running on the same host. (The Unraid file publishes port
6266 on all interfaces instead; point your proxy at that.)

## Requirements for the proxy

- **Give MangaPlex its own hostname** (or its own port). Serving it under a
  sub-path such as `https://example.lan/mangaplex/` is not supported: the app
  expects to live at `/`.
- **Pass the original `Host` header through.** The server uses it to build
  [activation links](#activation-links).
- **Allow uploads up to 128 MiB** if you want to restore backups through the
  proxy (see [Backup and restore](backup-and-restore.md#restoring-a-backup)).
- **Allow slow responses.** The first page of an archive on a drive that has
  to spin up can take a minute or more. Page requests stay open until the page
  is ready.
- No WebSocket or streaming configuration is needed; everything is ordinary
  HTTP requests.

## Caddy

Caddy obtains and renews certificates by itself, redirects HTTP to HTTPS,
passes the `Host` header through and has no upload limit or short read
timeout by default. For a name on your LAN that the public internet cannot
reach, use Caddy's internal certificate authority:

```caddyfile
comics.home.arpa {
    tls internal
    reverse_proxy 127.0.0.1:8080
}
```

With a real public domain whose DNS points at the proxy, drop the `tls
internal` line and Caddy uses Let's Encrypt instead. With `tls internal`,
each device needs to trust Caddy's root certificate once; Caddy's
documentation explains how.

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
        proxy_set_header Host $host;
    }
}
```

`proxy_set_header Host $host` matters. Without it, nginx sends
`127.0.0.1:8080` as the host, and activation links point there.

Use the `http2 on;` directive only with nginx 1.25.1 or later. On older
versions, write `listen 443 ssl http2;` instead.

## What the server sees behind a proxy

The server does not process `X-Forwarded-For` or `X-Forwarded-Proto`. Every
request looks to it like a plain-HTTP request from the proxy's own address.
In practice:

- **Sign-in rate limiting is shared.** The limit of 10 failed sign-ins per IP
  address in 5 minutes applies to the proxy's address. That limit is shared by
  every user, so one person repeatedly mistyping a password can block sign-in
  for everyone for a few minutes. The per-username limit and account lockout
  still work normally. You can raise the per-IP limit with
  [`MangaPlex__Security__RateLimit__MaxAttemptsPerIp`](configuration.md#sign-in-protection).
- **Cookies are not marked `Secure`.** They are still `HttpOnly` and
  `SameSite=Strict`, and the browser only ever sends them to your MangaPlex
  hostname. To make sure they never travel over plain HTTP, redirect HTTP to
  HTTPS at the proxy (Caddy does this by default). You can also add HSTS at the
  proxy, for example `header Strict-Transport-Security "max-age=31536000"` in
  Caddy.
- **Logs never contain client IP addresses anyway.** See
  [Troubleshooting](troubleshooting.md#logs).

### Activation links

When an admin creates a user without a password, the activation link is
built from the address the admin's browser used, as the server sees it.
Behind a TLS proxy it starts with `http://` rather than `https://`, for
example `http://comics.example.lan/activate?token=…`.

- If your proxy redirects HTTP to HTTPS, the link still works as is.
- Otherwise change `http://` to `https://` before you send it.
- If the link shows `127.0.0.1` or another internal address, your proxy is not
  passing the `Host` header (see the nginx note above).

## Exposing the server to the internet

The server is built for a home network: a small number of known users,
accounts created by an admin, and no public sign-up. If you still make it
reachable from the internet:

- Always use HTTPS via a proxy, as shown above.
- Keep the container's port bound to loopback (the canonical default) so the
  proxy is the only way in.
- Use strong passwords, and keep the admin account for administration.
- Keep the server updated.
