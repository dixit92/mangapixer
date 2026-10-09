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

## Author records (1.38.0)

- Captured 2026-10-09 with 5 requests in total, `User-Agent: MangaPixer/1.37.1 (+https://github.com/dixit92/mangapixer)`, 3 s
  apart: `GET /v1/authors/{author_id}` for public authors already named by the Berserk and Solo Leveling records above -
  `author-miura-kentaro.json` (22635311083), `author-mori-kouji.json` (38824888050), `author-chugong.json` (71651794005),
  `author-jang-seong-rak.json` (61564873920, credited as "DUBU (Redice Studio)" on Solo Leveling) - and the unknown id `1`
  (a `404` with an empty body; no file).
- Trimmed to the fields the provider reads (`id`, `name`, `associated[].name`, `actualname`) plus a few it ignores (`url`,
  `genres`, `stats`, `last_updated`), to prove unknown fields are tolerated. Image, birthday, birthplace, gender, social links,
  comments and the admin block are dropped.
