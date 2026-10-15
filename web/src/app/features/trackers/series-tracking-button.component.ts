import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, input, signal } from '@angular/core';
import { ConnectedPosition, OverlayModule } from '@angular/cdk/overlay';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { TrackerSeriesRowDto, TrackerSeriesSummaryDto } from '../../core/api/api-types';
import { TrackerStateService } from './tracker-state.service';
import { TrackersApiService } from './trackers-api.service';
import { TRACKING_PATH, bucketText, bucketTone, dateText, numbersText, ofText, statusText, trackerName } from './tracker-texts';

/** Below the button, right-aligned; above it when there is no room below. */
const POSITIONS: ConnectedPosition[] = [
  { originX: 'end', originY: 'bottom', overlayX: 'end', overlayY: 'top', offsetY: 6 },
  { originX: 'start', originY: 'bottom', overlayX: 'start', overlayY: 'top', offsetY: 6 },
  { originX: 'end', originY: 'top', overlayX: 'end', overlayY: 'bottom', offsetY: -6 },
];

/**
 * The series header's "Tracking" button (1.43.0 owner redesign): on a folder of a linked series, for a user with a connected
 * tracker, a gold button next to "Series info" opens a small popover - per connected tracker the tracker's name, the series' state
 * chip, MangaPixer's numbers against the tracker's, the dates, and "Open in Tracking" to that series' row on the Tracking page.
 * Hidden without a connected tracker, and for a folder that is not a linked series. Reads `GET /trackers/nodes/{id}/summary`
 * (stored state only - nothing is sent to a tracker). Phone: icon only.
 */
