import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';

import { ApiError, MissingReportSummaryDto, MissingSeriesDto } from '../../../core/api/api-types';
import { DUPLICATE_TIP, duplicateCountLabel, duplicateListText, duplicateTotals } from '../../../shared/duplicate-units';
import { MetadataReviewStateService } from '../metadata-review-state.service';
import { ReviewLibraryOption } from '../review/review-dashboard.component';
import { MissingReportApiService } from './missing-report-api.service';
import { reachSentence, upgradeText } from '../progress/series-progress-labels';
import { CompletionMarkComponent } from '../official/completion-mark.component';
import { ListCreditComponent } from '../../../shared/list-credit.component';
import {
  MISSING_CONFIDENCE_LABELS, MISSING_VERDICT_LABELS, batchSentence, conversionLine, gapDetail, gapsOf, haveSentence, noVerdictReason, totalTooltip,
} from './missing-labels';

type Filter = 'missing' | 'all';

/**
 * Missing tab of `/admin/metadata` (1.28.0; 1.30.0: volume files and chapter files merged through the stored volume list - the
 * same engine as the Volumes view - and an official volume held as chapters is an upgrade, never behind): every folder linked to a series, its highest volume / chapter
 * number on disk against the total its stored record states (English publisher first, then the country of
 * origin, then the latest chapter), holes in the local numbering, and where the total came from. Built from
 * stored data only - opening the tab never contacts a provider. Default filter: behind or with gaps.
 */
