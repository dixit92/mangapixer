# Grand Comics Database API fixtures

Recorded responses of the anonymous [Grand Comics Database](https://www.comics.org) API, used by the GCD provider, routing and
Identify tests (1.32.0). The data is from the Grand Comics Database and is licensed
[CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/); these files keep it unchanged under the same licence. Tests
replay them through a fake `HttpMessageHandler`; no test ever contacts the real network. No cover image is stored here (GCD's
cover scans are licensed for identification only); image tests use a synthetic PNG.

- Captured 2026-10-02 with 9 requests, `User-Agent: MangaPixer/1.31.1 (+https://github.com/dixit92/mangapixer)`, public titles
  only, at most one request every 12 seconds:
  - `search-bone-1991.json` - `/api/series/name/Bone/year/1991/` (3 series: the 1991 issue run and two substring hits);
  - `series-4347.json` - `/api/series/4347/` (Bone, 1991, Cartoon Books);
  - `issue-49773.json` - `/api/issue/49773/` (its first issue: credits, page count, an empty `cover`);
  - `publisher-672.json` - `/api/publisher/672/`;
  - `search-saga-2012.json` - `/api/series/name/Saga/year/2012/` (the issue run 63051 AND the trade paperbacks 69146, plus
    Spanish / German / Dutch editions and substring hits);
  - `search-blacksad.json` - `/api/series/name/Blacksad/` (38 series: the French original and its translations);
  - `search-asterix-1961.json` - `/api/series/name/Asterix/year/1961/` (the Dargaud album series);
  - `search-maus.json` - `/api/series/name/Maus/` (page 1 of 178 substring hits - the English "Maus" is not on it);
  - `not-found.json` - `/api/series/999999999/` (the 404 body).
- Unchanged, as recorded (all fields kept, to prove unknown fields are tolerated).
