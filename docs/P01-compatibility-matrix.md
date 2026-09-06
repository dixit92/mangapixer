# MangaPlex P01 Compatibility Matrix

This document records the actual pass/fail results from P01 archive/image/SQLite
compatibility testing. No claim is made based on upstream READMEs alone.

## Archive Formats

| Format | Detection | Enumeration | Extraction | Solid | Nested Dirs | Truncated | Notes |
|--------|-----------|-------------|------------|-------|-------------|-----------|-------|
| ZIP/CBZ | PASS | PASS | PASS | N/A | PASS | PASS (error) | Via `System.IO.Compression` fixtures + SharpCompress `ArchiveFactory.OpenArchive` |
| ZIP64 | PASS | PASS | PASS | N/A | PASS | — | 10-entry fixture verified |
| Empty ZIP | PASS | PASS (0 entries) | — | N/A | — | — | Empty archive signature detected |
| Legacy/Unicode filenames | PASS | PASS | — | N/A | — | — | CJK filenames (`第002ページ.png`) enumerated correctly |
| Non-image entries | PASS | PASS (filtered) | — | N/A | — | — | `ComicInfo.xml`, `__MACOSX/`, `Thumbs.db` correctly identified by `ShouldIgnore` |
| 7z/CB7 (non-solid) | PASS | PASS | PASS | — | PASS | PASS (error) | Requires `p7zip-full` for fixture generation; `7z -ms=off` |
| 7z/CB7 (solid) | PASS | PASS | PASS | PASS | — | — | `7z -ms=on`; SharpCompress reports `IsSolid=true` for all 7z archives |
| RAR4/CBR (signature) | PASS | — | — | — | — | PASS (error) | Signature detection only; real RAR read tests require pre-built fixtures |
| RAR5/CBR (signature) | PASS | — | — | — | — | — | Signature detection only |
| Encrypted ZIP | — | — | — | — | — | — | SharpCompress 0.50.x writer does not support password-protected ZIP creation; detection via `IsEncrypted` flag works |
| Multipart RAR | — | — | — | — | — | — | Detected via `IsSplitAfter` flag; full multipart tests deferred to P07 |

### RAR Limitation

RAR is a proprietary format. SharpCompress can **read** RAR archives but cannot create them.
P01 tests verify signature detection only. Full RAR read tests require either:
1. Pre-built tiny RAR fixtures with verified provenance (created with a licensed RAR tool), or
2. Downloaded SharpCompress test fixtures from the official repository.

This is a **known gap** — not a format-support claim. RAR read compatibility will be
verified in P07 (worker integration) when real fixture files are available.

## Image Formats

| Format | Probe (header) | Full Read | Animation Detection | Alpha Detection | Notes |
|--------|----------------|-----------|---------------------|-----------------|-------|
| PNG | PASS | PASS | Unknown (APNG possible) | PASS | Minimal 1x1 PNG fixture |
| JPEG | PASS | PASS | NotAnimated | — | Minimal JFIF fixture |
| GIF | PASS | PASS | Unknown (animated possible) | PASS | Minimal GIF89a fixture |
| BMP | PASS | PASS | NotAnimated | — | Minimal 1x1 24-bit BMP |
| WebP | PASS | PASS | Unknown (animated possible) | PASS | Minimal VP8L fixture |
| AVIF | — | — | — | — | Requires real AVIF fixture; minimal synthetic not available |
| TIFF | — | — | — | — | Requires real TIFF fixture; minimal synthetic not available |
| Invalid data | PASS (error) | — | — | — | Returns `ImageProbeResult` with `Error` set |
| Empty data | PASS (error) | — | — | — | Returns `ImageProbeResult` with `Error` set |
| Truncated PNG | PASS (error) | — | — | — | Returns `ImageProbeResult` with `Error` set |

### Image Limitation

AVIF and TIFF require real image fixtures (not synthetic minimal headers) for probe verification.
These will be added in P07 with verified-provenance test images.

Animation state detection returns `Unknown` for formats that **can** be animated (GIF, PNG/APNG,
WebP, AVIF). The `ReadWithFrames` method provides definitive frame count by fully reading the image.

## SQLite Runtime

| Feature | Status | Version | Notes |
|---------|--------|---------|-------|
| SQLite version | PASS | 3.53.3 (via `SQLitePCLRaw.lib.e_sqlite3`) | Meets >= 3.51.0 requirement |
| WAL mode | PASS | — | `PRAGMA journal_mode=WAL` succeeds |
| FTS5 | PASS | — | `CREATE VIRTUAL TABLE ... USING fts5(...)` works |
| Trigram tokenizer | PASS | — | `tokenize='trigram'` works for substring matching |
| User authentication | N/A | — | MangaPlex uses ASP.NET Core Identity, not SQLite auth |

### SQLite Configuration

- Bundle: `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5 (includes FTS5, JSON1, R*Tree)
- Library: `SQLitePCLRaw.lib.e_sqlite3` 3.53.3
- Provider: `SQLitePCLRaw.provider.e_sqlite3` 3.0.5
- Core: `SQLitePCLRaw.core` 3.0.5
- `Microsoft.Data.Sqlite.Core` 10.0.0 (Core version, no implicit bundle)

## Dependency Versions (P01 verified)

| Package | Version | Advisories | Notes |
|---------|---------|------------|-------|
| SharpCompress | 0.50.4 | None | Archive reading (ZIP, RAR, 7z) |
| Magick.NET-Q8-AnyCPU | 14.17.1 | None | Image probing and transformation |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5 | None | FTS5/trigram-enabled SQLite bundle |
| SQLitePCLRaw.lib.e_sqlite3 | 3.53.3 | None | Native SQLite library |

## Resource Limits (P01 frozen defaults)

These are the initial defaults for the worker resource gates. They are configurable
and will be tuned in P07/P08 based on real-world testing.

| Limit | Default | Rationale |
|-------|---------|-----------|
| Max archive entry count | 10,000 | Prevents pathologically large archives from exhausting memory |
| Max uncompressed entry size | 256 MB | Single image should never be this large |
| Max total uncompressed archive size | 2 GB | Bounded extraction budget |
| Max image dimensions | 50,000 x 50,000 px | Magick.NET read limit |
| Max image pixel count | 250 MP | Prevents decompression bombs |
| Max frame count (animated) | 1,000 | Prevents GIF/WEBP animation bombs |
| Worker memory budget | 512 MB | Per-worker process limit |
| Worker deadline | 120 seconds | Per-task timeout |
| Scratch directory budget | 4 GB | Bounded temporary extraction space |
| Thumbnail cache budget | 2 GB | File-backed, LRU-evicted |
| Reading page cache budget | 8 GB | File-backed, LRU-evicted, active-stream pinned |

## Test Results

- **Total tests**: 65 (22 Core + 36 MediaWorker + 7 Server)
- **Passed**: 65
- **Failed**: 0
- **Build**: 0 warnings, 0 errors (Release, .NET 10.0.11, SDK 10.0.400)

## Escalation Items

1. **RAR read tests**: Need pre-built RAR fixtures with verified provenance. Currently only
   signature detection is tested. This is a P07 prerequisite.
2. **AVIF/TIFF probe tests**: Need real image fixtures (not synthetic minimal headers).
   This is a P07 prerequisite.
3. **Encrypted ZIP**: SharpCompress 0.50.x cannot create password-protected ZIPs. Detection
   works via `IsEncrypted` flag. Full encrypted-archive handling is a P07 task.
4. **Multipart RAR**: Detection via `IsSplitAfter` works but full multipart read tests are
   deferred to P07.
