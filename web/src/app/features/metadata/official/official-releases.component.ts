import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';

import { OfficialReleaseRowDto, OfficialReleasesSummaryDto, CompletionBasis, SeriesAnswer } from '../../../core/api/api-types';
import { ReviewLibraryOption } from '../review/review-dashboard.component';
import { ANSWER_FILTERS, answerSentence, languageName, reachSentence, trackersLine, upgradeSentence } from '../progress/series-progress-labels';
import { OfficialReleasesApiService } from './official-releases-api.service';
import { SeriesAnswerChipComponent } from './series-answer-chip.component';

/** A filter of the tab: one answer, or every linked series. */
export type CompletionFilter = SeriesAnswer | 'All';

/** The filter toggle, in the order shown (1.32.0): the five answers, then All. */
export const COMPLETION_FILTERS: readonly { value: CompletionFilter; label: string }[] = [...ANSWER_FILTERS, { value: 'All', label: 'All' }];

/** The edition select of the two Finished filters (the 1.30.0 basis toggle). '' = any. */
export const COMPLETION_EDITIONS: readonly { value: CompletionBasis | ''; label: string }[] = [
  { value: '', label: 'Any' },
  { value: 'OfficialVolumes', label: 'Official' },
  { value: 'OfficialChapters', label: 'Official chapters' },
  { value: 'AllChapters', label: 'Chapter-based' },
  { value: 'OriginRun', label: 'Original run' },
];

const SUMMARY_KEYS: Record<SeriesAnswer, keyof OfficialReleasesSummaryDto> = {
  HaveItAll: 'haveItAll', FinishedMissing: 'finishedMissing', UpToDate: 'upToDate', MissingSome: 'missingSome', CantTell: 'cantTell',
};

/** How many series a filter holds (from the summary); All = every answer. */
export function filterCount(summary: OfficialReleasesSummaryDto | null, filter: CompletionFilter): number | null {
  if (!summary) return null;
  if (filter === 'All') return ANSWER_FILTERS.reduce((n, f) => n + ((summary[SUMMARY_KEYS[f.value]] as number | undefined) ?? 0), 0);
  return (summary[SUMMARY_KEYS[filter]] as number | undefined) ?? 0;
}

/** The empty-state sentence of a filter; a language without official volume totals says why. */
export function completionEmptyText(filter: CompletionFilter, language: string, upgradesOnly = false): string {
  const name = languageName(language) || language;
  if (upgradesOnly) return `No linked series here has an official ${name} volume that you hold only as chapters.`;
  const englishOnly = language.toLowerCase() !== 'en'
    ? ` Official volume totals are known only for English (MangaUpdates' English publishers); your preferred language is ${name}.`
    : '';
  switch (filter) {
    case 'HaveItAll': return `No linked series here has ended with all of it in your folder.${englishOnly}`;
    case 'FinishedMissing': return `No linked series here has ended with some of it missing.${englishOnly}`;
    case 'UpToDate': return `No series here is still coming out with everything released so far in your folder.${englishOnly}`;
    case 'MissingSome': return `No series here that is still coming out misses something released in ${name}.${englishOnly}`;
    case 'CantTell': return 'MangaPixer can compare every linked series here.';
    default: return 'No linked series folders yet.';
  }
}

/**
 * The Completion tab of `/admin/metadata` (1.32.0; the Official releases tab of 1.30.0): has each linked series ended, and does its
 * folder hold all of it - one answer per series, the edition of the finished ones, and "Upgrade available" as a separate switch. Use
 * it to find series to move to a library of finished series; MangaPixer never moves folders itself. Built from stored data only:
 * opening the tab never contacts a provider. Default filter: Have it all.
 */
