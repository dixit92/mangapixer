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
- 2026-09-28 (1.28.0, matcher-author-cover lane): 26 more requests, recorded the same way (at least 1 s apart,
  public titles only): 1 connectivity probe (a GET of an existing record, not stored), page 1 of three existing
  searches again (`Jigokuraku`, `Tensei Kizoku no Isekai Boukenroku`, `Isekai de Cheat Skill wo Te ni Shita Ore wa,
  Genjitsu Sekai wo mo Musou Suru`) whose `record.image.url` (`original`, `thumb`) was merged into the existing files
  (other fields are the earlier recording), 19 cover thumbnails and 3 full covers for the cover comparison.
- 2026-09-28 (1.28.0, part 3): 1 more request, recorded the same way: page 1 of `Solo Leveling` with the fixed filter
  plus `Manga`, `Manhua` (a declared manhwa as the search filter) - `search.solo-leveling.manhwa.json`. The harness keys
  searches by any `filter_types` beyond the fixed four.
- `cover.<image name>.jpg`: a MangaUpdates cover THUMBNAIL (`GET https://cdn.mangaupdates.com/image/thumb/<image name>.<ext>`),
  re-encoded tiny (at most 80 px, JPEG) - the harness answers an image GET by the file name in the URL. `local.<series
  id>.webp`: a local cover thumbnail made from that series' full cover (3% cropped per side, at most 96 px, WebP) - the
  stored thumbnail of a work's volume 1 in the cover cases. Cover images (c) their publishers, via MangaUpdates; used
  only to test the matcher.
- `search.<slug>.json`: `POST /v1/series/search` with `{search, page: 1, perpage: 10, filter_types}`;
  `search.<slug>.p2.json` is page 2 (the file carries `"page": 2`).
  `filter_types` is the fixed automatic-search filter (`Novel`, `Doujinshi`, `Artbook`, `Drama CD`);
  files ending in `.dj.json` were recorded with Doujinshi allowed (the folder Content setting
  "Doujinshi & adult one-shots"). Each file keeps the query, the filter and the response trimmed to
  `total_hits` and, per result, `record.series_id, title, type, year` plus `hit_title` (and, in the three files
  re-recorded in 1.28.0, `record.image.url`).
- `series.<id>.json`: `GET /v1/series/{id}`, trimmed to what the matcher reads: `series_id, title,
  associated[].title, type, year, status, latest_chapter, authors[] (name, type)`, the
  `Webtoon/Webcomic` category votes (when present) and `related_series[] (relation_type,
  related_series_id)`, and since 1.27.0 `publishers[] (publisher_name, type, notes)` (the English totals).
  Descriptions, images, genres, other categories, recommendations and per-user blocks are dropped.
- Which responses exist is decided by the golden-set harness itself: a missing fixture fails the
  case with the exact request it needs, so the set stays reproducible.
