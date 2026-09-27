import { Injectable, inject } from '@angular/core';
import { Observable, finalize, of, shareReplay, tap } from 'rxjs';

import { SeriesInfoDto } from '../../core/api/api-types';
import { MetadataApiService } from '../../features/metadata/metadata-api.service';
import { MetadataStateService } from '../../features/metadata/metadata-state.service';

/** Bounded so a long session over a large library never grows without limit. */
export const SERIES_INFO_CACHE_SIZE = 60;

/** Entries older than this are fetched again (an admin may have edited the record). */
export const SERIES_INFO_CACHE_TTL_MS = 5 * 60_000;

interface Entry {
  info: SeriesInfoDto;
  at: number;
}

/**
 * Small client cache for the local `GET nodes/{id}/series-info` (1.27.0), used by the
 * hover summary so resting on the same card twice costs one request. Keyed by node id,
 * least-recently-used eviction at {@link SERIES_INFO_CACHE_SIZE} entries, a short TTL,
 * and invalidated by every link change announced through `MetadataStateService.changed$`
 * (Link, Undo, Unlink, Don't match, clear, and `refresh`). A request in flight is shared,
 * so two hovers on one node never fetch twice. Nothing is sent to any provider: this is
 * the same local endpoint the side panel reads.
 */
@Injectable({ providedIn: 'root' })
export class SeriesInfoCacheService {
  private readonly api = inject(MetadataApiService);
  private readonly entries = new Map<string, Entry>();
  private readonly inFlight = new Map<string, Observable<SeriesInfoDto>>();
  /** Bumped per node on invalidation so a response already in flight is not stored. */
  private readonly generations = new Map<string, number>();

  constructor() {
    inject(MetadataStateService).changed$.subscribe((c) => this.invalidate(c.nodeId));
  }

  /** A fresh cached value, if any (no request). */
  peek(nodeId: string): SeriesInfoDto | null {
    const entry = this.entries.get(nodeId);
    if (!entry) return null;
    if (Date.now() - entry.at > SERIES_INFO_CACHE_TTL_MS) {
      this.entries.delete(nodeId);
      return null;
    }
    // Refresh recency (Map keeps insertion order: re-insert = most recent).
    this.entries.delete(nodeId);
    this.entries.set(nodeId, entry);
    return entry.info;
  }

  /** The node's series information: cached, shared in flight, or one GET. */
  get(nodeId: string): Observable<SeriesInfoDto> {
    const cached = this.peek(nodeId);
    if (cached) return of(cached);
    const pending = this.inFlight.get(nodeId);
    if (pending) return pending;

    const generation = this.generations.get(nodeId) ?? 0;
    const request = this.api.getSeriesInfo(nodeId).pipe(
      tap((info) => {
        if ((this.generations.get(nodeId) ?? 0) === generation) this.store(nodeId, info);
      }),
      finalize(() => {
        if (this.inFlight.get(nodeId) === request) this.inFlight.delete(nodeId);
      }),
      shareReplay({ bufferSize: 1, refCount: false }),
    );
    this.inFlight.set(nodeId, request);
    return request;
  }

  invalidate(nodeId: string): void {
    this.entries.delete(nodeId);
    this.inFlight.delete(nodeId);
    this.generations.set(nodeId, (this.generations.get(nodeId) ?? 0) + 1);
  }

  clear(): void {
    for (const id of [...this.entries.keys(), ...this.inFlight.keys()]) this.invalidate(id);
  }

  /** Number of cached entries (tests). */
  get size(): number {
    return this.entries.size;
  }

  private store(nodeId: string, info: SeriesInfoDto): void {
    this.entries.delete(nodeId);
    this.entries.set(nodeId, { info, at: Date.now() });
    while (this.entries.size > SERIES_INFO_CACHE_SIZE) {
      const oldest = this.entries.keys().next().value as string;
      this.entries.delete(oldest);
    }
  }
}
