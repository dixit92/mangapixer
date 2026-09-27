# Metadata auto-match golden set fixtures

Recorded responses of the public [MangaUpdates API](https://api.mangaupdates.com) (v1), used by the
stage-2 matcher golden set (`Features/Metadata/AutoMatch/Golden` in `MangaPixer.Server.Tests`; moved from
`MangaPixer.Core.Tests` in 1.27.0). Series data (c) MangaUpdates - credited here as the API's acceptable use
policy asks. The tests replay these files as HTTP answers through the production MangaUpdates provider,
mapping and automatic retrieval loop; no test ever contacts the real network.

- Captured 2026-09-26 by the matcher-core lane with a one-off recorder: `User-Agent:
  MangaPixer-Metadata`, at least 0.75 s between requests, PUBLIC well-known titles only (never a name
  from a real library). The request count is recorded in the lane's hand-off.
- 2026-09-27 (1.26.1, integrator): one more `series.40032173896.json` (the main record of a title already
  in the set), recorded the same way (one request) when the "Title: Subtitle" head rule made the harness
  need it.
- 2026-09-27 (1.27.0, matcher-accuracy lane): 25 more requests, recorded the same way (limit 60): page 2 of four
  existing searches (the matcher now reads page 2 on a tie or when nothing reaches the review floor), 7 new
  searches (plus 2 page-2 reads) and 9 GETs for the live-run lookalike cases, and `publishers[]` merged into three
  existing series files (`13015731700`, `15180124327`, `72274276213`) from a fresh GET - their other fields are the
  2026-09-26 recording.
- `search.<slug>.json`: `POST /v1/series/search` with `{search, page: 1, perpage: 10, filter_types}`;
  `search.<slug>.p2.json` is page 2 (the file carries `"page": 2`).
  `filter_types` is the fixed automatic-search filter (`Novel`, `Doujinshi`, `Artbook`, `Drama CD`);
  files ending in `.dj.json` were recorded with Doujinshi allowed (the folder Content setting
  "Doujinshi & adult one-shots"). Each file keeps the query, the filter and the response trimmed to
  `total_hits` and, per result, `record.series_id, title, type, year` plus `hit_title`.
- `series.<id>.json`: `GET /v1/series/{id}`, trimmed to what the matcher reads: `series_id, title,
  associated[].title, type, year, status, latest_chapter, authors[] (name, type)`, the
  `Webtoon/Webcomic` category votes (when present) and `related_series[] (relation_type,
  related_series_id)`, and since 1.27.0 `publishers[] (publisher_name, type, notes)` (the English totals).
  Descriptions, images, genres, other categories, recommendations and per-user blocks are dropped.
- Which responses exist is decided by the golden-set harness itself: a missing fixture fails the
  case with the exact request it needs, so the set stays reproducible.
