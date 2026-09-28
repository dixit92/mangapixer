# AniList GraphQL fixtures

Hand-written responses in the documented shape of the public [AniList GraphQL API](https://docs.anilist.co)
(`POST https://graphql.anilist.co`), used by the chapters-per-volume tests of the Missing report (1.28.0).
They are NOT recordings: no request was sent to AniList while writing them (the AniList lookup is behind an
owner-approved privacy amendment). Public, well-known titles only; the totals are illustrative. Tests replay
these files through a fake `HttpMessageHandler`; no test ever contacts the real network.

- `search-fma.json`: `Page(perPage: 5) { media(search, type: MANGA, format_not: NOVEL) }` - the main entry plus a
  differently titled entry that must not be picked.
- `get-fma.json`: `Media(id, type: MANGA)` of a FINISHED entry with volume and chapter totals.
- `get-running.json`: a RELEASING entry without totals.
