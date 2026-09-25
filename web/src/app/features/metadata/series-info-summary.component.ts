import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';

import { SeriesInfoDto } from '../../core/api/api-types';
import { itemLine, metaLine, roleLabel } from './series-info-labels';

/**
 * Presentational series summary (1.24.0) shared by the overlay and the series page,
 * so the two surfaces never disagree: title, alternative titles, the facts line,
 * credits, genres, description, the per-item ComicInfo block (archives), and the
 * mixed / none / Don't match states. `compact` (overlay) clamps the description to
 * six lines with a "More" toggle and folds long alt-title and genre lists.
 *
 * All text is bound as text (never innerHTML): ComicInfo and provider values are
 * data, never markup.
 */
@Component({
  selector: 'app-series-info-summary',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @let i = info();
    @switch (i.state) {
      @case ('None') {
        <p class="empty" data-testid="series-none">No series information.</p>
      }
      @case ('DontMatch') {
        <p class="empty" data-testid="series-dont-match">
          Marked "Don't match": this folder is not one series, so nothing is inherited from above.
        </p>
      }
      @case ('Mixed') {
        <h2 class="title">{{ i.anchorDisplayName }}</h2>
        <p class="mixed-lead" data-testid="series-mixed">This folder contains several series.</p>
        <ul class="mixed-list">
          @for (m of i.mixedSeries ?? []; track m.name) {
            <li><span class="mixed-name">{{ m.name }}</span> <span class="muted">{{ m.count }} item{{ m.count === 1 ? '' : 's' }}</span></li>
          }
        </ul>
      }
      @default {
        @if (i.web && i.web.hasImage && i.web.imageUrl) {
          <img class="poster" [src]="i.web.imageUrl" alt="" loading="lazy">
        }
        <h2 class="title" data-testid="series-title">{{ i.title }}</h2>
        @if (altTitles().length > 0) {
          <p class="alt">also: {{ altTitles().join(', ') }}@if (altOverflow() > 0) {, +{{ altOverflow() }}}</p>
        }
        @if (facts()) {
          <p class="facts">{{ facts() }}</p>
        }
        @if (i.licensedEn !== null && i.licensedEn !== undefined) {
          <p class="facts">{{ i.licensedEn ? 'Licensed in English' : 'Not licensed in English' }}@if (i.translationComplete === true) { · Translation complete}</p>
        }
        @if (credits().length > 0) {
          <p class="credits">
            @for (c of credits(); track c.label; let last = $last) {
              <span class="muted">{{ c.label }}:</span> {{ c.names }}@if (!last) { · }
            }
          </p>
        }
        @if (genres().length > 0) {
          <div class="chips">
            @for (g of genres(); track g) { <span class="chip">{{ g }}</span> }
            @if (genreOverflow() > 0) { <span class="chip more">+{{ genreOverflow() }}</span> }
          </div>
        }
        @if (i.description) {
          <p class="description" [class.clamped]="compact() && !expanded()">{{ i.description }}</p>
          @if (compact()) {
            <button type="button" class="more" (click)="expanded.set(!expanded())">{{ expanded() ? 'Less' : 'More' }}</button>
          }
        }
        @if (i.statusText && !compact()) {
          <p class="muted status-note">{{ i.statusText }}</p>
        }
      }
    }

    @if (i.item) {
      <section class="item" data-testid="series-item">
        <h3>This item@if (itemFacts()) { <span class="muted"> · {{ itemFacts() }}</span>}</h3>
        @if (i.item.title) { <p class="item-title">{{ i.item.title }}</p> }
        @if (i.item.summary) { <p class="item-summary" [class.clamped]="compact()">{{ i.item.summary }}</p> }
      </section>
    }
  `,
  styles: [`
    :host { display: block; }
    .title { margin: 0 0 4px; font-size: 20px; font-weight: 600; line-height: 1.25; color: #f0f0f6; }
    .poster { float: left; width: 96px; height: 136px; object-fit: cover; border-radius: 6px; margin: 0 12px 8px 0; }
    .alt, .facts, .credits { margin: 2px 0; font-size: 13px; color: #c8c8d4; }
    .alt { color: #9a9aad; }
    .muted { color: #8a8a99; }
    .chips { display: flex; flex-wrap: wrap; gap: 6px; margin: 8px 0; }
    .chip { font-size: 12px; padding: 2px 8px; border-radius: 12px; background: rgba(124, 77, 255, 0.18); color: #d8ccff; }
    .chip.more { background: rgba(255, 255, 255, 0.08); color: #b0b0c0; }
    .description, .item-summary { margin: 8px 0 0; font-size: 14px; line-height: 1.5; white-space: pre-line; color: #dcdce6; clear: both; }
    .clamped { display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 6; overflow: hidden; }
    .item-summary.clamped { -webkit-line-clamp: 3; }
    .more { background: none; border: none; padding: 4px 0; color: #b39dff; cursor: pointer; font-size: 13px; }
    .empty, .mixed-lead { margin: 4px 0; color: #c8c8d4; }
    .mixed-list { margin: 6px 0 0; padding-left: 18px; font-size: 13px; }
    .mixed-name { color: #e6e6ee; }
    .status-note { font-size: 12px; margin-top: 6px; }
    .item { clear: both; margin-top: 14px; padding-top: 10px; border-top: 1px solid rgba(255, 255, 255, 0.08); }
    .item h3 { margin: 0 0 4px; font-size: 13px; font-weight: 600; color: #e6e6ee; }
    .item-title { margin: 0; font-size: 14px; color: #dcdce6; }
  `],
})
export class SeriesInfoSummaryComponent {
  readonly info = input.required<SeriesInfoDto>();

  /** Overlay mode: clamp the description, fold long lists. */
  readonly compact = input(false);

  readonly expanded = signal(false);

  private readonly altLimit = computed(() => (this.compact() ? 2 : 50));
  private readonly genreLimit = computed(() => (this.compact() ? 3 : 50));

  readonly altTitles = computed(() => (this.info().altTitles ?? []).slice(0, this.altLimit()));
  readonly altOverflow = computed(() => Math.max(0, (this.info().altTitles ?? []).length - this.altLimit()));
  readonly genres = computed(() => (this.info().genres ?? []).slice(0, this.genreLimit()));
  readonly genreOverflow = computed(() => Math.max(0, (this.info().genres ?? []).length - this.genreLimit()));
  readonly facts = computed(() => metaLine(this.info()));
  readonly itemFacts = computed(() => itemLine(this.info().item));

  /** Credits grouped by role label, in first-seen order: "Story: A, B . Art: C". */
  readonly credits = computed(() => {
    const groups = new Map<string, string[]>();
    for (const c of this.info().creators ?? []) {
      const label = roleLabel(c.role);
      const names = groups.get(label) ?? [];
      if (!names.includes(c.name)) names.push(c.name);
      groups.set(label, names);
    }
    const max = this.compact() ? 3 : 20;
    return [...groups.entries()].map(([label, names]) => ({
      label,
      names: names.length > max ? `${names.slice(0, max).join(', ')}, +${names.length - max}` : names.join(', '),
    }));
  });
}
