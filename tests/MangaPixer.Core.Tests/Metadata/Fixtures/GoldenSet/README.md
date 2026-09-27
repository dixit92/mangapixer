# Metadata auto-match golden set fixtures

Recorded responses of the public [MangaUpdates API](https://api.mangaupdates.com) (v1), used by the
stage-2 matcher golden set (`Metadata/AutoMatch/Golden`). Series data (c) MangaUpdates - credited
here as the API's acceptable use policy asks. The tests replay these files; no test ever contacts
the real network.

- Captured 2026-09-26 by the matcher-core lane with a one-off recorder: `User-Agent:
  MangaPixer-Metadata`, at least 0.75 s between requests, PUBLIC well-known titles only (never a name
  from a real library). The request count is recorded in the lane's hand-off.
- `search.<slug>.json`: `POST /v1/series/search` with `{search, page: 1, perpage: 10, filter_types}`.
  `filter_types` is the fixed automatic-search filter (`Novel`, `Doujinshi`, `Artbook`, `Drama CD`);
  files ending in `.dj.json` were recorded with Doujinshi allowed (the folder Content setting
  "Doujinshi & adult one-shots"). Each file keeps the query, the filter and the response trimmed to
  `total_hits` and, per result, `record.series_id, title, type, year` plus `hit_title`.
- `series.<id>.json`: `GET /v1/series/{id}`, trimmed to what the matcher reads: `series_id, title,
  associated[].title, type, year, status, latest_chapter, authors[] (name, type)`, the
  `Webtoon/Webcomic` category votes (when present) and `related_series[] (relation_type,
  related_series_id)`. Descriptions, images, genres, other categories, publishers, recommendations
  and per-user blocks are dropped.
- Which responses exist is decided by the golden-set harness itself: a missing fixture fails the
  case with the exact request it needs, so the set stays reproducible.
