import { ChangeDetectionStrategy, Component, OnDestroy, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { firstValueFrom } from 'rxjs';

import {
  FolderMatchApplyItem,
  FolderMatchKind,
  FolderMatchRowDto,
  IdentifyCandidateDto,
} from '../../../core/api/api-types';
import { MetadataApiService } from '../metadata-api.service';
import { FolderMatchApiService } from './folder-match-api.service';
import {
  MAX_WEB_SEARCHES,
  WEB_SEARCH_PACE_MS,
  artistChoiceLabel,
  plural,
  recordChoiceLabel,
  resultText,
  statusText,
  stopsTheRun,
} from './folder-match-labels';

export interface FolderMatchDialogData {
  /** The selected FOLDERS (1-200). */
  folders: { id: string; displayName: string }[];
  /** Archives in the selection (ignored). */
  ignored: number;
}

/** What the dialog marked (the caller refreshes those nodes and says so). */
export interface FolderMatchDialogResult {
  kind: FolderMatchKind;
  marked: string[];
  failed: number;
  queued: number;
}

type WebState = 'idle' | 'waiting' | 'running' | 'done' | 'failed';

/** One preview row and what the admin chose for it. */
export interface FolderMatchRowState {
  row: FolderMatchRowDto;
  ticked: boolean;
  /** The chosen local match (index into the row's artists / records), -1 = none. */
  pick: number;
  /** Artists without a match: mark with the folder's own name (opt-in). */
  ownName: boolean;
  /** Collections without a local match: search the web for it (opt-in per row, under "Search the web for the rest"). */
  webTicked: boolean;
  webText: string;
  webState: WebState;
  webCandidates: IdentifyCandidateDto[];
  /** The chosen web candidate, -1 = none (a web result is never chosen automatically). */
  webPick: number;
  webError?: string;
  /** After Apply: `ok` or an error code / message. */
  result?: string;
  resultMessage?: string;
}

/** The search results kept per folder (the provider's first page, best first). */
const MAX_WEB_CANDIDATES = 8;

/**
 * "Match folders by name" (1.38.0): several selected folders marked at once as artist folders or as collections about a series, by
 * their names against names already stored on this server. The admin picks the kind, sees a preview with tick boxes (nothing changes
 * before Apply), resolves ambiguous rows, and may opt in to an artist folder named after the folder itself. For collections, "Search the
 * web for the rest" sends each ticked folder's search text (shown, editable) to MangaUpdates through the Identify search - one per second,
 * one at a time, at most 50 per run, stopping on the provider's backoff or the daily budget; results are only offered, never linked.
 */
@Component({
  selector: 'app-folder-match-dialog',
  standalone: true,
  imports: [
    FormsModule, MatButtonModule, MatButtonToggleModule, MatCheckboxModule, MatDialogModule, MatFormFieldModule, MatInputModule,
    MatProgressBarModule, MatSelectModule,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Match folders by name</h2>
    <mat-dialog-content class="content">
      <p class="lead" data-testid="folder-match-lead">
        {{ folderCount }}
        @if (data.ignored) { <span class="note">- {{ ignoredText }} ignored: only folders can be marked</span> }
      </p>
      <mat-button-toggle-group class="kind" [value]="kind()" (change)="choose($event.value)" [disabled]="busy()"
                               aria-label="Match the folders as" data-testid="folder-match-kind">
        <mat-button-toggle value="Artists" data-testid="folder-match-artists">As artist folders</mat-button-toggle>
        <mat-button-toggle value="Collections" data-testid="folder-match-collections">As collections</mat-button-toggle>
      </mat-button-toggle-group>
      <p class="small" data-testid="folder-match-explain">
        @if (kind() === 'Collections') {
          Each folder's name is compared with the titles and other titles of the series stored on this server. A matched folder becomes
          a collection about that series: it shows the series, and its works are matched on their own.
        } @else if (kind() === 'Artists') {
          Each folder's name is compared with every name of the artists stored on this server (any spelling, any name order, and their
          other names once fetched). A matched folder becomes that artist's folder, with the artist's main name as its declared creator.
        } @else {
          Pick what the folders are. Their names are compared only with what is stored on this server; nothing is sent.
        }
      </p>
      @if (loading()) { <mat-progress-bar mode="indeterminate" /> }
      @if (error(); as e) { <p class="error" role="alert">{{ e }}</p> }

      @if (rows().length) {
        <p class="summary" data-testid="folder-match-summary">{{ summary() }}</p>
        <ul class="rows">
          @for (r of rows(); track r.row.nodeId) {
            <li class="row" [class.muted]="!tickable(r)" [attr.data-testid]="'folder-match-row-' + r.row.nodeId">
              <mat-checkbox class="tick" [checked]="r.ticked" [disabled]="!tickable(r) || busy()" (change)="tick(r, $event.checked)"
                            [aria-label]="'Mark ' + r.row.displayName" [attr.data-testid]="'folder-match-tick-' + r.row.nodeId" />
              <div class="body">
                <div class="name">{{ r.row.displayName || r.row.nodeId }}</div>
                <div class="status" [class.warn]="r.row.status === 'Decided'">{{ status(r) }}</div>
                @if (choices(r).length > 1 || (r.row.status === 'Decided' && choices(r).length > 0)) {
                  <mat-form-field appearance="outline" class="pick" subscriptSizing="dynamic">
                    <mat-label>{{ kind() === 'Artists' ? 'Artist' : 'Series' }}</mat-label>
                    <mat-select [value]="r.pick" (selectionChange)="choosePick(r, $event.value)" [disabled]="busy()"
                                [attr.data-testid]="'folder-match-pick-' + r.row.nodeId">
                      <mat-option [value]="-1">Skip this folder</mat-option>
                      @for (c of choices(r); track $index) { <mat-option [value]="$index">{{ c }}</mat-option> }
                    </mat-select>
                  </mat-form-field>
                } @else if (choices(r).length === 1) {
                  <div class="choice">{{ choices(r)[0] }}</div>
                }
                @if (kind() === 'Artists' && r.row.status === 'NoMatch') {
                  <mat-checkbox [checked]="r.ownName" [disabled]="busy()" (change)="setOwnName(r, $event.checked)"
                                [attr.data-testid]="'folder-match-own-' + r.row.nodeId">Mark it with the folder's own name</mat-checkbox>
                }
                @if (web() && webEligible(r)) {
                  <div class="web-row">
                    <mat-checkbox [checked]="r.webTicked" [disabled]="busy()" (change)="patch(r, { webTicked: $event.checked })"
                                  aria-label="Search the web for this folder" [attr.data-testid]="'folder-match-web-tick-' + r.row.nodeId" />
                    <mat-form-field appearance="outline" class="web-text" subscriptSizing="dynamic">
                      <mat-label>Search text sent to MangaUpdates</mat-label>
                      <input matInput [ngModel]="r.webText" (ngModelChange)="patch(r, { webText: $event })" [disabled]="busy()"
                             maxlength="200" [attr.data-testid]="'folder-match-web-text-' + r.row.nodeId">
                    </mat-form-field>
                  </div>
                  @if (r.webState === 'running') { <div class="small">Searching…</div> }
                  @if (r.webState === 'failed') { <div class="error small">{{ r.webError }}</div> }
                  @if (r.webState === 'done') {
                    @if (r.webCandidates.length) {
                      <mat-form-field appearance="outline" class="pick" subscriptSizing="dynamic">
                        <mat-label>Series from the web ({{ r.webCandidates.length }})</mat-label>
                        <mat-select [value]="r.webPick" (selectionChange)="chooseWeb(r, $event.value)" [disabled]="busy()"
                                    [attr.data-testid]="'folder-match-web-pick-' + r.row.nodeId">
                          <mat-option [value]="-1">None of these</mat-option>
                          @for (c of r.webCandidates; track c.externalId; let i = $index) {
                            <mat-option [value]="i">{{ webLabel(c) }}</mat-option>
                          }
                        </mat-select>
                      </mat-form-field>
                    } @else { <div class="small">Nothing found</div> }
                  }
                }
                @if (r.result) {
                  <div class="result" [class.error]="r.result !== 'ok'" [attr.data-testid]="'folder-match-result-' + r.row.nodeId">
                    {{ result(r) }}
                  </div>
                }
              </div>
            </li>
          }
        </ul>

        @if (kind() === 'Collections') {
          <mat-checkbox [checked]="doujinContent()" [disabled]="busy()" (change)="doujinContent.set($event.checked)"
                        data-testid="folder-match-doujin">Also set each folder's Content to "Doujinshi &amp; adult one-shots"</mat-checkbox>
          @if (webEligibleCount() > 0) {
            <section class="web" data-testid="folder-match-web-section">
              <mat-checkbox [checked]="web()" [disabled]="busy()" (change)="setWeb($event.checked)"
                            data-testid="folder-match-web">Search the web for the rest</mat-checkbox>
              <p class="small">
                Sends the search text of each ticked folder above - nothing else - to MangaUpdates, as the Identify dialog does: one
                folder per second, at most {{ maxWeb }} per run, counted in the daily budget. The results are only offered: pick a series
                to mark a folder (its record is then fetched).
              </p>
              @if (web()) {
                <div class="web-actions">
                  <mat-checkbox [checked]="hideDoujinshi()" [disabled]="busy()" (change)="hideDoujinshi.set($event.checked)"
                                data-testid="folder-match-hide">Hide doujinshi &amp; novels</mat-checkbox>
                  @if (searching()) {
                    <button mat-stroked-button type="button" (click)="stop()" data-testid="folder-match-web-stop">Stop</button>
                  } @else {
                    <button mat-stroked-button type="button" [disabled]="webQueue().length === 0 || applying()" (click)="search()"
                            data-testid="folder-match-web-search">Search {{ webQueueLabel() }}</button>
                  }
                </div>
                @if (webNote(); as n) { <p class="small" role="status" data-testid="folder-match-web-note">{{ n }}</p> }
              }
            </section>
          }
        }
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" (click)="close()" [disabled]="applying()" data-testid="folder-match-close">{{ applied() ? 'Close' : 'Cancel' }}</button>
      @if (!applied()) {
        <button mat-flat-button type="button" [disabled]="applyCount() === 0 || busy()" (click)="apply()"
                data-testid="folder-match-apply">Mark {{ applyLabel() }}</button>
      }
    </mat-dialog-actions>
  `,
  styles: [`
    .content { display: block; }
    .lead { margin: 0 0 8px; overflow-wrap: anywhere; }
    .note { color: #a8a8b8; }
    .kind { margin: 0 0 8px; max-width: 100%; flex-wrap: wrap; }
    .small { margin: 4px 0 8px; font-size: 13px; line-height: 1.45; color: #a8a8b8; }
    .error { color: #ef9a9a; }
    .summary { margin: 8px 0 4px; font-weight: 500; }
    .rows { list-style: none; margin: 0; padding: 0; }
    .row {
      display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 0 4px; align-items: start;
      padding: 6px 0; border-top: 1px solid rgba(255, 255, 255, 0.08);
    }
    .row.muted .name { color: #8a8a99; }
    .tick { margin-top: -6px; }
    .body { min-width: 0; }
    .name { font-weight: 500; overflow-wrap: anywhere; }
    .status { font-size: 13px; color: #a8a8b8; overflow-wrap: anywhere; }
    .status.warn { color: #ffcc80; }
    .choice { font-size: 14px; overflow-wrap: anywhere; }
    .pick { width: 100%; margin-top: 6px; }
    .web-row { display: flex; gap: 4px; align-items: center; margin-top: 6px; }
    .web-text { flex: 1 1 auto; min-width: 0; }
    .result { margin-top: 4px; font-size: 13px; color: #a5d6a7; }
    .result.error { color: #ef9a9a; }
    .web { margin-top: 12px; padding-top: 8px; border-top: 1px solid rgba(255, 255, 255, 0.12); }
    .web-actions { display: flex; flex-wrap: wrap; gap: 8px 16px; align-items: center; }
  `],
})
export class FolderMatchDialogComponent implements OnDestroy {
  readonly data = inject<FolderMatchDialogData>(MAT_DIALOG_DATA);
  private readonly ref = inject<MatDialogRef<FolderMatchDialogComponent, FolderMatchDialogResult | undefined>>(MatDialogRef);
  private readonly api = inject(FolderMatchApiService);
  private readonly metadata = inject(MetadataApiService);

  /** Pause between two web searches (tests set 0). */
  paceMs = WEB_SEARCH_PACE_MS;
  readonly maxWeb = MAX_WEB_SEARCHES;

  readonly kind = signal<FolderMatchKind | null>(null);
  readonly rows = signal<FolderMatchRowState[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly doujinContent = signal(true);
  readonly web = signal(false);
  readonly hideDoujinshi = signal(true);
  readonly searching = signal(false);
  readonly applying = signal(false);
  readonly applied = signal(false);
  readonly webNote = signal<string | null>(null);
  readonly busy = computed(() => this.loading() || this.searching() || this.applying());
  private stopped = false;
  private destroyed = false;
  private result: FolderMatchDialogResult | undefined;

  readonly folderCount = plural(this.data.folders.length, 'folder selected', 'folders selected');
  readonly ignoredText = plural(this.data.ignored, 'archive', 'archives');

  readonly summary = computed(() => {
    const rows = this.rows();
    const count = (s: string) => rows.filter((r) => r.row.status === s).length;
    const parts = [
      `${count('Proposed')} matched`,
      `${count('Ambiguous')} with several matches`,
      `${count('NoMatch')} without a match`,
    ];
    if (count('Decided')) parts.push(`${count('Decided')} already decided`);
    return parts.join(', ');
  });

  readonly webEligibleCount = computed(() => this.rows().filter((r) => this.webEligible(r)).length);
  /** The ticked folders not searched yet - none unless "Search the web for the rest" is ticked. */
  readonly webQueue = computed(() => (this.web() ? this.rows() : []).filter((r) => this.webEligible(r) && r.webTicked
    && r.webText.trim().length > 0 && (r.webState === 'idle' || r.webState === 'waiting')));
  readonly webQueueLabel = computed(() => {
    const n = this.webQueue().length;
    return n > MAX_WEB_SEARCHES ? `the first ${MAX_WEB_SEARCHES} of ${n}` : plural(n, 'folder', 'folders');
  });

  readonly applyCount = computed(() => this.localItems().length + this.webPicks().length);
  readonly applyLabel = computed(() => plural(this.applyCount(), 'folder', 'folders'));

  ngOnDestroy(): void {
    this.destroyed = true;
    this.stopped = true;
  }

  choose(kind: FolderMatchKind): void {
    if (!kind || kind === this.kind()) return;
    this.kind.set(kind);
    this.rows.set([]);
    this.web.set(false);
    this.webNote.set(null);
    this.error.set(null);
    this.loading.set(true);
    this.api.preview({ kind, nodeIds: this.data.folders.map((f) => f.id) }).subscribe({
      next: (preview) => {
        if (this.kind() !== preview.kind) return;
        this.loading.set(false);
        this.rows.set(preview.rows.map((row) => initialState(row)));
      },
      error: (err: { message?: string }) => {
        this.loading.set(false);
        this.error.set(`The preview failed: ${err?.message ?? 'error'}`);
      },
    });
  }

  status(r: FolderMatchRowState): string {
    return statusText(r.row, this.kind() ?? 'Artists');
  }

  choices(r: FolderMatchRowState): string[] {
    return this.kind() === 'Artists'
      ? (r.row.artists ?? []).map(artistChoiceLabel)
      : (r.row.records ?? []).map(recordChoiceLabel);
  }

  webLabel(c: IdentifyCandidateDto): string {
    const details = [c.year ? String(c.year) : null, c.providerType ?? null].filter((x): x is string => !!x);
    return `${c.title}${details.length ? ' - ' + details.join(', ') : ''}`;
  }

  result(r: FolderMatchRowState): string {
    return resultText(r.result ?? '', r.resultMessage);
  }

  /** A row can be ticked when it is a folder with something to mark: a chosen match, or (artists) the folder's own name. */
  tickable(r: FolderMatchRowState): boolean {
    if (r.result === 'ok') return false;
    if (r.row.status === 'NotAFolder' || r.row.status === 'NotFound') return false;
    if (r.row.status === 'NoMatch') return this.kind() === 'Artists';
    return this.choices(r).length > 0;
  }

  webEligible(r: FolderMatchRowState): boolean {
    return this.kind() === 'Collections' && r.row.status === 'NoMatch' && !!r.row.searchText;
  }

  patch(r: FolderMatchRowState, change: Partial<FolderMatchRowState>): void {
    this.rows.update((rows) => rows.map((x) => (x.row.nodeId === r.row.nodeId ? { ...x, ...change } : x)));
  }

  private patchId(nodeId: string, change: Partial<FolderMatchRowState>): void {
    this.rows.update((rows) => rows.map((x) => (x.row.nodeId === nodeId ? { ...x, ...change } : x)));
  }

  tick(r: FolderMatchRowState, on: boolean): void {
    if (r.row.status === 'NoMatch') {
      this.setOwnName(r, on);
      return;
    }
    // Ticking a row without a chosen match chooses the first one (the list is in order: linked series / main names first).
    this.patch(r, { ticked: on, pick: on && r.pick < 0 ? 0 : r.pick });
  }

  choosePick(r: FolderMatchRowState, pick: number): void {
    this.patch(r, { pick, ticked: pick >= 0 });
  }

  setOwnName(r: FolderMatchRowState, on: boolean): void {
    this.patch(r, { ownName: on, ticked: on });
  }

  chooseWeb(r: FolderMatchRowState, pick: number): void {
    this.patch(r, { webPick: pick });
  }

  setWeb(on: boolean): void {
    this.web.set(on);
    this.webNote.set(null);
  }

  stop(): void {
    this.stopped = true;
  }

  /** The web searches: the ticked rows, one at a time, at least one second apart, at most 50; stops on backoff / budget / busy. */
  async search(): Promise<void> {
    const queue = this.webQueue().slice(0, MAX_WEB_SEARCHES);
    if (queue.length === 0 || this.busy()) return;
    this.stopped = false;
    this.searching.set(true);
    this.webNote.set(null);
    queue.forEach((r) => this.patchId(r.row.nodeId, { webState: 'waiting' }));
    let sent = 0;
    let stopNote: string | null = null;
    for (const r of queue) {
      if (this.stopped) break;
      if (sent > 0) await delay(this.paceMs);
      if (this.stopped) break;
      const current = this.rows().find((x) => x.row.nodeId === r.row.nodeId);
      if (!current) continue;
      this.patchId(r.row.nodeId, { webState: 'running' });
      sent++;
      try {
        const page = await firstValueFrom(
          this.metadata.search(r.row.nodeId, current.webText.trim(), 1, this.hideDoujinshi(), 'mangaupdates'));
        this.patchId(r.row.nodeId, { webState: 'done', webCandidates: page.candidates.slice(0, MAX_WEB_CANDIDATES), webPick: -1 });
      } catch (e) {
        const err = e as { status?: number; message?: string };
        this.patchId(r.row.nodeId, { webState: 'failed', webError: err?.message ?? 'The search failed.' });
        if (stopsTheRun(err?.status)) {
          stopNote = `Stopped: ${err?.message ?? 'the search failed'}`;
          break;
        }
      }
    }
    if (this.destroyed) return;
    // Rows that were not reached go back to the queue.
    this.rows.update((rows) => rows.map((x) => (x.webState === 'waiting' ? { ...x, webState: 'idle' } : x)));
    this.searching.set(false);
    const left = this.webQueue().length;
    this.webNote.set(stopNote ?? (this.stopped
      ? `Stopped after ${plural(sent, 'search', 'searches')}.`
      : `${plural(sent, 'search', 'searches')} sent.${left ? ` ${left} left: search again for the rest.` : ''}`));
  }

  /** The local items to mark, from the ticked rows. */
  private localItems(): FolderMatchApplyItem[] {
    const kind = this.kind();
    const items: FolderMatchApplyItem[] = [];
    for (const r of this.rows()) {
      if (!r.ticked || r.result === 'ok') continue;
      if (kind === 'Artists') {
        const artist = (r.row.artists ?? [])[r.pick];
        if (artist) items.push({ nodeId: r.row.nodeId, name: artist.name, role: artist.role });
        else if (r.row.status === 'NoMatch' && r.ownName) items.push({ nodeId: r.row.nodeId });
      } else if (kind === 'Collections') {
        const record = (r.row.records ?? [])[r.pick];
        if (record) items.push({ nodeId: r.row.nodeId, provider: record.provider, externalId: record.externalId });
      }
    }
    return items;
  }

  private webPicks(): { nodeId: string; candidate: IdentifyCandidateDto }[] {
    if (this.kind() !== 'Collections') return [];
    return this.rows()
      .filter((r) => r.webState === 'done' && r.webPick >= 0 && r.result !== 'ok' && this.webEligible(r))
      .map((r) => ({ nodeId: r.row.nodeId, candidate: r.webCandidates[r.webPick] }))
      .filter((x) => !!x.candidate);
  }

  /** Marks the ticked folders: the local matches in one call (stored data only), then each web pick through Collection about. */
  async apply(): Promise<void> {
    const kind = this.kind();
    if (!kind || this.busy()) return;
    const local = this.localItems();
    const web = this.webPicks();
    if (local.length + web.length === 0) return;
    this.applying.set(true);
    this.error.set(null);
    const marked: string[] = [];
    let failed = 0;
    let queued = 0;
    try {
      if (local.length) {
        const res = await firstValueFrom(this.api.apply({ kind, items: local, setDoujinContent: this.doujinContent() }));
        for (const item of res.results) {
          this.patchId(item.nodeId, { result: item.code, ticked: false });
          if (item.code === 'ok') {
            marked.push(item.nodeId);
            queued += item.queued ?? 0;
          } else failed++;
        }
      }
      for (const w of web) {
        if (this.destroyed) return;
        try {
          const res = await firstValueFrom(this.metadata.setCollection(w.nodeId, {
            provider: 'mangaupdates', externalId: w.candidate.externalId, matchMethod: 'Search', setDoujinContent: this.doujinContent(),
          }));
          this.patchId(w.nodeId, { result: 'ok' });
          marked.push(w.nodeId);
          queued += res.queued ?? 0;
        } catch (e) {
          const err = e as { error?: string; message?: string };
          this.patchId(w.nodeId, { result: err?.error ?? 'error', resultMessage: err?.message });
          failed++;
        }
      }
      this.applied.set(true);
    } catch (e) {
      this.error.set(`Marking failed: ${(e as { message?: string })?.message ?? 'error'}`);
    } finally {
      this.applying.set(false);
      if (marked.length || failed) this.result = { kind, marked, failed, queued };
    }
  }

  close(): void {
    this.stopped = true;
    this.ref.close(this.result);
  }
}

function initialState(row: FolderMatchRowDto): FolderMatchRowState {
  return {
    row,
    ticked: row.status === 'Proposed',
    pick: row.status === 'Proposed' || row.status === 'Decided' ? 0 : -1,
    ownName: false,
    webTicked: true,
    webText: row.searchText ?? '',
    webState: 'idle',
    webCandidates: [],
    webPick: -1,
  };
}

function delay(ms: number): Promise<void> {
  return ms > 0 ? new Promise((resolve) => setTimeout(resolve, ms)) : Promise.resolve();
}
