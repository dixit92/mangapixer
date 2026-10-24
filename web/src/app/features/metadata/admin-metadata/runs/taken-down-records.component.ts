import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';

import { TakenDownRecordsDto } from '../../../../core/api/api-types';
import { MetadataApiService } from '../../metadata-api.service';

/**
 * "MangaDex records taken down" on the Runs tab of Metadata Manager (1.47.0, owner decision 10a): the records the latest-upload sweep
 * stopped finding in MangaDex's answer, with when that began, how many checks in a row, when one last found it, and the series here
 * that use it. Everything comes from what the sweep already stored (`GET /admin/metadata/taken-down`): no request to MangaDex or any
 * other site, and no lookup for a replacement. Shown only while at least one record is missing.
 */
@Component({
  selector: 'app-taken-down-records',
  standalone: true,
  imports: [DatePipe, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (data(); as d) {
      @if (d.total > 0) {
        <section class="takedowns" data-testid="taken-down">
          <h3>MangaDex records taken down <span class="count" data-testid="taken-down-count">{{ d.total }}</span></h3>
          <p class="muted">MangaDex no longer returns these records, so their new chapters are not noticed there. MangaPixer keeps what it
            stored and looks nowhere else.</p>
          <ul class="rows">
            @for (r of d.items; track r.recordId) {
              <li class="row" data-testid="taken-down-row">
                <strong class="title">{{ r.title }}</strong>
                <span class="series">
                  @for (s of r.series; track s.nodeId; let last = $last) {
                    <a [routerLink]="['/libraries', s.libraryId, 'browse', s.nodeId]">{{ s.displayName }}</a>@if (!last) {, }
                  }
                  @if (r.moreSeries) { and {{ r.moreSeries }} more }
                </span>
                <span class="when muted" data-testid="taken-down-since">
                  Missing since {{ r.since | date: 'mediumDate' }} · {{ r.sweeps }} {{ r.sweeps === 1 ? 'check' : 'checks' }} in a row
                  @if (r.lastSeen) { · last found {{ r.lastSeen | date: 'mediumDate' }} }
                </span>
              </li>
            }
          </ul>
          @if (d.total > d.items.length) {
            <p class="muted">Showing the {{ d.items.length }} oldest of {{ d.total }}.</p>
          }
        </section>
      }
    }
  `,
  styles: [`
    :host { display: block; }
    .takedowns { padding: 14px 16px; border-radius: 12px; background: var(--mp-surface-raised); border: 1px solid rgb(var(--mp-ink-rgb) / 0.07);
      margin-bottom: 12px; }
    h3 { font-size: 15px; font-weight: 500; margin: 0 0 4px; }
    .count { margin-left: 6px; font-size: 11px; font-weight: 600; padding: 0 6px; border-radius: 9px; background: var(--mp-accent-strong); color: var(--mp-on-accent);
      line-height: 18px; }
    .muted { color: var(--mp-text-muted); font-size: 13px; margin: 2px 0; }
    .rows { list-style: none; margin: 8px 0 0; padding: 0; display: flex; flex-direction: column; }
    .row { display: grid; grid-template-columns: minmax(0, 2fr) minmax(0, 3fr) minmax(0, 3fr); gap: 4px 16px; padding: 8px 0;
      border-top: 1px solid rgb(var(--mp-ink-rgb) / 0.06); font-size: 13px; overflow-wrap: anywhere; }
    .title { font-weight: 500; }
    .series a { color: var(--mp-accent); text-decoration: none; }
    .series a:hover { text-decoration: underline; }
    .when { margin: 0; }
    @media (max-width: 599.98px) { .row { grid-template-columns: minmax(0, 1fr); } }
  `],
})
export class TakenDownRecordsComponent implements OnInit {
  private readonly api = inject(MetadataApiService);

  readonly data = signal<TakenDownRecordsDto | null>(null);

  ngOnInit(): void {
    // A failed read shows nothing: this is an extra notice, and the rest of the tab is unaffected.
    this.api.getTakenDown().subscribe({ next: (d) => this.data.set(d), error: () => this.data.set(null) });
  }
}
