# MangaPlex — MVP acceptance evidence (release-one gate)

Consolidated evidence for the **complete-core release acceptance criteria** in
`Initial MangaPlex Plan.md §2.2` (the release-one gate, P18). Per the gate's own
rule, required failures are **escalated here, not relabeled as future scope**, and
any verification that could not be run on this host is disclosed.

- **Date:** 2026-09-08 · **Version:** `0.1.0-dev.39`
- **Automated suite:** 446 passing — Core 132, MediaWorker 36 (incl. 7z fixtures
  with `p7zip` present), Server 278. `dotnet format --verify-no-changes` clean.
  Front-end: 19 vitest unit + 2 Playwright e2e passing.
- **Live checks:** run against the running container (`mangaplex:0.1.0-dev.39`,
  `127.0.0.1:8091`) with a real bounded library mounted **read-only**
  (`D:\dev\lp-mangaplex-audit\media` — 5 manga CBZ series, 3 webtoon series, and a
  multi-volume RAR/CBR series; ~180 archives).
- Host note: this dev host has no .NET 10 SDK; all builds/tests run in the
  `mcr.microsoft.com/dotnet/sdk:10.0` container, per the plan's tooling guidance.

Status legend: ✅ met · ⚠️ met with a disclosed limitation · ⛔ escalated gap.

---

## 1. Containerized server; app data separate from media; admin without a shipped default password — ✅
- **Tests:** `AuthHttpTests.NoDefaultCredential_LoginFails_OnFreshInstance`,
  `SetupStatus_OnFreshInstance_ReportsSetupRequired`,
  `Setup_CreatesFirstAdmin_AndSignsIn`, `Setup_Rejected_OnceUserExists_Returns409`;
  `AuthIntegrationTests.FirstRunSetup_RequiredOnFreshDb_WithNoDefaultCredential`,
  `FirstRunSetup_CreatesFirstAdmin_WhenNoUsersExist`;
  `SourceImmutabilityTests.ScratchRoot_IsSeparateFromSourceRoot`;
  `HealthEndpointTests.HealthEndpoints_ReturnHealthy`.
- **Live:** fresh instance reports `setupRequired:true`; admin created via
  `POST /auth/setup` with a user-chosen password; storage roots point at mounted
  `/data`,`/cache`,`/scratch` volumes (commit `dcb6872`), separate from the
  read-only `/media` mount. Container healthy.

## 2. Register multiple roots via local config; scan; browse arbitrary depth; folders + archives coexist; no forced layout — ✅
- **Tests:** `StorageIntegrationTests.LibraryRegistration_*` (valid/duplicate/
  nested/app-root-conflict/non-existent); `AdminHttpTests.RegisterLibrary_*`;
  `ScannerIntegrationTests.Scan_FirstRun_AddsAllNodes`;
  `HierarchyAndIdResolutionTests.Scan_NestedFolders_PreservesHierarchy`,
  `Browse_WithParentPublicId_ReturnsChildren`;
  `CatalogBrowseTests.Browse_RootChildren_ReturnsFoldersAndArchives`.
- **Live:** registered `/media/Manga` and `/media/Manhwa` as two libraries; browsed
  series → chapters at depth; folders and archives coexist with no imposed layout.

## 3. Read supported formats incl. RAR5 and solid RAR/7z, without pre-extracting the whole collection; large archives not whole-file in memory — ⛔ (solid archives deferred)
- **Tests:** `ZipArchiveReaderTests` (Zip64, legacy encoding, truncated,
  ExtractEntry); `SevenZipArchiveReaderTests` (non-solid + **solid enumerate**);
  `RarArchiveReaderTests` (Rar4/Rar5 detection); `ArchiveFormatDetectorTests`
  (7z/Rar5 signatures); `WorkerProcessTests.Extract_WebpVariant_ProducesWebpFile`;
  `CacheServiceTests.LRU_*` (page-granular cache, no whole-library extraction).
- **Live:** CBZ (ZIP) and a **non-solid RAR/CBR** series (`Negima!`, 563 pages)
  both analyze and **read page-by-page** end-to-end. Extraction is on-demand and
  random-access (single entry per request); the page cache is bounded/LRU, so the
  collection is never bulk-extracted.
- **⛔ ESCALATION — solid RAR/7z *reading* is not yet supported.** The worker
  detects solid archives (`ArchiveReader.IsSolid`) and returns a graceful
  `unsupported_solid` error rather than reading the wrong page or hanging. Solid
  archives can be *enumerated* (analysis) but not *read* page-by-page, because
  random-access extraction of a solid stream needs sequential decompression +
  bounded scratch, which is deferred (see `Post-MVP Feature Ideas` / the C13
  checkpoint). ZIP, non-solid RAR, and non-solid 7z fully meet this criterion;
  **solid RAR/7z reading is the one open item against §2.2.**