@Component({
  selector: 'app-missing-report',
  standalone: true,
  imports: [
    RouterLink, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatIconModule, MatProgressSpinnerModule, MatSelectModule,
    MatTooltipModule, CompletionMarkComponent, ListCreditComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="missing" data-testid="missing-report">
      <p class="intro">Compares the volumes and chapters in your folders - volume files and chapter files merged through each linked
        series' stored volume list - with what its stored record says is released in your language. Nothing is fetched to build
        this list; totals update when records refresh.</p>
      <div class="toolbar">
        <mat-button-toggle-group [value]="filter()" (change)="setFilter($event.value)" aria-label="Which series" hideSingleSelectionIndicator>
          <mat-button-toggle value="missing" data-testid="missing-filter-missing">Behind or with gaps</mat-button-toggle>
          <mat-button-toggle value="all" data-testid="missing-filter-all">All linked series</mat-button-toggle>
        </mat-button-toggle-group>
        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="lib-filter">
          <mat-label>Library</mat-label>
          <mat-select [value]="library() ?? ''" (selectionChange)="setLibrary($event.value || null)">
            <mat-option value="">All libraries</mat-option>
            @for (l of libraries(); track l.id) { <mat-option [value]="l.id">{{ l.name }}</mat-option> }
          </mat-select>
        </mat-form-field>
      </div>
      <div class="convert-bar" data-testid="missing-convert-bar">
        @if (aniListAutomatic()) {
          <span class="muted small" data-testid="missing-convert-auto">Chapters per volume are looked up automatically: from MangaDex's
            volume list, or from AniList for a linked series MangaDex has none for.</span>
        } @else if (aniListReady()) {
          <button mat-stroked-button type="button" [disabled]="batchBusy()" (click)="lookupBatch()" data-testid="missing-convert-batch"
                  matTooltip="Asks AniList for up to 20 linked series that have no chapters-per-volume yet (one request each)">
            <mat-icon>swap_horiz</mat-icon> {{ batchBusy() ? 'Looking up…' : 'Get chapters per volume from AniList' }}
          </button>
        } @else if (aniListBlocked(); as why) {
          <span class="muted small" data-testid="missing-convert-off">Chapters per volume from AniList: {{ why }}</span>
        }
        @if (batchMessage()) { <span class="small" role="status" data-testid="missing-convert-result">{{ batchMessage() }}</span> }
      </div>

      @if (summary(); as s) {
        <p class="summary" data-testid="missing-summary">
          {{ s.series }} linked series · <span class="behind">{{ s.behind }} behind</span> · {{ s.holes }} with gaps ·
          {{ s.upToDate }} up to date @if (s.noTotal) { · {{ s.noTotal }} without a total } @if (s.noVerdict) { · {{ s.noVerdict }} not comparable }
          @if (s.notTracked) { · <span data-testid="missing-not-tracked">{{ s.notTracked }} not tracked</span> }
          @if (s.upgrades) {
            · <button type="button" class="link" (click)="openOfficial.emit()" data-testid="missing-upgrades-link">{{ s.upgrades }} with official volumes to get</button>
          }
        </p>
      }

      @if (loading()) {
        <div class="state"><mat-spinner diameter="28" /></div>
      } @else if (error()) {
        <p class="state error" role="alert">{{ error() }}</p>
      } @else {
        <div class="list">
          @for (row of items(); track row.nodeId) {
            <article class="row" [class]="'v-' + row.verdict" data-testid="missing-row">
              @if (row.coverUrl) {
                <img class="cover" [src]="row.coverUrl" alt="" loading="lazy" />
              } @else {
                <div class="cover none"><mat-icon>folder</mat-icon></div>
              }
              <div class="body">
                <header>
                  <a class="name" [routerLink]="['/series', row.nodeId]" data-testid="missing-series-link">{{ row.displayName }}</a>
                  <span class="verdict" data-testid="missing-verdict">{{ verdictLabels[row.verdict] }}</span>
                  <app-completion-mark [progress]="row.progress" />
                </header>
                <p class="meta">{{ row.recordTitle }} · {{ row.libraryName }}@if (row.linkState === 'Auto') { · <span class="auto">automatic link</span> }</p>
                @if (reachSentence(row.progress); as r) { <p class="reach" data-testid="missing-reach">{{ r }}</p> }
                <app-list-credit [credit]="row.progress?.listCredit" />
                @if (row.progress && upgradeText(row.progress); as u) { <p class="note upgrade" data-testid="missing-upgrade">{{ u }} (an upgrade, not missing)</p> }
                @for (gap of gapsOf(row); track gap.kind) {
                  <p class="gap" data-testid="missing-gap">
                    <span class="have">{{ haveSentence(gap) }}</span>
                    @if (gapDetail(gap); as d) { <span class="detail">{{ d }}</span> }
                    @if (gap.confidence; as c) {
                      <mat-icon class="conf" [class]="'c-' + c" [matTooltip]="totalTooltip(gap)"
                                [attr.aria-label]="totalTooltip(gap)">info</mat-icon>
                    }
                  </p>
                }
                @if (dupLabel(row); as label) {
                  <!-- 1.31.0: the same number in more than one file of one folder (a chapter uploaded twice). Not a hole, not "behind". -->
                  <p class="note dup" [matTooltip]="dupTip" data-testid="missing-duplicates">
                    <mat-icon inline>content_copy</mat-icon> {{ label }} ({{ dupList(row) }})
                  </p>
                }
                @if (row.conversion; as conv) {
                  <p class="note conv" data-testid="missing-conversion">
                    @if (conv.siteUrl) {
                      <a [href]="conv.siteUrl" target="_blank" rel="noopener noreferrer" referrerpolicy="no-referrer">{{ conversionLine(conv) }}</a>
                    } @else { {{ conversionLine(conv) }} }
                  </p>
                } @else if (aniListReady() && row.provider === 'mangaupdates') {
                  <button mat-button type="button" class="conv-btn" [disabled]="busyRow() === row.nodeId" (click)="lookupOne(row)"
                          data-testid="missing-convert-one">
                    <mat-icon>swap_horiz</mat-icon> Chapters per volume (AniList)
                  </button>
                }
                @if (rowMessage()[row.nodeId]; as m) { <p class="note" role="status">{{ m }}</p> }
                @if (noVerdictReason(row); as reason) { <p class="note">{{ reason }}</p> }
                @if (row.englishTotalUnknown) {
                  <p class="note" data-testid="missing-english-unknown">An English edition is listed; its total is read on the record's next refresh.</p>
                }
              </div>
              <a mat-icon-button class="open" [routerLink]="['/libraries', row.libraryId, 'browse', row.nodeId]" aria-label="Browse folder">
                <mat-icon>folder_open</mat-icon>
              </a>
            </article>
          } @empty {
            <div class="empty" data-testid="missing-empty">
              <mat-icon>library_add_check</mat-icon>
              <p>{{ filter() === 'missing' ? 'Nothing behind or with gaps among the linked series.' : 'No linked series folders yet.' }}</p>
            </div>
          }
        </div>
        @if (cursor()) {
          <div class="more"><button mat-stroked-button type="button" (click)="load(true)" data-testid="missing-more">Load more</button></div>
        }
      }
    </div>
  `,
  styles: [`
    .intro { margin: 0 0 12px; color: #9a9aa8; font-size: 13px; }
    .toolbar { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; justify-content: space-between; margin-bottom: 8px; }
    .lib-filter { width: 220px; }
    .summary { margin: 4px 0 12px; font-size: 13px; color: #c8c8d4; }
    .summary .behind { color: #ffb74d; }
    .link { background: none; border: 0; padding: 0; font: inherit; color: #b39dff; cursor: pointer; text-decoration: underline; }
    .reach { margin: 2px 0; font-size: 13px; overflow-wrap: anywhere; }
    .note.upgrade { color: #b39dff; }
    .note.dup { color: #ffcc80; overflow-wrap: anywhere; }
    .list { display: flex; flex-direction: column; gap: 8px; }
    .row { display: flex; gap: 12px; align-items: flex-start; padding: 10px 12px; border-radius: 10px; background: #1c1c26;
      border: 1px solid rgba(255, 255, 255, 0.06); border-left: 3px solid #555; }
    .row.v-Behind { border-left-color: #ffb74d; }
    .row.v-Holes { border-left-color: #ff8a80; }
    .row.v-UpToDate { border-left-color: #81c784; }
    .cover { width: 48px; height: 68px; object-fit: cover; border-radius: 4px; flex: none; background: #2a2a36; }
    .cover.none { display: flex; align-items: center; justify-content: center; color: #6a6a78; }
    .body { flex: 1; min-width: 0; }
    header { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
    .name { font-weight: 500; font-size: 15px; color: inherit; text-decoration: none; overflow-wrap: anywhere; }
    .name:hover { text-decoration: underline; }
    .verdict { padding: 0 8px; border-radius: 10px; background: rgba(255, 255, 255, 0.08); font-size: 12px; line-height: 20px; }
    .v-Behind .verdict { background: rgba(255, 183, 77, 0.18); color: #ffcc80; }
    .v-Holes .verdict { background: rgba(244, 67, 54, 0.18); color: #ff8a80; }
    .v-UpToDate .verdict { background: rgba(129, 199, 132, 0.16); color: #a5d6a7; }
    .meta { margin: 2px 0 4px; font-size: 12px; color: #9a9aa8; overflow-wrap: anywhere; }
    .auto { color: #ffcc80; }
    .gap { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 10px; margin: 2px 0; font-size: 13px; }
    .detail { color: #ffcc80; }
    .conf { font-size: 16px; width: 16px; height: 16px; color: #8a8a99; cursor: help; }
    .conf.c-Low { color: #ff8a80; }
    .note { margin: 2px 0; font-size: 12px; color: #9a9aa8; }
    .open { flex: none; color: #b39dff; }
    .convert-bar { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 12px; margin: 0 0 8px; }
    .small { font-size: 12px; }
    .muted { color: #9a9aa8; }
    .conv a { color: #b39dff; }
    .conv-btn { font-size: 12px; margin-left: -8px; }
    .state { display: flex; justify-content: center; padding: 32px 0; }
    .error { color: #ff8a80; }
    .empty { display: flex; flex-direction: column; align-items: center; padding: 32px 0; color: #8a8a99; text-align: center; }
    .more { display: flex; justify-content: center; margin-top: 12px; }
    @media (max-width: 599.98px) {
      .lib-filter { width: 100%; }
      .cover { width: 40px; height: 56px; }
      .open { display: none; }
    }
  `],
})
export class MissingReportComponent implements OnInit {
  private readonly api = inject(MissingReportApiService);
  private readonly state = inject(MetadataReviewStateService);

  readonly libraries = input<ReviewLibraryOption[]>([]);
  readonly initialLibrary = input<string | null>(null);
  /** 1.30.0: the summary's "with official volumes to get" opens the Official releases tab. */
  readonly openOfficial = output<void>();

  readonly filter = signal<Filter>('missing');
  readonly library = signal<string | null>(null);
  readonly items = signal<MissingSeriesDto[]>([]);
  readonly summary = signal<MissingReportSummaryDto | null>(null);
  readonly cursor = signal<string | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly batchBusy = signal(false);
  readonly batchMessage = signal<string | null>(null);
  readonly busyRow = signal<string | null>(null);
  readonly rowMessage = signal<Record<string, string>>({});

  /** Why an AniList lookup cannot run now (the gateway would refuse it the same way), or null when it can. */
  readonly aniListBlocked = computed(() => {
    const s = this.state.settings();
    if (!s) return 'loading settings…';
    if (s.networkDisabledByConfig) return 'disabled by the server configuration.';
    if (!s.fetchEnabled || s.consentRenewalNeeded || s.acceptedConsentVersion !== s.currentConsentVersion) {
      return 'turn on "Fetch from the web" in Settings first.';
    }
    if (!(s.providers ?? []).some((p) => p.id === 'anilist' && p.allowed)) return 'AniList is not an allowed site (Settings).';
    return null;
  });

  readonly aniListReady = computed(() => this.aniListBlocked() === null);

  /**
   * 1.29.0 RC: Automatic matching with "Volume covers from the web" already asks AniList in the background for a linked series
   * MangaDex has no volume list for, so the batch button is not offered then (it stays the way in without Automatic matching).
   */
  readonly aniListAutomatic = computed(() => {
    const s = this.state.settings();
    return !!s && this.aniListReady() && !!s.autoMatchEnabled && !s.autoConsentRenewalNeeded
      && s.acceptedAutoConsentVersion === s.currentAutoConsentVersion
      && s.volumeCoversEnabled !== false && !s.volumeCoversDisabledByConfig;
  });

  readonly verdictLabels = MISSING_VERDICT_LABELS;
  readonly confidenceLabels = MISSING_CONFIDENCE_LABELS;
  readonly dupTip = DUPLICATE_TIP;
  readonly haveSentence = haveSentence;
  readonly totalTooltip = totalTooltip;
  readonly gapDetail = gapDetail;
  readonly gapsOf = gapsOf;
  readonly noVerdictReason = noVerdictReason;
  readonly conversionLine = conversionLine;
  readonly reachSentence = reachSentence;
  readonly upgradeText = upgradeText;

  /** 1.31.0: "2 duplicate chapters" (or "N duplicate numbers" when the server capped the list); '' when none. */
  dupLabel(row: MissingSeriesDto): string {
    const list = row.duplicates ?? [];
    const total = row.duplicateCount ?? list.length;
    if (total === 0) return '';
    if (total > list.length) return `${total} duplicate numbers`;
    const t = duplicateTotals(list);
    return duplicateCountLabel(t.chapters, t.volumes);
  }

  dupList(row: MissingSeriesDto): string {
    return duplicateListText(row.duplicates, row.duplicateCount);
  }

  ngOnInit(): void {
    this.library.set(this.initialLibrary());
    if (!this.state.settings()) this.state.refreshSettings();
    this.load(false);
  }

  /** One AniList request for this row; the row is replaced by the server's recomputed one. */
  lookupOne(row: MissingSeriesDto): void {
    this.busyRow.set(row.nodeId);
    this.api.lookupConversion(row.nodeId).subscribe({
      next: (r) => {
        this.busyRow.set(null);
        this.items.update((list) => list.map((i) => (i.nodeId === row.nodeId ? r.row : i)));
        const text = r.outcome === 'Found' ? '' : r.outcome === 'NoCounts'
          ? 'AniList has this series, but no final volume and chapter totals yet.'
          : 'No AniList entry matched this series confidently; nothing was stored.';
        this.rowMessage.update((m) => ({ ...m, [row.nodeId]: text }));
      },
      error: (err: { error?: ApiError }) => {
        this.busyRow.set(null);
        this.rowMessage.update((m) => ({ ...m, [row.nodeId]: err?.error?.message || 'The lookup failed.' }));
      },
    });
  }

  lookupBatch(): void {
    this.batchBusy.set(true);
    this.batchMessage.set(null);
    this.api.lookupConversions(this.library()).subscribe({
      next: (r) => {
        this.batchBusy.set(false);
        this.batchMessage.set(batchSentence(r));
        this.load(false);
        this.state.refreshSettings();
      },
      error: () => {
        this.batchBusy.set(false);
        this.batchMessage.set('The lookup failed.');
      },
    });
  }

  setFilter(filter: Filter): void {
    this.filter.set(filter);
    this.load(false);
  }

  setLibrary(id: string | null): void {
    this.library.set(id);
    this.load(false);
  }

  load(more: boolean): void {
    if (!more) {
      this.loading.set(true);
      this.cursor.set(null);
    }
    this.error.set(null);
    this.api.list(this.library(), this.filter() === 'missing', more ? this.cursor() : null).subscribe({
      next: (page) => {
        this.items.set(more ? [...this.items(), ...page.items] : page.items);
        this.summary.set(page.summary);
        this.cursor.set(page.nextCursor ?? null);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('The report could not be loaded.');
        this.loading.set(false);
      },
    });
  }
}
