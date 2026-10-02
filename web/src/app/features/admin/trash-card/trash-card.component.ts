import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { Observable } from 'rxjs';

import { ApiService } from '../../../core/api/api.service';
import {
  ApiError,
  TrashCountsDto,
  TrashHold,
  TrashLibraryDto,
  TrashOverviewDto,
  UpdateTrashSettingsRequest,
} from '../../../core/api/api-types';
import { formatBytes } from '../backup-snapshot-move-status.component';

/** The retention presets as the admin reads them. */
const RETENTION_LABELS: Record<number, string> = {
  1: 'Daily (1 day)',
  7: 'Weekly (7 days)',
  30: 'Monthly (30 days)',
  90: 'Quarterly (90 days)',
  365: 'Yearly (365 days)',
};

/** Why a library keeps its trash, in the admin's words. */
export const HOLD_TEXT: Record<TrashHold, string> = {
  scan_running: 'A scan of this library is running; its trash is emptied after the scan.',
  root_unavailable: "The last scan could not reach this library's folder (a disk or share may be offline).",
  burst: 'More than half of this library would go - a disk or folder may have been offline when it was scanned.',
};

/** "3 items (2 files, 1 folder) · 4 reading-state entries · 6.0 KB" - what a trash count holds, also used by the jobs row's confirm. */
export function trashCountsText(c: TrashCountsDto): string {
  if (c.nodes === 0) return 'nothing';
  const parts = [`${c.nodes} ${c.nodes === 1 ? 'item' : 'items'}`];
  const detail: string[] = [];
  if (c.archives > 0) detail.push(`${c.archives} ${c.archives === 1 ? 'file' : 'files'}`);
  if (c.folders > 0) detail.push(`${c.folders} ${c.folders === 1 ? 'folder' : 'folders'}`);
  if (detail.length > 0) parts[0] += ` (${detail.join(', ')})`;
  if (c.userStateRows > 0) parts.push(`${c.userStateRows} reading-state ${c.userStateRows === 1 ? 'entry' : 'entries'}`);
  parts.push(formatBytes(c.bytes));
  return parts.join(' · ');
}

/** What a confirm step is about. */
type Pending =
  | { kind: 'empty-all' }
  | { kind: 'empty-library'; library: TrashLibraryDto }
  | { kind: 'clean' };

/**
 * Trash card (1.31.0): Plex-style "Empty trash" and "Clean bundles" for the admin page, self-contained
 * (`<app-trash-card />`).
 * - The move window, which is also the trash retention: how long a moved or renamed series is recognised (keeping its
 *   reading state) and how long removed items stay in the trash before they can be emptied.
 * - A status line for automatic cleaning (off by default); the switch and the hour moved to the Scheduled jobs card in 1.32.0,
 *   so each job time is set in one place. A daily run empties the trash and cleans bundles; libraries with a hold are skipped.
 * - "Empty trash now" (all libraries, or one - which also releases its hold) and "Clean bundles now", each confirmed
 *   with what it removes.
 */
