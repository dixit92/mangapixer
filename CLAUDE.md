# MangaPlex — Claude Code project instructions

## Docker testing environments

Two Docker environments exist for this project:

- **Local Docker (default)** — use this for regular issue resolution, feature
  development, and day-to-day container testing. This is the default; no
  special setup needed beyond what's in `deploy/compose.yaml` /
  `deploy/compose.dev.yaml`.
- **Unraid host** (`deploy/compose.unraid.yaml`, port 6266, `mangaplex`
  container is live there) — reserved for:
  - testing against large/real-sized libraries that don't fit local dev setups
  - destructive testing (volume wipes, corruption scenarios, crash/recovery,
    anything you would NOT want to risk against a shared or persistent env)

  Do **not** default to the Unraid host for routine work. Only use it when the
  task specifically calls for scale or destruction that local Docker can't
  safely provide, and prefer to ask the user for confirmation before running
  destructive commands there (deleting volumes, `docker system prune`, etc.),
  same as you would for any other production-adjacent, hard-to-reverse
  action.

  Connection details (host, credentials) are intentionally **not** committed
  here — see `.claude/unraid-docker.md` (git-ignored, local machine only).
