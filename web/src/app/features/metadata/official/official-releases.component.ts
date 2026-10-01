import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';

import { OfficialReleaseRowDto, OfficialReleasesFilter, OfficialReleasesSummaryDto, CompletionBasis } from '../../../core/api/api-types';
import { ReviewLibraryOption } from '../review/review-dashboard.component';
import { languageName, reachSentence, trackersLine, upgradeSentence } from '../progress/series-progress-labels';
import { CompletionMarkComponent } from './completion-mark.component';
import { OfficialReleasesApiService } from './official-releases-api.service';

/** The filter toggle, in the order shown. */
export const OFFICIAL_FILTERS: readonly { value: OfficialReleasesFilter; label: string }[] = [
  { value: 'ToAct', label: 'To act on' },
  { value: 'Upgrades', label: 'Upgrades' },
  { value: 'Finished', label: 'Finished, not complete' },
  { value: 'Complete', label: 'Complete collections' },
  { value: 'All', label: 'All' },
];

/** The basis toggle (owner, 1.30.0 RC): which release a finished / complete series is finished by. '' = any. */
export const OFFICIAL_BASES: readonly { value: CompletionBasis | ''; label: string }[] = [
  { value: '', label: 'Any' },
  { value: 'OfficialVolumes', label: 'Official' },
  { value: 'AllChapters', label: 'Fan translation' },
  { value: 'OriginRun', label: 'Original run' },
];

/** The empty-state sentence of a filter; a language without official volume totals says why. */
export function officialEmptyText(filter: OfficialReleasesFilter, language: string): string {
  const name = languageName(language) || language;
  const englishOnly = language.toLowerCase() !== 'en'
    ? ` Official volume totals are known only for English (MangaUpdates' English publishers); your preferred language is ${name}, so only finished series can appear here.`
    : '';
  switch (filter) {
    case 'Upgrades':
      return `No linked series has an official ${name} volume that you hold only as chapters.${englishOnly}`;
    case 'Finished':
      return `No finished series is missing from your folders.${englishOnly}`;
    case 'Complete':
      return 'No linked folder holds a finished series whole yet.';
    case 'All':
      return 'No linked series folders yet.';
    default:
      return `Nothing to act on: no official ${name} volume held only as chapters, and no finished series you do not hold whole.${englishOnly}`;
  }
}

/**
 * Official releases tab of `/admin/metadata` (1.30.0): volumes released officially in the preferred language that a folder holds
 * only as (scanlated) chapters - an upgrade, never "missing" - and finished series the folder does not hold whole, plus the
 * complete collections. Built from stored data only; opening the tab never contacts a provider. Default filter: to act on.
 */
