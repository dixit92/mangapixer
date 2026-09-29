# MangaDex API fixtures

Recorded responses of the public [MangaDex API](https://api.mangadex.org), used by the MangaDex companion tests
(1.29.0). Data (c) MangaDex and its contributors - credited here as MangaDex's terms ask. Tests replay these
files through a fake `HttpMessageHandler`; no test ever contacts the real network. No cover IMAGE is recorded:
image tests use synthetic images only.

- Captured 2026-09-29 with 11 requests to `api.mangadex.org`, `User-Agent: MangaPixer-DevFixtureRecording/1.29`,
  at least 1.3 s apart, public well-known titles only:
  - `GET /manga?title=...&limit=10&order[relevance]=desc&contentRating[]=safe,suggestive,erotica,pornographic&includes[]=cover_art`
    for Berserk, Jigokuraku, Chainsaw Man, Tower of God and JoJo no Kimyou na Bouken Part 3: Stardust Crusaders
    (`search.*.json`);
  - `GET /manga/{id}/aggregate?includeUnavailable=1` of the Berserk, Chainsaw Man, Tower of God and JoJo Part 3
    companions (`aggregate.*.json`; Tower of God answers `"volumes": []`);
  - `GET /cover?manga[]={id}&locales[]=en&locales[]=ja&order[volume]=asc&limit=100` of Berserk and JoJo Part 3
    (`cover.*.json`).
- Trimmed: descriptions are dropped; search records keep titles, alternative titles, links, original language,
  last volume / chapter, status, year, content rating, dates, tag names and the `cover_art` relation; aggregate
  chapters lose their upload ids (`id`, `others`); covers lose their descriptions.
- What they exercise: the cross-link rule (the record's own `links.mu` names the MangaUpdates record; ties between
  an original and an official colour edition, a "Book Version" ranked first without an AniList link, two JoJo
  records with the same link; the parenthetical strip "Jigokuraku (KAKU Yuuji)"; the 2005 namesake that must be
  rejected), the `[]` shapes, the `none` bucket, and JoJo Part 3's continuous volume numbering across parts
  (aggregate up to 28, its own covers up to 16) that the foreign-numbering guard must refuse.