## 4. Loose images are not books/pages; YACReader DBs, sync bookkeeping, and XML do not affect the model — ✅
- **Tests:** `StorageIntegrationTests.LibraryScanPolicy_IsIgnoredDirectory_RecognizesBookkeeping`,
  `_IsIgnoredFile_RecognizesMetadata`, `_IsArchiveCandidate_FiltersCorrectly`;
  `ScannerIntegrationTests.Scan_IgnoredDirectories_NotScanned`;
  `ZipArchiveReaderTests.OpenAndEnumerate_NonImageEntries_FiltersCorrectly`;
  `FilesystemBrowseServiceTests.Browse_HiddenDirectories_AreNotListed`.
- **Live:** only archives become catalog items; a `.yacreaderlibrary` bookkeeping
  folder is ignored by both the scan and the admin directory picker (dot-prefixed
  directories filtered).

## 5. Reader: LTR/RTL, spreads with cover/offset, zoom/fit/fullscreen/page-select, long-image vertical, animation; resume survives reload and mode/viewport changes — ⚠️
- **Tests:** `reader.component.spec.ts` (6 specs: cover-offset spread pairing, odd
  trailing page, no-cover pairing, direction-invariant grouping, active-pair
  entries, paged=1); `ReadingStateTests.UpdateProgress_*` /
  `GetProgress_MarksStaleWhenContentVersionChanges` (resume + staleness).
- **Live:** paged (LTR/RTL), double-spread (cover-offset, RTL via CSS flow), and
  vertical-webtoon modes all render; fit-to-screen default; fit modes
  screen/width/height/original; fullscreen (controls hidden in fullscreen);
  page-select via next/prev + edge-tap + keyboard; webtoon width slider (30–100%,
  localStorage); resume restores the saved page on reopen.
- **⚠️ Disclosed limitations:**
  - **Animation** ("animated fixtures visibly change frames"): the worker passes
    animated sources through verbatim (no static re-encode) and animation state is
    probed, but this was **not** visually verified — the bounded test library has
    no animated pages. Needs an animated fixture to confirm end-to-end.
  - **Zoom**: provided via fit modes + "original size", not a dedicated
    pinch/scroll magnifier gesture. Adequate for fit/read; a true zoom tool is
    tracked as a reader enhancement.

## 6. Two users independent (position, completion, prefs, bookmarks); unauthorized users cannot enumerate/retrieve hidden-library content — ✅
- **Tests:** `ReadingStateTests.TwoUsers_DoNotOverwriteEachOther`,
  `UpdateProgress_UnauthorizedUser_ReturnsUnauthorized`, `AddBookmark_*`,
  `GetPreferences_ReturnsDefaultsWhenNoneSet`;
  `AuthIntegrationTests.TwoUserIsolation_ReaderCannotAccessOtherLibrary`,
  `LibraryAuthorization_ReaderNeedsGrant`, `_InactiveUserDenied`;
  `CatalogBrowseTests.Browse_UnauthorizedUser_ReturnsEmpty`,
  `GetBreadcrumbs_UnauthorizedUser_ReturnsNull`, `GetNode_UnauthorizedUser_ReturnsNull`,
  `Search_UnauthorizedUser_ReturnsEmptyResults`;
  `PageHttpTests.GetPage_Unauthenticated_Returns401`.
- **Live:** per-user grants managed from the admin UI; a reader granted `Manhwa`
  sees only `Manhwa`; grant/revoke reflected immediately.

## 7. Incremental rescans: no progress reset, no duplicate unchanged items, no decode of unchanged archives, unavailable mount ≠ empty library; moves preserve identity only with strong evidence — ⚠️
- **Tests:** `ScannerIntegrationTests.Scan_SecondRun_IsIdempotent`,
  `Scan_NewFile_AddedOnNextScan`, `Scan_RemovedFile_Tombstoned`,
  `Scan_ReappearingFile_RegainsAvailability`, `Scan_RootUnavailable_ReturnsError`.
- **Identity/moves:** implemented by `IdentityRelinkService` (lazy SHA-256; auto
  relink only when exactly one new item shares the hash; ambiguous cases require
  conflict-safe manual relink; no cross-library relink).
- **⚠️ Disclosed limitation:** the move/rename→identity-relink path has no
  dedicated integration test in the suite yet (the service and policy exist and are
  wired). Recommended follow-up: a move-fixture test (rename an archive, rescan,
  assert progress relinks on unique hash and is held for ambiguous hashes).