@Component({
  selector: 'app-trash-card',
  standalone: true,
  imports: [CommonModule, MatButtonModule, MatCardModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-card data-testid="trash-card">
      <mat-card-header>
        <mat-card-title>Trash</mat-card-title>
        <mat-card-subtitle>Removed items and unused files</mat-card-subtitle>
      </mat-card-header>
      <mat-card-content>
        @if (loading()) {
          <p class="muted">Loading…</p>
        } @else if (overview(); as o) {
          <div class="row">
            <label for="trash-retention">Keep removed items for</label>
            <select id="trash-retention" data-testid="trash-retention" [disabled]="busy()"
                    (change)="setRetention(+$any($event.target).value)">
              @for (days of o.settings.allowedRetentionDays; track days) {
                <option [value]="days" [selected]="days === o.settings.retentionDays">{{ retentionLabel(days) }}</option>
              }
            </select>
          </div>
          <p class="hint">
            A series moved or renamed within this time is recognised and keeps its reading state.
            After it, removed items can leave the trash - with their reading progress, read marks, bookmarks and favorites.
          </p>

          <p class="auto" data-testid="trash-auto-status">
            <mat-icon inline>schedule</mat-icon>
            <span>
              Automatic cleaning:
              @if (o.settings.automaticCleaning) {
                daily at {{ hour(o.settings.automaticHour) }} (server time); libraries with a hold are skipped.
              } @else {
                Off. Nothing is removed unless you choose "Empty trash now" or "Clean bundles now".
              }
              <button type="button" class="link" data-testid="trash-auto-link" (click)="showSchedule()">Change in Scheduled jobs</button>
            </span>
          </p>

          <h4>In the trash</h4>
          @if (o.libraries.length === 0) {
            <p class="muted" data-testid="trash-empty-state">Nothing has been removed from your libraries.</p>
          } @else {
            <ul class="libs">
              @for (lib of o.libraries; track lib.libraryId) {
                <li [attr.data-testid]="'trash-lib-' + lib.libraryId">
                  <div class="lib-head">
                    <span class="name">{{ lib.name }}</span>
                    @if (lib.hold) {
                      <span class="hold" [attr.data-hold]="lib.hold"><mat-icon inline>pause_circle</mat-icon> Held</span>
                    }
                  </div>
                  <div class="lib-counts">
                    {{ countsText(lib.eligible) }}
                    @if (lib.waiting > 0) { · {{ lib.waiting }} waiting (removed recently) }
                  </div>
                  @if (lib.hold) { <div class="hint">{{ holdText(lib.hold) }}</div> }
                  @if (lib.eligible.nodes > 0 && lib.hold !== 'scan_running') {
                    <button mat-stroked-button type="button" [disabled]="busy()"
                            [attr.data-testid]="'trash-empty-lib-' + lib.libraryId"
                            (click)="ask({ kind: 'empty-library', library: lib })">
                      Empty this library's trash
                    </button>
                  }
                </li>
              }
            </ul>
          }

          <div class="actions">
            <button mat-raised-button color="warn" type="button" data-testid="trash-empty-all"
                    [disabled]="busy() || o.total.nodes === 0" (click)="ask({ kind: 'empty-all' })">
              Empty trash now
            </button>
            <button mat-stroked-button type="button" data-testid="trash-clean"
                    [disabled]="busy() || o.bundles.files === 0" (click)="ask({ kind: 'clean' })">
              Clean bundles now
            </button>
          </div>
          <p class="hint">
            Ready to empty: {{ countsText(o.total) }}.
            Unused files: {{ o.bundles.files }} ({{ size(o.bundles.bytes) }}) - thumbnails and covers in the data folder
            that nothing uses any more. Covers and posters of linked series are kept.
          </p>

          @if (pending(); as p) {
            <div class="confirm" role="alertdialog" aria-labelledby="trash-confirm-h" data-testid="trash-confirm">
              <h5 id="trash-confirm-h">{{ confirmTitle(p) }}</h5>
              <p>{{ confirmText(p, o) }}</p>
              @if (p.kind === 'empty-library' && p.library.hold) {
                <p class="warn">{{ holdText(p.library.hold) }} Empty it anyway?</p>
              }
              <div class="actions">
                <button mat-flat-button color="warn" type="button" data-testid="trash-confirm-yes"
                        [disabled]="busy()" (click)="confirm()">{{ confirmButton(p) }}</button>
                <button mat-button type="button" [disabled]="busy()" (click)="cancel()">Cancel</button>
              </div>
            </div>
          }

          <div class="last">
            <div data-testid="trash-last-empty">
              Last emptied:
              @if (o.lastEmpty; as r) {
                {{ r.at | date: 'short' }} ({{ r.automatic ? 'automatic' : 'by an admin' }}) - {{ r.count }} {{ r.count === 1 ? 'item' : 'items' }}, {{ size(r.bytes) }}
                @if (r.heldLibraries > 0) { · {{ r.heldLibraries }} held }
              } @else { never }
            </div>
            <div data-testid="trash-last-clean">
              Last cleaned:
              @if (o.lastBundleClean; as r) {
                {{ r.at | date: 'short' }} ({{ r.automatic ? 'automatic' : 'by an admin' }}) - {{ r.count }} {{ r.count === 1 ? 'file' : 'files' }}, {{ size(r.bytes) }}
              } @else { never }
            </div>
          </div>

          @if (message()) { <p class="ok" role="status" data-testid="trash-message">{{ message() }}</p> }
          @if (error()) { <div class="error" role="alert">{{ error() }}</div> }
        } @else {
          <div class="error" role="alert">{{ error() }}</div>
        }
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    mat-card { margin: 0; }
    .row { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; margin: 10px 0; font-size: 14px; }
    .row label { min-width: 110px; }
    select { font: inherit; padding: 4px 6px; max-width: 100%; }
    .hint, .muted { color: #999; font-size: 13px; margin: 6px 0; }
    .auto { display: flex; align-items: flex-start; gap: 6px; font-size: 14px; margin: 10px 0; }
    .auto mat-icon { flex: 0 0 auto; margin-top: 3px; }
    .link { background: none; border: none; padding: 0; margin-left: 4px; font: inherit; color: #b39ddb; text-decoration: underline; cursor: pointer; }
    h4 { margin: 16px 0 6px; }
    h5 { margin: 0 0 6px; font-size: 14px; }
    .libs { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 10px; }
    .libs li { border: 1px solid rgba(255, 255, 255, 0.08); border-radius: 6px; padding: 8px 10px; min-width: 0; }
    .lib-head { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; }
    .name { font-weight: 500; overflow-wrap: anywhere; }
    .hold { color: #ffb300; font-size: 13px; display: inline-flex; align-items: center; gap: 4px; }
    .lib-counts { font-size: 13px; margin: 4px 0; }
    .libs button { margin-top: 4px; }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 12px; }
    .confirm { border: 1px solid rgba(244, 67, 54, 0.5); border-radius: 6px; padding: 10px 12px; margin: 12px 0; font-size: 14px; }
    .confirm p { margin: 4px 0; }
    .warn { color: #ffb300; }
    .last { color: #999; font-size: 13px; margin-top: 12px; display: flex; flex-direction: column; gap: 2px; }
    .ok { color: #4caf50; font-size: 14px; }
    .error { color: #f44336; font-size: 14px; margin: 8px 0; }
  `],
})
export class TrashCardComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly loading = signal(true);
  readonly busy = signal(false);
  readonly overview = signal<TrashOverviewDto | null>(null);
  readonly pending = signal<Pending | null>(null);
  readonly message = signal<string | null>(null);
  readonly error = signal<string | null>(null);

  readonly heldCount = computed(() => this.overview()?.libraries.filter((l) => !!l.hold && l.eligible.nodes > 0).length ?? 0);

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.api.getTrash().subscribe({
      next: (o) => {
        this.overview.set(o);
        this.loading.set(false);
      },
      error: (e: ApiError) => {
        this.loading.set(false);
        this.error.set(e?.message || 'The trash could not be loaded.');
      },
    });
  }

  retentionLabel(days: number): string {
    return RETENTION_LABELS[days] ?? `${days} days`;
  }

  holdText(hold: TrashHold): string {
    return HOLD_TEXT[hold] ?? hold;
  }

  hour(h: number): string {
    return `${String(h).padStart(2, '0')}:00`;
  }

  size(bytes: number): string {
    return formatBytes(bytes);
  }

  countsText(c: TrashCountsDto): string {
    return trashCountsText(c);
  }

  setRetention(days: number): void {
    this.saveSettings({ retentionDays: days }, `Removed items are kept for ${this.retentionLabel(days).toLowerCase()}.`);
  }

  /** Moves the page to the Scheduled jobs row where automatic cleaning is switched on and timed (1.32.0). */
  showSchedule(): void {
    const row = document.getElementById('job-trash');
    if (!row) return;
    row.scrollIntoView({ behavior: 'smooth', block: 'center' });
    // The slide toggle's host is not focusable; its inner button is.
    const toggle = row.querySelector<HTMLElement>('[data-testid="job-trash-auto"]');
    (toggle?.querySelector<HTMLElement>('button') ?? toggle)?.focus({ preventScroll: true });
  }

  ask(p: Pending): void {
    this.message.set(null);
    this.error.set(null);
    this.pending.set(p);
  }

  cancel(): void {
    this.pending.set(null);
  }

  confirmTitle(p: Pending): string {
    switch (p.kind) {
      case 'empty-all': return 'Empty the trash now?';
      case 'empty-library': return `Empty the trash of ${p.library.name}?`;
      case 'clean': return 'Clean bundles now?';
    }
  }

  confirmText(p: Pending, o: TrashOverviewDto): string {
    switch (p.kind) {
      case 'empty-all': {
        const held = this.heldCount();
        return `This removes ${this.countsText(o.total)}. This cannot be undone.`
          + (held > 0 ? ` ${held} held ${held === 1 ? 'library keeps its' : 'libraries keep their'} trash.` : '');
      }
      case 'empty-library':
        return `This removes ${this.countsText(p.library.eligible)}. This cannot be undone.`;
      case 'clean':
        return `This removes ${o.bundles.files} unused ${o.bundles.files === 1 ? 'file' : 'files'} (${formatBytes(o.bundles.bytes)}).`;
    }
  }

  confirmButton(p: Pending): string {
    switch (p.kind) {
      case 'clean': return 'Clean bundles';
      default: return 'Empty trash';
    }
  }

  confirm(): void {
    const p = this.pending();
    if (!p || this.busy()) return;
    switch (p.kind) {
      case 'empty-all':
        this.empty(null, false);
        break;
      case 'empty-library':
        this.empty(p.library.libraryId, !!p.library.hold);
        break;
      case 'clean':
        this.run(this.api.cleanBundles(), (r) => `Removed ${r.files} unused ${r.files === 1 ? 'file' : 'files'} (${formatBytes(r.bytes)}).`);
        break;
    }
  }

  private empty(libraryId: string | null, releaseHold: boolean): void {
    this.run(this.api.emptyTrash({ libraryId, releaseHold }), (r) => {
      const held = r.held.length > 0 ? ` ${r.held.length} held ${r.held.length === 1 ? 'library was' : 'libraries were'} skipped.` : '';
      return `Removed ${this.countsText(r.removed)}.${held}`;
    });
  }

  private run<T>(call: Observable<T>, done: (r: T) => string): void {
    this.busy.set(true);
    this.error.set(null);
    call.subscribe({
      next: (r) => {
        this.busy.set(false);
        this.pending.set(null);
        this.message.set(done(r));
        this.load();
      },
      error: (e: ApiError) => {
        this.busy.set(false);
        this.pending.set(null);
        this.error.set(e?.message || 'The action failed.');
        this.load();
      },
    });
  }

  private saveSettings(request: UpdateTrashSettingsRequest, message: string): void {
    this.busy.set(true);
    this.error.set(null);
    this.message.set(null);
    this.api.updateTrashSettings(request).subscribe({
      next: (settings) => {
        this.busy.set(false);
        this.pending.set(null);
        this.message.set(message);
        const o = this.overview();
        if (o) this.overview.set({ ...o, settings });
        // The window changes what is ready: reload the preview.
        this.load();
      },
      error: (e: ApiError) => {
        this.busy.set(false);
        this.pending.set(null);
        this.error.set(e?.message || 'The setting could not be saved.');
      },
    });
  }
}
