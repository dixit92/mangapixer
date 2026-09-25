# MangaUpdates API fixtures

Recorded responses of the public [MangaUpdates API](https://api.mangaupdates.com) (v1), used by the
MangaUpdates provider tests. Series data (c) MangaUpdates - credited here as the API's acceptable use
policy asks. Tests replay these files through a fake `HttpMessageHandler`; no test ever contacts the
real network.

- Captured 2026-09-25 with 4 requests in total, `User-Agent: MangaPixer-Metadata`, public titles only:
  `POST /v1/series/search` for "Berserk" and "Solo Leveling" (`perpage` 5) and `GET /v1/series/{id}`
  for the top hit of each.
- Trimmed to the fields the provider reads (plus a few it ignores, to prove unknown fields are
  tolerated): search records keep `series_id, title, url, description` (cut to 300 characters),
  `image, type, year, bayesian_rating, genres, last_updated` and `hit_title`; the per-user `metadata`
  block is dropped. Records keep the top 25 categories by votes, and authors/publishers without their
  URLs. Recommendations, related series, publications, forum and admin blocks are dropped.
