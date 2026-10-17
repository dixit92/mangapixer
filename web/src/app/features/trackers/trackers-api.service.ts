import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import {
  AdminTrackerAccountDto,
  AdminTrackerSettingsDto,
  TrackerAccountDto,
  TrackerAttentionDto,
  TrackerBatchAction,
  TrackerBatchResultDto,
  TrackerConnectStartResponse,
  TrackerDatesRequest,
  TrackerEntryDto,
  TrackerFromTrackerResultDto,
  TrackerNodeOverrideDto,
  TrackerPreviewDto,
  TrackerSeriesIdsDto,
  TrackerSeriesPageDto,
  TrackerSelectableSeriesListDto,
  TrackerSeriesRowDto,
  TrackerSeriesSummaryDto,
  TrackersOverviewDto,
  UpdateTrackerSettingsRequest,
  UpdateTrackerUserSettingsRequest,
} from '../../core/api/api-types';

/**
 * Tracker sync (1.43.0): the caller's own trackers (`/api/v1/trackers`) and Administration > Trackers
 * (`/api/v1/admin/trackers`). Cookie + CSRF only (the xsrf interceptor adds the header). The browser never contacts a
 * tracker's API: every request here goes to this server, which relays it with the user's own sign-in. A password, a client
 * secret or an OAuth code passes through this service once and is never kept in it.
 */
@Injectable({ providedIn: 'root' })
export class TrackersApiService {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/v1/trackers';
  private readonly adminBase = '/api/v1/admin/trackers';

  // --- The caller's own trackers ---

  overview(): Observable<TrackersOverviewDto> {
    return this.http.get<TrackersOverviewDto>(this.base, { withCredentials: true });
  }

  /** OAuth trackers (MyAnimeList): the answer names the tracker's sign-in page, which the browser then opens. */
  connectStart(slug: string, acceptedConsentVersion: number): Observable<TrackerConnectStartResponse> {
    return this.http.post<TrackerConnectStartResponse>(`${this.base}/${slug}/connect/start`, { acceptedConsentVersion },
      { withCredentials: true });
  }

  /** The callback page: the code and state the tracker handed back (already removed from the address bar). */
  connectFinish(slug: string, code: string, state: string): Observable<TrackerAccountDto> {
    return this.http.post<TrackerAccountDto>(`${this.base}/${slug}/connect`, { code, state }, { withCredentials: true });
  }

  /** Password trackers (MangaUpdates): used once by the server to get a sign-in token, never stored. */
  connectPassword(slug: string, username: string, password: string, acceptedConsentVersion: number): Observable<TrackerAccountDto> {
    return this.http.post<TrackerAccountDto>(`${this.base}/${slug}/connect/password`, { username, password, acceptedConsentVersion },
      { withCredentials: true });
  }

  disconnect(slug: string): Observable<void> {
    return this.http.post<void>(`${this.base}/${slug}/disconnect`, {}, { withCredentials: true });
  }

  updateSettings(slug: string, request: UpdateTrackerUserSettingsRequest): Observable<TrackerAccountDto> {
    return this.http.put<TrackerAccountDto>(`${this.base}/${slug}/settings`, request, { withCredentials: true });
  }

  entries(slug: string): Observable<TrackerEntryDto[]> {
    return this.http.get<TrackerEntryDto[]>(`${this.base}/${slug}/entries`, { withCredentials: true });
  }

  /** "Sync now": what syncs on its own (a held jump stays held). */
  syncNow(slug: string, seriesId: string): Observable<TrackerEntryDto> {
    return this.http.post<TrackerEntryDto>(`${this.entry(slug, seriesId)}/sync`, {}, { withCredentials: true });
  }

  /** "Send": the held jump too (never lowers the tracker). */
  send(slug: string, seriesId: string): Observable<TrackerEntryDto> {
    return this.http.post<TrackerEntryDto>(`${this.entry(slug, seriesId)}/send`, {}, { withCredentials: true });
  }

  /** "Set <tracker> to N": may LOWER the tracker - the caller asks the user first. */
  setProgress(slug: string, seriesId: string, chapters: number | null, volumes: number | null): Observable<TrackerEntryDto> {
    return this.http.post<TrackerEntryDto>(`${this.entry(slug, seriesId)}/set`, { chapters, volumes }, { withCredentials: true });
  }

  setDontSync(slug: string, seriesId: string, dontSync: boolean): Observable<TrackerEntryDto> {
    return this.http.put<TrackerEntryDto>(`${this.entry(slug, seriesId)}/dont-sync`, { dontSync }, { withCredentials: true });
  }

  /** First-connect preview: the server reads a batch from the tracker and writes nothing; call again while `remaining` > 0. */
  preview(slug: string): Observable<TrackerPreviewDto> {
    return this.http.post<TrackerPreviewDto>(`${this.base}/${slug}/preview`, {}, { withCredentials: true });
  }

  syncOverrides(): Observable<TrackerNodeOverrideDto[]> {
    return this.http.get<TrackerNodeOverrideDto[]>(`${this.base}/sync-overrides`, { withCredentials: true });
  }

  /** true = sync on (also inside a Private library or for doujinshi), false = off, null = as default (removes the switch). */
  /** 1.44.0: "Set <tracker>'s volumes to N" - only the volume count its own chapters complete (read again on the server first). */
  volumesFromChapters(slug: string, seriesId: string): Observable<TrackerEntryDto> {
    return this.http.post<TrackerEntryDto>(`${this.base}/${slug}/entries/${encodeURIComponent(seriesId)}/volumes-from-chapters`, null,
      { withCredentials: true });
  }

