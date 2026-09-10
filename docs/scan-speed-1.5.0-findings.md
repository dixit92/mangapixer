# Scan speed lane (1.5.0) — phase-1 findings and decisions

Scratch notes for the integrator. Written by the lane agent from reading
`LibraryScanCoordinator` + storage layer and from an in-container profile.
Not vault documentation; the integrator owns the vault.

## Profile (baseline, before any change)

Synthetic library: 200 series × 1 volume × 50 archives = 10,400 nodes, tiny
files, Linux `sdk:10.0` container on the Windows host's bind mount, SQLite
with the production pragmas (`synchronous=FULL`, WAL). Fresh `DbContext` per
scan, exactly as the admin scan endpoint does.

| Measurement                                    | Baseline | After lane |
|------------------------------------------------|---------:|-----------:|
| Directory walk incl. per-file `GetSourceStamp` | 121 ms   | n/a (removed) |
| Directory walk using enumeration metadata only | 46 ms    | 53 ms      |
| First scan (all nodes new)                     | 175.9 s  | 3.2 s      |
| Rescan, nothing changed                        | 10.1 s   | 0.44 s     |
| Rescan, one new file                           | 10.1 s   | 0.44 s     |

The benchmark test used to take these numbers was temporary and is not
committed (it created 10k files; too slow for the suite). The measured
phases are also logged per scan at Debug level (event 3024) so a real
library can be profiled from the container logs.

## What the code did (confirmed)

1. `ObserveAsync` recursed the whole tree every scan and called
   `GetSourceStamp` per archive — an extra `File.Exists` + `FileInfo` stat on
   top of the enumeration, which already carries size and mtime. Locally that
   is 75 ms per 10k files; on SMB/NFS it is two or three extra round trips per
   archive. **The walk itself is not the cost driver** (0.1 s of 176 s).
2. `ReconcileAsync` committed **twice per new archive** (`SaveChangesAsync`
   after the node insert to obtain the id, again after the archive item).
   With `synchronous=FULL` each commit is an fsync: ~17 ms per node, i.e. the
   entire 176 s first-scan cost.
3. The no-change rescan cost 10 s for 10k nodes although nothing was
   inserted. Cause: every node's `LastSeenScanRevision` is updated each scan,
   and the FTS5 trigger `catalog_search_au` was `AFTER UPDATE ON
   catalog_nodes` (any column) — so every rescan re-tokenised the trigram
   search index for the whole library (~1 ms/node).
4. Content change was compared as `(ByteLength, ModificationTicks)` per
   archive **but the branch was dead in production**: it was guarded by
   `existing.ArchiveItem is not null`, and the nodes were loaded without the
   archive item, so with a fresh context the navigation was always null. The
   existing tests reused one context across scans (EF fix-up populated the
   navigation), which is why they passed. Consequence: an archive rewritten in
   place was never re-analysed by a scan.
5. A moved/renamed archive was tombstoned at the old path and created fresh
   at the new one: new node id → re-analysis, thumbnail regenerated, and all
   per-user rows keyed by item id (read marks, progress, bookmarks, overrides)
   orphaned on the tombstone.
6. `ScanResult.NodesTombstoned` was never populated (always 0), so the
   persisted scan-run counters under-reported.

## Decisions

- **Batching (done).** Nodes are added with the `Parent` navigation set (EF
  resolves `ParentId` and orders inserts, including a 1:1 `ArchiveItem`
  child) and committed every 500 new nodes. Existing-node parent repair uses
  the same mechanism, so a parent created in the same batch is handled.
- **FTS trigger narrowed (done).** `catalog_search_au` is now `AFTER UPDATE
  OF DisplayName, RelativePath, LibraryId`. It is dropped and re-created at
  startup so existing databases pick it up. Renames/moves still re-index.
- **Content-change detection fixed (done).** Archive items are loaded in one
  query per scan; a stamp change bumps `ContentVersion`, clears the stored
  signature and marks the item pending so the post-scan enqueue re-analyses
  it.
- **Move detection (done).** New nullable column
  `archive_items.ContentSignature` (migration
  `20260910234134_AddArchiveContentSignature`), populated by the worker pool
  after a successful analysis from the bytes on disk (size + SHA-256 of the
  first and last 64 KiB; stamp re-checked after hashing). During
  reconciliation, an archive missing from its old path is re-pointed to a new
  observation only when: the old row has a signature; byte length agrees; the
  new file's freshly computed signature equals it; and the pairing is 1:1
  (duplicates → safe fallback). Legacy rows never match until their next
  analysis populates the signature.
- **Directory-signature incremental walk (#3): NOT implemented.** Profiling
  shows the walk is ~0.1 % of scan time even before removing the redundant
  stats; the wins are in the write path. Beyond the SMB/NFS mtime
  unreliability the brief calls out, directory mtime does not change when a
  file is rewritten in place, so an mtime-gated skip would reintroduce the
  missed-content-change bug that item 4 just fixed. Not worth a correctness
  risk for a sub-second phase. Revisit only if a profile of a real network
  share shows enumeration dominating.
- **Filesystem watching (#4): follow-up, not in this lane.**
- Folder renames: child archives are recognised individually as moves; the
  folder node itself is still new, so a folder-level reader default
  (`FolderReaderDefaultEntity`) does not follow a renamed folder. Noted as a
  possible follow-up.