@Component({
  selector: 'app-series-tracking-button',
  standalone: true,
  imports: [OverlayModule, RouterLink, MatButtonModule, MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (shown()) {
      <button mat-stroked-button type="button" class="tracking-btn" cdkOverlayOrigin #origin="cdkOverlayOrigin"
              matTooltip="How this series stands on your trackers" aria-label="Tracking" aria-haspopup="dialog"
              [attr.aria-expanded]="open()" aria-controls="series-tracking-popover" data-testid="series-tracking-button"
              (click)="toggle()">
        <mat-icon>track_changes</mat-icon><span class="lbl">Tracking</span>
      </button>
      <ng-template cdkConnectedOverlay [cdkConnectedOverlayOrigin]="origin" [cdkConnectedOverlayOpen]="open()"
                   [cdkConnectedOverlayPositions]="positions" [cdkConnectedOverlayPush]="true"
                   [cdkConnectedOverlayViewportMargin]="8"
                   (overlayOutsideClick)="onOutside($event, origin.elementRef.nativeElement)" (detach)="open.set(false)"
                   (overlayKeydown)="onKey($event)">
        <section id="series-tracking-popover" class="tracking-popover" role="dialog" aria-label="Tracking for this series"
                 data-testid="series-tracking-popover">
          @for (row of rows(); track row.tracker) {
            <div class="row" [attr.data-testid]="'series-tracking-row-' + row.tracker">
              <div class="head">
                <h2>{{ name(row.tracker) }}</h2>
                <span class="chip" [attr.data-tone]="tone(row)" [attr.data-testid]="'series-tracking-state-' + row.tracker">{{ chip(row) }}</span>
              </div>
              <div class="tiles">
                <div class="tile">
                  <span class="cap">MangaPixer</span>
                  <span class="num" [attr.data-testid]="'series-tracking-here-' + row.tracker">{{ here(row) }}</span>
                </div>
                <div class="tile remote">
                  <span class="cap">{{ name(row.tracker) }}</span>
                  <span class="num" [attr.data-testid]="'series-tracking-remote-' + row.tracker">{{ remote(row) }}</span>
                </div>
              </div>
              @if (row.dates.supported) {
                <p class="dates" [attr.data-testid]="'series-tracking-dates-' + row.tracker">
                  Start {{ date(row.dates.startedOn) }} · Finish {{ date(row.dates.finishedOn) }}
                </p>
              }
              <a mat-flat-button color="primary" class="open" [routerLink]="trackingPath"
                 [queryParams]="{ tracker: row.tracker, series: row.seriesId }" [state]="{ title: row.title }"
                 (click)="open.set(false)" [attr.data-testid]="'series-tracking-open-' + row.tracker">
                Open in Tracking <mat-icon iconPositionEnd>arrow_forward</mat-icon>
              </a>
            </div>
          }
        </section>
      </ng-template>
    }
  `,
  styles: [`
    :host { display: contents; }
    .tracking-btn { border-color: var(--mp-warn-strong); color: var(--mp-warn); background: rgb(var(--mp-warn-strong-rgb) / 0.12); }
    .tracking-btn mat-icon { margin-right: 4px; color: var(--mp-warn); }
    @media (max-width: 599.98px) {
      .tracking-btn .lbl { display: none; }
      .tracking-btn mat-icon { margin-right: 0; }
      .tracking-btn { min-width: 0; padding: 0 8px; }
    }
    .tracking-popover {
      width: min(380px, calc(100vw - 16px)); box-sizing: border-box; max-height: calc(100dvh - 96px); overflow-y: auto;
      background: var(--mp-surface-raised); color: var(--mp-text); border: 1px solid rgb(var(--mp-ink-rgb) / 0.16);
      border-radius: 12px; padding: 14px 16px; display: flex; flex-direction: column; gap: 14px;
      box-shadow: 0 8px 24px rgb(var(--mp-shade-rgb) / 0.45);
    }
    .row { display: flex; flex-direction: column; gap: 10px; min-width: 0; }
    .row + .row { border-top: 1px solid rgb(var(--mp-ink-rgb) / 0.1); padding-top: 12px; }
    .head { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 6px 10px; }
    h2 { margin: 0; font-size: 15px; font-weight: 500; color: var(--mp-text-strong); }
    .chip { font-size: 12px; padding: 2px 8px; border-radius: 10px; overflow-wrap: anywhere;
            background: rgb(var(--mp-ink-rgb) / 0.08); color: var(--mp-text-secondary); }
    .chip[data-tone='ok'] { background: rgb(var(--mp-success-strong-rgb) / 0.16); color: var(--mp-success); }
    .chip[data-tone='ahead'] { background: rgb(var(--mp-accent-strong-rgb) / 0.16); color: var(--mp-accent); }
    .chip[data-tone='tracker'], .chip[data-tone='warn'] { background: rgb(var(--mp-warn-strong-rgb) / 0.16); color: var(--mp-warn); }
    .tiles { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px; }
    .tile { background: var(--mp-surface); border-radius: 8px; padding: 8px 10px; display: flex; flex-direction: column; gap: 2px; min-width: 0; }
    .tile.remote { box-shadow: inset 0 -2px 0 var(--mp-tracker-mark); }
    .cap { font-size: 12px; color: var(--mp-text-muted); }
    .num { font-size: 15px; color: var(--mp-text-strong); overflow-wrap: anywhere; }
    .remote .num { color: var(--mp-warn); }
    .dates { margin: 0; font-size: 13px; color: var(--mp-text-secondary); }
    .open { align-self: flex-start; }
  `],
})
export class SeriesTrackingButtonComponent implements OnInit {
  private readonly api = inject(TrackersApiService);
  private readonly state = inject(TrackerStateService);

  /** The folder being browsed. */
  readonly nodeId = input.required<string>();

  readonly trackingPath = TRACKING_PATH;
  readonly positions = POSITIONS;
  readonly open = signal(false);
  readonly summary = signal<TrackerSeriesSummaryDto | null>(null);

  readonly rows = computed(() => this.summary()?.rows ?? []);
  /** A connected tracker and a linked series with at least one row. */
  readonly shown = computed(() => this.state.hasConnected() && !!this.summary()?.seriesId && this.rows().length > 0);

  readonly name = trackerName;
  readonly date = dateText;

  private requested: string | null = null;

  constructor() {
    // A new folder (or the first connected tracker) reads its summary; a folder without one hides the button.
    effect(() => {
      const id = this.nodeId();
      if (!this.state.hasConnected()) {
        this.summary.set(null);
        return;
      }
      this.load(id);
    });
  }

  ngOnInit(): void {
    this.state.ensureLoaded();
  }

  toggle(): void {
    const next = !this.open();
    this.open.set(next);
    // Opening re-reads the numbers (a sync may have run since).
    if (next) this.load(this.nodeId(), true);
  }

  onOutside(event: MouseEvent, origin: HTMLElement): void {
    // A click on the button itself toggles; anything else outside closes.
    if (event.target instanceof Node && origin.contains(event.target)) return;
    this.open.set(false);
  }

  onKey(event: KeyboardEvent): void {
    if (event.key === 'Escape') this.open.set(false);
  }

  chip(row: TrackerSeriesRowDto): string {
    return bucketText(row);
  }

  tone(row: TrackerSeriesRowDto): string {
    return bucketTone(row.bucket);
  }

  here(row: TrackerSeriesRowDto): string {
    const parts = [ofText(row.allChapters ?? row.chapters, row.totalChapters, 'ch'), ofText(row.allVolumes ?? row.volumes, row.totalVolumes, 'vol')]
      .filter((p): p is string => !!p);
    return parts.length ? parts.join(' · ') : 'nothing read';
  }

  remote(row: TrackerSeriesRowDto): string {
    if (row.remoteMissing) return 'not on your list';
    const n = numbersText(row.remoteChapters, row.remoteVolumes);
    const s = statusText(row.remoteStatus);
    if (!n && !s) return row.lastSyncAt ? 'nothing read' : 'not checked yet';
    return [n, s].filter((p) => !!p).join(' · ');
  }

  private load(id: string, force = false): void {
    if (!force && this.requested === id) return;
    // Another folder: its own summary or nothing - never the previous folder's while this one loads.
    if (this.requested !== id) this.summary.set(null);
    this.requested = id;
    this.api.summary(id).subscribe({
      next: (s) => { if (this.nodeId() === id) this.summary.set(s); },
      error: () => { if (this.nodeId() === id) this.summary.set(null); },
    });
  }
}