  setSyncOverrides(nodeIds: string[], enabled: boolean | null): Observable<TrackerNodeOverrideDto[]> {
    return this.http.put<TrackerNodeOverrideDto[]>(`${this.base}/sync-overrides`, { nodeIds, enabled }, { withCredentials: true });
  }

  // --- The Tracking page (1.43.0 redesign, lane G's API) ---

  /**
   * One page (50 rows, by title) of the caller's series on a tracker. `filter` is a chip (`TrackerListBucket`, `Attention`) or
   * `All`; `q` searches titles (on this server only). Stored state only - the server contacts no tracker for it.
   */
  seriesPage(slug: string, page: number, filter: string, q: string): Observable<TrackerSeriesPageDto> {
    let params = new HttpParams().set('page', page);
    if (filter && filter !== 'All') params = params.set('filter', filter);
    if (q.trim()) params = params.set('q', q.trim());
    return this.http.get<TrackerSeriesPageDto>(`${this.base}/${slug}/series`, { params, withCredentials: true });
  }

  /**
   * 1.43.2 "Select all in this chip": every series id matching the chip + search (the list's own rules, at most 2000 - `truncated`),
   * with which batch actions apply to each. Stored state only.
   */
  selectableSeries(slug: string, filter: string, q: string): Observable<TrackerSelectableSeriesListDto> {
    let params = new HttpParams();
    if (filter && filter !== 'All') params = params.set('filter', filter);
    if (q.trim()) params = params.set('q', q.trim());
    return this.http.get<TrackerSelectableSeriesListDto>(`${this.base}/${slug}/series/ids`, { params, withCredentials: true });
  }

  /**
   * 1.43.2 Select mode (replaces "Send all"): one action for up to 100 series (the caller sends larger selections in chunks); each
   * series gets what its per-series route does, the server skips the ones it does not apply to and stops at the daily cap.
   */
  batch(slug: string, action: TrackerBatchAction, seriesIds: string[]): Observable<TrackerBatchResultDto> {
    return this.http.post<TrackerBatchResultDto>(`${this.base}/${slug}/entries/batch`, { action, seriesIds }, { withCredentials: true });
  }

  /** The account menu's badge: series waiting for Send + series a tracker is ahead on. */
  attention(): Observable<TrackerAttentionDto> {
    return this.http.get<TrackerAttentionDto>(`${this.base}/attention`, { withCredentials: true });
  }

  /** The series header's Tracking popover: one row per connected tracker for the folder's series (no tracker request). */
  summary(nodeId: string): Observable<TrackerSeriesSummaryDto> {
    return this.http.get<TrackerSeriesSummaryDto>(`${this.base}/nodes/${encodeURIComponent(nodeId)}/summary`, { withCredentials: true });
  }

  /** "Set progress from <tracker> to MangaPixer": marks chapters / volumes read HERE up to the tracker's numbers; never unmarks. */
  fromTracker(slug: string, seriesId: string): Observable<TrackerFromTrackerResultDto> {
    return this.http.post<TrackerFromTrackerResultDto>(`${this.entry(slug, seriesId)}/from-tracker`, {}, { withCredentials: true });
  }

  /** The user's start / finish dates (`yyyy-MM-dd`; null = automatic). Both are replaced. MyAnimeList only. */
  setDates(slug: string, seriesId: string, request: TrackerDatesRequest): Observable<TrackerSeriesRowDto> {
    return this.http.put<TrackerSeriesRowDto>(`${this.entry(slug, seriesId)}/dates`, request, { withCredentials: true });
  }

  // --- Administration > Trackers ---

  adminList(): Observable<AdminTrackerSettingsDto[]> {
    return this.http.get<AdminTrackerSettingsDto[]>(this.adminBase, { withCredentials: true });
  }

  adminUpdate(slug: string, request: UpdateTrackerSettingsRequest): Observable<AdminTrackerSettingsDto> {
    return this.http.put<AdminTrackerSettingsDto>(`${this.adminBase}/${slug}`, request, { withCredentials: true });
  }

  adminAccounts(): Observable<AdminTrackerAccountDto[]> {
    return this.http.get<AdminTrackerAccountDto[]>(`${this.adminBase}/accounts`, { withCredentials: true });
  }

  adminDisconnect(slug: string, userId: string): Observable<void> {
    return this.http.post<void>(`${this.adminBase}/${slug}/accounts/${encodeURIComponent(userId)}/disconnect`, {},
      { withCredentials: true });
  }

  seriesIds(nodeId: string): Observable<TrackerSeriesIdsDto> {
    return this.http.get<TrackerSeriesIdsDto>(`${this.adminBase}/nodes/${encodeURIComponent(nodeId)}/ids`, { withCredentials: true });
  }

  /** An empty id removes the admin's id (the stored one applies again). */
  setSeriesId(nodeId: string, slug: string, remoteId: string): Observable<TrackerSeriesIdsDto> {
    return this.http.put<TrackerSeriesIdsDto>(`${this.adminBase}/nodes/${encodeURIComponent(nodeId)}/ids/${slug}`, { remoteId },
      { withCredentials: true });
  }

  private entry(slug: string, seriesId: string): string {
    return `${this.base}/${slug}/entries/${encodeURIComponent(seriesId)}`;
  }
}
