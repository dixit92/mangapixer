# Reader load harness (performance profiling)

Synthetic multi-reader load for a MangaPixer **test instance you run yourself** (synthetic or test media only - never a
library with personal data, never an instance other people use). Standard-library Python 3.10+, no packages.

## `reader_load.py`

Simulates N readers. Each logs in, opens items of one library one after another and reads them like the web reader:

| Profile | View | Viewport (CSS px x DPR) | Mean think time |
|---|---|---|---|
| `paged-desktop` | paged, fit screen | 1280x900 x1 | 2.0 s |
| `paged-phone` / `paged-tablet` | paged | 390x844 x3 / 820x1180 x2 | 2.0 s |
| `paged-skim` | paged | 1280x900 x1 | 0.5 s |
| `webtoon-phone` / `webtoon-desktop` | webtoon, 100 % width | 390x844 x3 / 1280x900 x1 | 0.8 s |

- Page URLs carry the reader's `?maxDim=<bucket>&filter=` for that viewport (the 1080 / 1440 / 2160 ladder of
  `page-variant.ts`; above the top rung the full-size transcode is asked for).
- Paged: on each turn the current page is needed and the next 6 and previous 2 pages are prefetch targets; webtoon:
  the visible page plus the next 4. Each page URL is requested once per item, as the reader's dedup set does: a
  prefetched page is a browser-cache hit when it becomes current, or the reader waits for that same request.
- `--prefetch-mode capped` (default; the 1.42.0 reader): at most 2 prefetches in flight, started only once the current
  page is there (paged views), sent with `X-MangaPixer-Prefetch: 1`. `--prefetch-mode legacy`: the earlier reader, all
  targets at once and unmarked - use it to compare against an older server build.
- Each reader has 6 connections (a browser's HTTP/1.1 limit per host); the current page is queued ahead of prefetches.
- After every turn the reader saves its progress (`PUT /api/v1/reading/progress/{id}` with `If-Match`).
- Think time varies +-30 % around the profile's mean.

Recorded per request: time to headers (TTFB) and to the last byte, status, bytes and the server's `Server-Timing`
fields (`cache` hit / miss, `db`, `slot` wait, `worker`, `open` / `read` / `encode`). Per turn: how long the reader
waited for the page ("ready"; 0 when the prefetch had already brought it). With `--diagnostics` (admin account) the
server's media metrics snapshot (`GET /api/v1/operations/diagnostics`, `media`) is stored before and after the run.

```bash
MP_PERF_PASSWORD='<test admin password>' python3 scripts/perf/reader_load.py \
  --base http://127.0.0.1:8097 --user admin --readers 2 --profiles paged-desktop,webtoon-phone \
  --duration 60 --library "Test fixture" --run-id r1-b --scenario b --csv r1-b.csv --summary r1-b.json --diagnostics
```

The password is read from an environment variable (never a command-line argument). Output: a CSV with one row per
request and per turn, and a JSON summary with p50 / p95 / max for the current page separately from prefetches, the
first page of an item separately from later turns, progress saves, status counts (503 = no worker in time, 499 = client
left) and cache hits / misses.

## `summarize_runs.py`

```bash
python3 scripts/perf/summarize_runs.py <folder of run summaries> [--exclude r3-c] [--json all.json]
```

Groups the summaries by scenario and reports, for each measure, the median over runs with the spread (IQR and
min-max), plus the server-side window of every run (difference of the cumulative metric buckets before / after).

## Method notes (noisy hosts)

- Repeat every scenario at least 5 times and interleave them (a b c d a b c d ...) rather than running each block in
  one go, so a drift in the host's load spreads over all scenarios.
- Record the host load around each run (for example `/proc/loadavg` and `docker stats --no-stream`) and drop or flag
  runs taken during a spike; report medians with their spread, never a single run.
- The page cache starts empty after every server restart; restart before a run for a cold-cache run, or repeat the same
  items right after a run for a warm one.

## Library browsing and the server-side split (1.46.0)

Measurement-only server switches (all off by default; set them only on an instance you measure):

| Setting | Effect |
|---|---|
| `MangaPixer:Perf:RequestTiming=true` | Every `/api` response carries `Server-Timing: app, sql, sqln, lockw` (app time, EF SQL time, SQL command count, write-transaction BEGIN wait). The admin diagnostics snapshot gets a `perf` block: per route template, per SQL statement shape (literals and numbers replaced) and per phase lap (`phase:browse.*`, `phase:enrich.*`, `phase:marks.*`, `phase:home.*`), BEGIN / COMMIT waits, GC heap and thread pool. Raw ADO commands are not seen by the EF interceptor; the phase laps cover them. |
| `MangaPixer:Perf:SkipHostedServices=<ClassName>,...` | Leaves those hosted services out (e.g. `ThumbnailBackfillHostedService,ContentSignatureBackfillHostedService,ComicInfoBackfillHostedService`). |
| `MangaPixer:Perf:TrackerMarksWithNetworkOff=true` | Keeps the display-only tracker card marks while `Trackers:NetworkDisabled` is on (nothing is sent either way). |
| `Facets:Index:FullPassSeconds=<n>` (+ `Facets:Index:SweepSeconds=1`) | Full tag-index passes back to back (a sustained rebuild load). |

- `library_load.py discover` stores ids only (largest library, its biggest folder, a series with stored info, readable items, card
  items) in a targets file; `run` times the user actions as the web app sends them (`browse_root`, `browse_big_folder`, the read /
  completion filters, the Volumes view, a later page, search plain / read filter / tag, Info panel, series page, home, the root's card
  covers, reader open + first page at both image qualities, Back from the reader in a small and a long folder), concurrently like a
  browser and interleaved over N rounds; `--first-only` takes one sample per point (cold, right after a restart); `covers` asks for
  card covers in a loop (a background thumbnail load on an instance without thumbnail files).
- `summarize_library.py` prints the points, the slowest request of each with its `Server-Timing` split and the diagnostics window (top
  statements and phases); `compare_library.py` puts runs side by side.
- `frontend_probe.mjs` (Playwright) times the first card / first page of a library root and a folder (cold and warm), the long list's
  scroll (frame times, long tasks, DOM nodes, JS heap). No screenshots.
- `MeasureAccounts/` adds two measurement accounts to a throwaway COPY of a database through the app's own Identity user manager and
  hasher (`dotnet run --project scripts/perf/MeasureAccounts -- <copy.db> <password-file> [--local-backups] [--synthetic-tracking]`);
  it never reads or changes other users. `--synthetic-tracking` gives the reader a token-less tracker account with one entry per
  linked series (an upper bound for the tracker-mark cost).
