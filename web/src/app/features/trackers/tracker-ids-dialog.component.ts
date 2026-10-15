import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';

import { TrackerSeriesIdDto, TrackerSeriesIdsDto } from '../../core/api/api-types';
import { TrackersApiService } from './trackers-api.service';
import { failureText, parseTrackerId, trackerName } from './tracker-texts';

export interface TrackerIdsDialogData {
  nodeId: string;
  slug: string;
}

/**
 * "Set MyAnimeList id" / "Set MangaUpdates id" (1.43.0, owner Q6, admin): the tracker's number of the series a folder is linked
 * to, for every user's sync. Shows the id in use and where it comes from; takes the number or the series address on the tracker's
 * site (read here, nothing is fetched); an empty field removes the admin's id so the stored one applies again. Closes with true
 * after a change.
 */
@Component({
  selector: 'app-tracker-ids-dialog',
  standalone: true,
  imports: [FormsModule, MatDialogModule, MatButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h2 mat-dialog-title>Set {{ name }} id</h2>
    <mat-dialog-content>
      <form id="tracker-id-form" (ngSubmit)="save()" data-testid="tracker-ids-dialog">
        @if (loading()) {
          <p class="muted">Loading…</p>
        } @else if (ids(); as d) {
          @if (!d.seriesId) {
            <p class="warn" data-testid="tracker-ids-not-linked">This folder is not linked to a series. Link it first (Identify).</p>
          } @else {
            <p data-testid="tracker-ids-current">{{ currentText() }}</p>
            <label class="field">
              <span>{{ name }} number or series address</span>
              <input type="text" name="remoteId" autocomplete="off" spellcheck="false" maxlength="512" [disabled]="busy()"
                     [placeholder]="placeholder" [ngModel]="text()" (ngModelChange)="text.set($event)" data-testid="tracker-ids-input" />
            </label>
            <p class="muted small">Used for every user's sync of this series. Leave it empty to use the number from the series' own
              record again.</p>
            @if (text().trim() !== '' && parsed() === null) {
              <p class="error" role="alert" data-testid="tracker-ids-invalid">{{ invalidText }}</p>
            }
          }
        }
        @if (error(); as e) { <p class="error" role="alert" data-testid="tracker-ids-error">{{ e }}</p> }
      </form>
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" mat-dialog-close>Cancel</button>
      <button mat-raised-button color="primary" type="submit" form="tracker-id-form" [disabled]="!canSave()" data-testid="tracker-ids-save">
        Save
      </button>
    </mat-dialog-actions>
  `,
  styles: [`
    .field { display: flex; flex-direction: column; gap: 2px; margin: 8px 0; font-size: 13px; color: var(--mp-text-secondary); min-width: 0; }
    .field input { font: inherit; font-size: 14px; padding: 6px 8px; max-width: 100%; min-width: 0; box-sizing: border-box;
                   color: var(--mp-text); background: var(--mp-surface); border: 1px solid rgb(var(--mp-ink-rgb) / 0.2); border-radius: 4px; }
    p { overflow-wrap: anywhere; }
    .muted { color: var(--mp-text-muted); font-size: 13px; }
    .small { font-size: 12px; }
    .warn { color: var(--mp-warn-strong); }
    .error { color: var(--mp-error-strong); font-size: 13px; }
  `],
})
export class TrackerIdsDialogComponent implements OnInit {
  private readonly api = inject(TrackersApiService);
  private readonly ref = inject(MatDialogRef<TrackerIdsDialogComponent, boolean>);
  readonly data = inject<TrackerIdsDialogData>(MAT_DIALOG_DATA);

  readonly name = trackerName(this.data.slug);
  readonly placeholder = this.data.slug === 'mal' ? 'e.g. 2 or https://myanimelist.net/manga/2/...' : 'e.g. https://www.mangaupdates.com/series/...';
  readonly invalidText = this.data.slug === 'mal'
    ? 'Enter the MyAnimeList number, or a myanimelist.net/manga/... address.'
    : 'Enter the MangaUpdates number, or a mangaupdates.com/series/... address.';

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly ids = signal<TrackerSeriesIdsDto | null>(null);
  readonly text = signal('');
  readonly error = signal<string | null>(null);

  readonly entry = computed<TrackerSeriesIdDto | null>(() => this.ids()?.ids.find((i) => i.tracker === this.data.slug) ?? null);
  readonly parsed = computed(() => parseTrackerId(this.data.slug, this.text()));
  readonly canSave = computed(() => {
    const p = this.parsed();
    if (this.busy() || this.loading() || !this.ids()?.seriesId || p === null) return false;
    return p !== (this.entry()?.overrideId ?? '');
  });

  readonly currentText = computed(() => {
    const e = this.entry();
    if (!e?.remoteId) return `No ${this.name} number is known for this series - its sync is skipped until one is set.`;
    const from = e.source === 'Admin' ? 'set by an admin'
      : e.source === 'MangaDex' ? 'from the series\' MangaDex record'
        : 'from the linked record';
    return `In use: ${e.remoteId} (${from}).`;
  });

  ngOnInit(): void {
    this.api.seriesIds(this.data.nodeId).subscribe({
      next: (d) => {
        this.ids.set(d);
        this.text.set(d.ids.find((i) => i.tracker === this.data.slug)?.overrideId ?? '');
        this.loading.set(false);
      },
      error: (err: unknown) => { this.loading.set(false); this.error.set(failureText(err, this.data.slug)); },
    });
  }

  save(): void {
    const id = this.parsed();
    if (!this.canSave() || id === null) return;
    this.busy.set(true);
    this.error.set(null);
    this.api.setSeriesId(this.data.nodeId, this.data.slug, id).subscribe({
      next: () => { this.busy.set(false); this.ref.close(true); },
      error: (err: unknown) => { this.busy.set(false); this.error.set(failureText(err, this.data.slug)); },
    });
  }
}