@Component({
  selector: 'app-official-releases',
  standalone: true,
  imports: [
    RouterLink, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatIconModule, MatProgressSpinnerModule, MatSelectModule,
    CompletionMarkComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="official" data-testid="official-releases">
      <p class="intro">Volumes released in {{ languageLabel() }} that you hold only as chapters, and finished series. Built from stored
        data; nothing is fetched. Totals update when records refresh.</p>
      <div class="toolbar">
        <div class="filters">
          <mat-button-toggle-group [value]="filter()" (change)="setFilter($event.value)" aria-label="Which series" hideSingleSelectionIndicator>
            @for (f of filters; track f.value) {
              <mat-button-toggle [value]="f.value" [attr.data-testid]="'official-filter-' + f.value">{{ f.label }}</mat-button-toggle>
            }
          </mat-button-toggle-group>
        </div>
        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="lib-filter">
          <mat-label>Library</mat-label>
          <mat-select [value]="library() ?? ''" (selectionChange)="setLibrary($event.value || null)">
            <mat-option value="">All libraries</mat-option>
            @for (l of libraries(); track l.id) { <mat-option [value]="l.id">{{ l.name }}</mat-option> }
          </mat-select>
        </mat-form-field>
      </div>
      @if (filter() !== 'Upgrades') {
        <div class="filters bases">
          <mat-button-toggle-group [value]="basis()" (change)="setBasis($event.value)" aria-label="Finished by" hideSingleSelectionIndicator>
            @for (b of bases; track b.value) {
              <mat-button-toggle [value]="b.value" [attr.data-testid]="'official-basis-' + (b.value || 'Any')">{{ b.label }}</mat-button-toggle>
            }
          </mat-button-toggle-group>
        </div>
      }

      @if (summary(); as s) {
        <p class="summary" data-testid="official-summary">
          {{ s.series }} linked series · <span class="up">{{ s.upgrades }} with official volumes to get</span> ·
          {{ s.finishedNotHeld }} finished, not complete · {{ s.completeCollections }} complete collections
        </p>
      }

      @if (loading()) {
        <div class="state"><mat-spinner diameter="28" /></div>
      } @else if (error()) {
        <p class="state error" role="alert">{{ error() }}</p>
      } @else {
        <div class="list">
          @for (row of items(); track row.nodeId) {
            <article class="row" [class.upgrade]="row.progress.upgradeCount > 0" [class.complete]="row.progress.completion === 'CompleteCollection'"
                     data-testid="official-row">
              @if (row.coverUrl) {
                <img class="cover" [src]="row.coverUrl" alt="" loading="lazy" />
              } @else {
                <div class="cover none"><mat-icon>folder</mat-icon></div>
              }
              <div class="body">
                <header>
                  <a class="name" [routerLink]="['/series', row.nodeId]" data-testid="official-series-link">{{ row.displayName }}</a>
                  <app-completion-mark [progress]="row.progress" />
                </header>
                <p class="meta">{{ row.recordTitle }} · {{ row.libraryName }}@if (row.linkState === 'Auto') { · <span class="auto">automatic link</span> }</p>
                @if (upgradeSentence(row.progress); as u) { <p class="line upgrade-line" data-testid="official-upgrade">{{ u }}</p> }
                @if (trackersLine(row.progress); as t) { <p class="line trackers" data-testid="official-trackers">{{ t }}</p> }
                @if (reachSentence(row.progress); as f) { <p class="line" data-testid="official-folder">{{ f }}</p> }
              </div>
              <a mat-icon-button class="open" [routerLink]="['/libraries', row.libraryId, 'browse', row.nodeId]" aria-label="Browse folder">
                <mat-icon>folder_open</mat-icon>
              </a>
            </article>
          } @empty {
            <div class="empty" data-testid="official-empty">
              <mat-icon>new_releases</mat-icon>
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
    .toolbar { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; justify-content: space-between; margin-bottom: 8px; }
    /* The toggles scroll sideways inside their own box on a phone rather than widening the page. */
    .filters { max-width: 100%; overflow-x: auto; }
    .bases { margin: 8px 0 0; }
    .bases mat-button-toggle-group { font-size: 13px; }
    .lib-filter { width: 220px; }
    .summary { margin: 4px 0 12px; font-size: 13px; color: #c8c8d4; }
    .summary .up { color: #b39dff; }
    .list { display: flex; flex-direction: column; gap: 8px; }
    .row { display: flex; gap: 12px; align-items: flex-start; padding: 10px 12px; border-radius: 10px; background: #1c1c26;
      border: 1px solid rgba(255, 255, 255, 0.06); border-left: 3px solid #555; }
    .row.upgrade { border-left-color: #b39dff; }
    .row.complete { border-left-color: #81c784; }
    .cover { width: 48px; height: 68px; object-fit: cover; border-radius: 4px; flex: none; background: #2a2a36; }
    .cover.none { display: flex; align-items: center; justify-content: center; color: #6a6a78; }
    .body { flex: 1; min-width: 0; }
    header { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; }
    .name { font-weight: 500; font-size: 15px; color: inherit; text-decoration: none; overflow-wrap: anywhere; }
    .name:hover { text-decoration: underline; }
    .meta { margin: 2px 0 4px; font-size: 12px; color: #9a9aa8; overflow-wrap: anywhere; }
    .auto { color: #ffcc80; }
    .line { margin: 2px 0; font-size: 13px; overflow-wrap: anywhere; }
    .line.trackers { color: #9a9aa8; font-size: 12px; }
    .upgrade-line { color: #b39dff; }
    .open { flex: none; color: #b39dff; }
    .state { display: flex; justify-content: center; padding: 32px 0; }
    .error { color: #ff8a80; }
    .empty { display: flex; flex-direction: column; align-items: center; padding: 32px 0; color: #8a8a99; text-align: center; }
    .empty p { max-width: 560px; }
    .more { display: flex; justify-content: center; margin-top: 12px; }
    @media (max-width: 599.98px) {
      .lib-filter { width: 100%; }
      .cover { width: 40px; height: 56px; }
      .open { display: none; }
    }
  `],
})
export class OfficialReleasesComponent implements OnInit {
  private readonly api = inject(OfficialReleasesApiService);

  readonly libraries = input<ReviewLibraryOption[]>([]);
  readonly initialLibrary = input<string | null>(null);

  readonly filters = OFFICIAL_FILTERS;
  readonly filter = signal<OfficialReleasesFilter>('ToAct');
  readonly bases = OFFICIAL_BASES;
  readonly basis = signal<CompletionBasis | ''>('');
  readonly library = signal<string | null>(null);
  readonly items = signal<OfficialReleaseRowDto[]>([]);
  readonly summary = signal<OfficialReleasesSummaryDto | null>(null);
  readonly language = signal('en');
  readonly cursor = signal<string | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly languageLabel = computed(() => languageName(this.language()) || this.language());
  readonly emptyText = computed(() => {
    const basis = this.filter() === 'Upgrades' ? '' : this.basis();
    return basis ? `Nothing here finished by the ${OFFICIAL_BASES.find((b) => b.value === basis)!.label.toLowerCase()}.`
      : officialEmptyText(this.filter(), this.language());
  });

  readonly trackersLine = trackersLine;
  readonly reachSentence = reachSentence;
  readonly upgradeSentence = upgradeSentence;

  ngOnInit(): void {
    this.library.set(this.initialLibrary());
    this.load(false);
  }

  setFilter(filter: OfficialReleasesFilter): void {
    this.filter.set(filter);
    this.load(false);
  }

  setBasis(basis: CompletionBasis | ''): void {
    this.basis.set(basis);
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
    const basis = this.filter() === 'Upgrades' ? null : this.basis() || null;
    this.api.list(this.library(), this.filter(), more ? this.cursor() : null, 50, basis).subscribe({
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