@Component({
  selector: 'app-official-releases',
  standalone: true,
  imports: [
    RouterLink, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatIconModule, MatProgressSpinnerModule, MatSelectModule,
    MatSlideToggleModule, SeriesAnswerChipComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="official" data-testid="official-releases">
      <p class="intro">Has each linked series ended, and do you have all of it? Use <strong>Have it all</strong> to find series you can
        move to a library of finished series; MangaPixer never moves folders itself. Built from stored data; nothing is fetched. Answers
        update when records refresh.</p>
      <div class="toolbar">
        <div class="filters">
          <mat-button-toggle-group [value]="filter()" (change)="setFilter($event.value)" aria-label="Which series" hideSingleSelectionIndicator>
            @for (f of filters; track f.value) {
              <mat-button-toggle [value]="f.value" [attr.data-testid]="'official-filter-' + f.value">
                {{ f.label }}@if (count(f.value) !== null) { <span class="count">{{ count(f.value) }}</span> }
              </mat-button-toggle>
            }
          </mat-button-toggle-group>
        </div>
      </div>
      <div class="toolbar second">
        <mat-slide-toggle [checked]="upgradesOnly()" (change)="setUpgrades($event.checked)" data-testid="official-upgrades-only">
          Upgrades only
        </mat-slide-toggle>
        @if (editionApplies()) {
          <mat-form-field appearance="outline" subscriptSizing="dynamic" class="edition-filter">
            <mat-label>Edition</mat-label>
            <mat-select [value]="basis()" (selectionChange)="setBasis($event.value)" data-testid="official-edition">
              @for (b of editions; track b.value) { <mat-option [value]="b.value">{{ b.label }}</mat-option> }
            </mat-select>
          </mat-form-field>
        }
        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="lib-filter">
          <mat-label>Library</mat-label>
          <mat-select [value]="library() ?? ''" (selectionChange)="setLibrary($event.value || null)">
            <mat-option value="">All libraries</mat-option>
            @for (l of libraries(); track l.id) { <mat-option [value]="l.id">{{ l.name }}</mat-option> }
          </mat-select>
        </mat-form-field>
      </div>

      @if (summary(); as s) {
        <p class="summary" data-testid="official-summary">
          {{ s.series }} linked series@if (s.upgrades > 0) { · <span class="up">{{ s.upgrades }} with an upgrade available</span> }
        </p>
      }

      @if (loading()) {
        <div class="state"><mat-spinner diameter="28" /></div>
      } @else if (error()) {
        <p class="state error" role="alert">{{ error() }}</p>
      } @else {
        <div class="list">
          @for (row of items(); track row.nodeId) {
            <article class="row" [class]="'row ' + (row.progress.answer ?? '')" data-testid="official-row">
              @if (row.coverUrl) {
                <img class="cover" [src]="row.coverUrl" alt="" loading="lazy" />
              } @else {
                <div class="cover none"><mat-icon>folder</mat-icon></div>
              }
              <div class="body">
                <header>
                  <a class="name" [routerLink]="['/series', row.nodeId]" data-testid="official-series-link">{{ row.displayName }}</a>
                  <app-series-answer-chip [progress]="row.progress" />
                  @if (row.linkState === 'Auto') { <span class="auto">automatic link</span> }
                </header>
                <p class="meta">{{ row.recordTitle }} · {{ row.libraryName }}</p>
                @if (answerSentence(row.progress); as a) { <p class="line answer-line" data-testid="official-answer">{{ a }}</p> }
                @if (trackersLine(row.progress); as t) { <p class="line trackers" data-testid="official-trackers">{{ t }}</p> }
                @if (reachSentence(row.progress); as f) { <p class="line" data-testid="official-folder">{{ f }}</p> }
                @if (upgradeSentence(row.progress); as u) { <p class="line upgrade-line" data-testid="official-upgrade">{{ u }}</p> }
              </div>
              <a mat-icon-button class="open" [routerLink]="['/libraries', row.libraryId, 'browse', row.nodeId]" aria-label="Browse folder">
                <mat-icon>folder_open</mat-icon>
              </a>
            </article>
          } @empty {
            <div class="empty" data-testid="official-empty">
              <mat-icon>fact_check</mat-icon>
              <p>{{ emptyText() }}</p>
            </div>
          }
        </div>
        @if (cursor()) {
          <div class="more"><button mat-stroked-button type="button" (click)="load(true)" data-testid="official-more">Load more</button></div>
        }
      }
    </div>
  `,
  styles: [`
    :host { display: block; }
    .intro { margin: 0 0 12px; color: #9a9aa8; font-size: 13px; }
    .intro strong { color: #c8c8d4; font-weight: 500; }
    .toolbar { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; margin-bottom: 8px; }
    .toolbar.second { justify-content: flex-start; }
    /* The toggles scroll sideways inside their own box on a phone rather than widening the page. */
    .filters { max-width: 100%; overflow-x: auto; }
    .count { margin-left: 4px; color: #9a9aa8; font-size: 12px; }
    .lib-filter { width: 220px; }
    .edition-filter { width: 180px; }
    .summary { margin: 4px 0 12px; font-size: 13px; color: #c8c8d4; }
    .summary .up { color: #b39dff; }
    .list { display: flex; flex-direction: column; gap: 8px; }
    .row { display: flex; gap: 12px; align-items: flex-start; padding: 10px 12px; border-radius: 10px; background: #1c1c26;
      border: 1px solid rgba(255, 255, 255, 0.06); border-left: 3px solid #555; }
    .row.HaveItAll { border-left-color: #81c784; }
    .row.FinishedMissing { border-left-color: #ffb74d; }
    .row.UpToDate { border-left-color: #64b5f6; }
    .row.MissingSome { border-left-color: #ef5350; }
    .cover { width: 48px; height: 68px; object-fit: cover; border-radius: 4px; flex: none; background: #2a2a36; }
    .cover.none { display: flex; align-items: center; justify-content: center; color: #6a6a78; }
    .body { flex: 1; min-width: 0; }
    header { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; }
    .name { font-weight: 500; font-size: 15px; color: inherit; text-decoration: none; overflow-wrap: anywhere; }
    .name:hover { text-decoration: underline; }
    .auto { color: #ffcc80; font-size: 12px; }
    .meta { margin: 2px 0 4px; font-size: 12px; color: #9a9aa8; overflow-wrap: anywhere; }
    .line { margin: 2px 0; font-size: 13px; overflow-wrap: anywhere; }
    .answer-line { color: #e0e0ea; }
    .line.trackers { color: #9a9aa8; font-size: 12px; }
    .upgrade-line { color: #b39dff; }
    .open { flex: none; color: #b39dff; }
    .state { display: flex; justify-content: center; padding: 32px 0; }
    .error { color: #ff8a80; }
    .empty { display: flex; flex-direction: column; align-items: center; padding: 32px 0; color: #8a8a99; text-align: center; }
    .empty p { max-width: 560px; }
    .more { display: flex; justify-content: center; margin-top: 12px; }
    @media (max-width: 599.98px) {
      .lib-filter, .edition-filter { width: 100%; }
      .cover { width: 40px; height: 56px; }
      .open { display: none; }
    }
  `],
})
export class OfficialReleasesComponent implements OnInit {
  private readonly api = inject(OfficialReleasesApiService);

  readonly libraries = input<ReviewLibraryOption[]>([]);
  readonly initialLibrary = input<string | null>(null);

  readonly filters = COMPLETION_FILTERS;
  readonly filter = signal<CompletionFilter>('HaveItAll');
  readonly editions = COMPLETION_EDITIONS;
  readonly basis = signal<CompletionBasis | ''>('');
  readonly upgradesOnly = signal(false);
  readonly library = signal<string | null>(null);
  readonly items = signal<OfficialReleaseRowDto[]>([]);
  readonly summary = signal<OfficialReleasesSummaryDto | null>(null);
  readonly language = signal('en');
  readonly cursor = signal<string | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  /** The edition applies to the two Finished answers only. */
  readonly editionApplies = computed(() => this.filter() === 'HaveItAll' || this.filter() === 'FinishedMissing');
  readonly emptyText = computed(() => {
    const basis = this.editionApplies() ? this.basis() : '';
    return basis ? `Nothing here is finished by the ${COMPLETION_EDITIONS.find((b) => b.value === basis)!.label.toLowerCase()} edition.`
      : completionEmptyText(this.filter(), this.language(), this.upgradesOnly());
  });

  readonly trackersLine = trackersLine;
  readonly reachSentence = reachSentence;
  readonly upgradeSentence = upgradeSentence;
  readonly answerSentence = answerSentence;

  count(filter: CompletionFilter): number | null {
    return filterCount(this.summary(), filter);
  }

  ngOnInit(): void {
    this.library.set(this.initialLibrary());
    this.load(false);
  }

  setFilter(filter: CompletionFilter): void {
    this.filter.set(filter);
    this.load(false);
  }

  setBasis(basis: CompletionBasis | ''): void {
    this.basis.set(basis);
    this.load(false);
  }

  setUpgrades(on: boolean): void {
    this.upgradesOnly.set(on);
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
    const filter = this.filter();
    const basis = this.editionApplies() ? this.basis() || null : null;
    const answer = filter === 'All' ? null : filter;
    this.api.list(this.library(), 'All', more ? this.cursor() : null, 50, basis, answer, this.upgradesOnly()).subscribe({
      next: (page) => {
        this.items.set(more ? [...this.items(), ...page.items] : page.items);
        this.summary.set(page.summary);
        this.language.set(page.language);
        this.cursor.set(page.nextCursor ?? null);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('The list could not be loaded.');
        this.loading.set(false);
      },
    });
  }
}