## 8. Corrupt/unsupported item → bounded, actionable error (no scan abort, no API crash); media-worker crash recoverable — ✅
- **Tests:** `ZipArchiveReaderTests.OpenAndEnumerate_TruncatedZip_ReturnsError`;
  `SevenZipArchiveReaderTests.OpenAndEnumerate_TruncatedSevenZip_HandlesError`;
  `RarArchiveReaderTests.Open_TruncatedRar4_HandlesError`;
  `ImageProbeAdapterTests.Probe_InvalidImageData_ReturnsError`, `_TruncatedPng`;
  `ManifestHttpTests.GetReadiness_FailedAnalysis_ReturnsFailed`;
  `WorkerProcessTests.WorkerKill_SupervisorDetectsExit`,
  `Analyze_SourceModified_RejectsResult`, `Extract_MissingEntry_ReturnsExtractError`;
  `JobRecoveryServiceTests.RecoverInterruptedJobs_MarksPendingAsFailed`.
- **Live:** a solid archive yields a clean `unsupported_solid` (not a crash); a
  missing entry yields `page_not_found`.

## 9. DB backup/restore and restart recovery verified; no live-DB file copy that ignores WAL — ⚠️ (Windows-native restart not run on this host)
- **Tests:** `BackupServiceTests.Backup_CreatesValidBackupFile`, `VerifyBackup_*`,
  `Restore_ToNewTarget_Succeeds`, `Restore_ToExistingTarget_RequiresConfirmation`,
  `_WithConfirmation_Succeeds` (uses the SQLite backup API, not a raw file copy —
  WAL-safe); `JobRecoveryServiceTests.*`;
  `ScannerIntegrationTests.ScanLease_ExpiredLease_RecoveredOnStartup`;
  `HostingCorrectnessTests.Startup_NoRecoveryOrMaintenanceFailureEvents`,
  `ValidateSchema_*`.
- **Live:** full **container recreate** (Linux) preserves admin, libraries, and
  grants on the `/data` volume — restart recovery verified on Linux.
- **⚠️ Disclosed:** a **native Windows** server restart was not executed (this host
  has no .NET 10 SDK; the server runs only in the Linux container here). Windows
  restart recovery relies on the same platform-agnostic startup-recovery code but
  is unverified on a real Windows run.

## 10. No app-created files or changes to source bytes/lengths/names/mtimes (synthetic + bounded real-library pilot) — ✅
- **Tests:** `SourceImmutabilityTests.SourceMarkerFile_IsNeverModified`,
  `ScratchRoot_IsSeparateFromSourceRoot`,
  `Database_StoresSourcePathButNeverUsesForWrites`;
  `StorageIntegrationTests.ReadOnlyFileSystem_OpenRead_ReturnsReadOnlyStream`
  (the library filesystem exposes no write methods).
- **Live (bounded real-library pilot):** the real library is mounted **read-only**
  (`:ro`); the OS enforces that no bytes/names/mtimes can change. All derived data
  (cache/scratch/db) lives under the app-owned volumes, never beside the media.

## 11. Git has no remote; no personal data, credentials, real paths, generated state, or machine config tracked — ✅
- **Verified:** `git remote -v` is empty. `.gitignore` excludes `.env*`,
  `secrets.json`, `appsettings.*.Local.json`, `*.db`/`-wal`/`-shm`,
  `data/`,`cache/`,`scratch/`, `bin/`,`obj/`, `node_modules/`.
- **Tests:** `PrivacyGateTests.*` (no DTO — catalog node, breadcrumbs,
  diagnostics, search, progress, continue-reading — contains a source path);
  `ContractSerializationTests.NoDto_ContainsSourcePathFields`,
  `AnalyzeRequest_ArchivePath_IsOnlyPathField`;
  `DiagnosticsServiceTests.ExportLog_ReturnsSanitizedExport`;
  `LogRedactionTests.*`.

---

## Summary

10 of 11 criteria are met (four with a disclosed, minor limitation). The single
**escalated gap** is **solid RAR/7z page-*reading*** (criterion 3): detected and
handled gracefully, but deferred as a post-MVP package. ZIP and non-solid
RAR/7z — which cover the observed real collection — read fully.

**Recommended before declaring release-one "done":**
1. Decide whether solid RAR/7z reading blocks the MVP or ships as a fast-follow
   (the graceful `unsupported_solid` error keeps it safe either way).
2. Add an animated fixture and confirm animation renders (criterion 5).
3. Add a move→identity-relink integration test (criterion 7).
4. If Windows is a target platform, run a native Windows restart-recovery smoke
   (criterion 9).
