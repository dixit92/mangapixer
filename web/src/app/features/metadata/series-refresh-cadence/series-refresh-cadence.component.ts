import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';

import { ApiService } from '../../../core/api/api.service';
import { SeriesRefreshCadenceDto } from '../../../core/api/api-types';
import { cadenceLabel } from '../../admin/scheduled-jobs/scheduled-jobs.component';

/** "every 2 months" for an interval in days (between two volumes, or two chapters). */
export function intervalLabel(days: number): string {
  if (days < 60) {
    const weeks = Math.max(1, Math.round(days / 7));
    return weeks === 1 ? 'every week' : `every ${weeks} weeks`;
  }
  const months = Math.round(days / 30);
  return months === 1 ? 'every month' : `every ${months} months`;
}

/** The one line for a series' refresh cadence. */
export function cadenceLine(c: SeriesRefreshCadenceDto, nowMs: number): string {
  const every = cadenceLabel(c.days).toLowerCase();
  // 1.35.1: the pace that set the days - new chapters (observed, or the average since the series began) or new volumes.
  const fromChapters = c.paceFrom === 'chapters' || (c.paceFrom == null && c.volumeIntervalDays == null);
  const pace = fromChapters && c.chapterIntervalDays != null
    ? `a new chapter about ${intervalLabel(c.chapterIntervalDays)}${c.chapterIntervalEstimated ? ' on average' : ''}`
    : c.volumeIntervalDays != null ? `a new volume about ${intervalLabel(c.volumeIntervalDays)}` : null;
  const why = c.reason === 'pace' && pace ? ` (${pace})`
    : c.reason === 'finished' ? ' (finished)'
    : c.reason === 'paused' ? ' (on hiatus, or nothing new for six months)'
    : c.reason === 'pace_unknown' ? ' (following its pace, not known yet)'
    : ' (your choice for ongoing series)';
  const days = Math.ceil((Date.parse(c.nextCheckAt) - nowMs) / 86_400_000);
  const next = days <= 0 ? 'next check at the next refresh' : days === 1 ? 'next check in 1 day' : `next check in ${days} days`;
  return `Checked ${every}${why}; ${next}.`;
}

/**
 * One admin-only line on a linked series (1.32.0): how often its information is refreshed and why - the admin's choice or the
 * series' publishing pace - and when next (`<app-series-refresh-cadence [linkNodeId]="..." />`, the node holding the link).
 * Shows nothing for a series without a Confirmed / Auto link.
 */
@Component({
  selector: 'app-series-refresh-cadence',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (line(); as l) { <p class="cadence" data-testid="series-refresh-cadence">{{ l }}</p> }
  `,
  styles: [`.cadence { color: var(--mp-text-muted); font-size: 13px; margin: 4px 0; }`],
})
export class SeriesRefreshCadenceComponent {
  private readonly api = inject(ApiService);
  private readonly destroyRef = inject(DestroyRef);

  readonly linkNodeId = input<string | null | undefined>(null);
  readonly line = signal<string | null>(null);

  constructor() {
    effect(() => {
      const id = this.linkNodeId();
      untracked(() => this.load(id));
    });
  }

  private load(id: string | null | undefined): void {
    this.line.set(null);
    if (!id) return;
    this.api.getSeriesRefreshCadence(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: dto => this.line.set(dto ? cadenceLine(dto, Date.now()) : null),
        error: () => this.line.set(null),
      });
  }
}
