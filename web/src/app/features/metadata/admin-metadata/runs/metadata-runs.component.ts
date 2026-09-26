import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Subscription, timer } from 'rxjs';

import { ApiError, MetadataAutoMatchStatusDto, MetadataMatchRunDto } from '../../../../core/api/api-types';
import { MetadataApiService } from '../../metadata-api.service';
import { MetadataReviewStateService } from '../../metadata-review-state.service';
import { RUN_TRIGGER_LABELS, runProgress, waitingLabel } from '../metadata-admin-labels';

/** Poll interval while a run is live. */
export const RUNS_POLL_MS = 5000;

/**
 * Runs tab of `/admin/metadata` (stage 2): the worker's status (on / waiting and why /
 * pending folders), the live run's progress with Cancel, the run history with its
 * counters, and the local "auto links changed by an admin" counter - how often an
 * automatic link was later changed by hand (a local quality measure; nothing is sent).
 * Polls every 5 s only while a run is live.
 */
@Component({
  selector: 'app-metadata-runs',
  standalone: true,
  imports: [DatePipe, MatButtonModule, MatIconModule, MatProgressBarModule, MatProgressSpinnerModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="runs" data-testid="metadata-runs">
      @if (loading()) {
        <div class="state"><mat-spinner diameter="28" /></div>
      } @else if (error()) {
        <p class="state error" role="alert">{{ error() }}</p>
      } @else {
        @if (status(); as st) {
          <section class="status" data-testid="runs-status">
            <mat-icon [class.on]="st.active">{{ st.active ? 'play_circle' : st.enabled ? 'pause_circle' : 'do_not_disturb_on' }}</mat-icon>
            <div>
              <strong>{{ st.active ? 'Matching in the background' : st.enabled ? 'Automatic matching is waiting' : 'Automatic matching is off' }}</strong>
              @if (st.waitingCode) {
                <p class="muted">{{ waiting(st.waitingCode) }}
                  @if (st.waitingUntil) { Until {{ st.waitingUntil | date: 'short' }}. }</p>
              }
              <p class="muted">{{ st.pending.toLocaleString('en-US') }} folder{{ st.pending === 1 ? '' : 's' }} queued.</p>
            </div>
            <div class="changed" matTooltip="Automatic links an admin later changed, unlinked or marked Don't match. Counted on this server only.">
              <span class="big" data-testid="runs-changed">{{ changedByAdmin() }}</span>
              <span class="muted">auto links changed by an admin</span>
            </div>
          </section>
        }

        @for (r of live(); track r.runId) {
          <section class="live" data-testid="run-live">
            <div class="live-head">
              <strong>{{ r.libraryName }}</strong>
              <span class="muted">{{ trigger(r) }} · started {{ r.startedAt | date: 'short' }}@if (r.reviewFirst) { · review everything once }</span>
              <span class="spacer"></span>
              <button mat-stroked-button type="button" [disabled]="cancelling() === r.runId" (click)="cancel(r)" data-testid="run-cancel">
                <mat-icon>stop</mat-icon> Cancel</button>
            </div>
            <mat-progress-bar mode="determinate" [value]="progress(r)" [attr.aria-label]="'Run progress ' + progress(r) + '%'" />
            <p class="counters">{{ r.processed }} of {{ r.queued }} done · {{ r.autoLinked }} auto-linked · {{ r.needsReview }} to review ·
              {{ r.unmatched }} unmatched · {{ r.requestsUsed }} requests</p>
          </section>
        }

        <h3>History</h3>
        <div class="table-wrap">
          <table class="history" data-testid="runs-history">
            <thead>
              <tr>
                <th>Started</th><th>Library</th><th>How</th><th>Status</th>
                <th class="n">Folders</th><th class="n">Auto</th><th class="n">Review</th><th class="n">Unmatched</th>
                <th class="n">Failed</th><th class="n">Requests</th><th class="n" matTooltip="Auto links an admin changed later">Changed</th>
              </tr>
            </thead>
            <tbody>
              @for (r of runs(); track r.runId) {
                <tr [class.running]="r.status === 'Running'">
                  <td>{{ r.startedAt | date: 'short' }}</td>
                  <td>{{ r.libraryName }}</td>
                  <td>{{ trigger(r) }}</td>
                  <td>{{ r.status === 'Running' ? 'Running' : r.status === 'Cancelled' ? 'Cancelled' : 'Done' }}</td>
                  <td class="n">{{ r.candidates }}</td>
                  <td class="n">{{ r.autoLinked }}</td>
                  <td class="n">{{ r.needsReview }}</td>
                  <td class="n">{{ r.unmatched }}</td>
                  <td class="n">{{ r.failed }}</td>
                  <td class="n">{{ r.requestsUsed }}</td>
                  <td class="n">{{ r.autoChangedByAdmin }}</td>
                </tr>
              } @empty {
                <tr><td colspan="11" class="muted empty">No runs yet. Turn on Automatic matching, or use "Match now" on a library in Settings.</td></tr>
              }
            </tbody>
          </table>
        </div>
        @if (cursor()) {
          <div class="more"><button mat-stroked-button type="button" (click)="load(true)">Load more</button></div>
        }
      }
    </div>
  `,
  styles: [`
    .status { display: flex; flex-wrap: wrap; align-items: center; gap: 14px; padding: 14px 16px; border-radius: 12px; background: #1c1c26;
      border: 1px solid rgba(255, 255, 255, 0.07); margin-bottom: 12px; }
    .status > mat-icon { font-size: 32px; width: 32px; height: 32px; color: #8a8a99; }
    .status > mat-icon.on { color: #81c784; }
    .status p { margin: 2px 0 0; }
    .changed { margin-left: auto; display: flex; flex-direction: column; align-items: flex-end; }
    .big { font-size: 24px; font-weight: 600; }
    .live { padding: 12px 16px; border-radius: 12px; background: #211d33; border: 1px solid #5e4b9c; margin-bottom: 12px; }
    .live-head { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin-bottom: 8px; }
    .spacer { flex: 1 1 auto; }
    .counters { font-size: 13px; margin: 8px 0 0; }
    h3 { font-size: 15px; font-weight: 500; margin: 16px 0 8px; }
    .table-wrap { overflow-x: auto; }
    .history { width: 100%; border-collapse: collapse; font-size: 13px; }
    .history th, .history td { text-align: left; padding: 6px 8px; border-bottom: 1px solid rgba(255, 255, 255, 0.06); white-space: nowrap; }
    .history th { color: #9a9aa8; font-weight: 500; }
    .history .n { text-align: right; font-variant-numeric: tabular-nums; }
    .history tr.running td { color: #d8ccff; }
    .empty { white-space: normal; text-align: center; padding: 20px; }
    .muted { color: #9a9aa8; font-size: 13px; }
    .state { display: flex; justify-content: center; padding: 32px 0; }
    .error { color: #ff8a80; }
    .more { display: flex; justify-content: center; margin: 12px 0; }
    @media (max-width: 599.98px) { .changed { margin-left: 0; align-items: flex-start; } }
  `],
})
export class MetadataRunsComponent implements OnInit {
  private readonly api = inject(MetadataApiService);
  private readonly reviewState = inject(MetadataReviewStateService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);

  readonly status = signal<MetadataAutoMatchStatusDto | null>(null);
  readonly runs = signal<MetadataMatchRunDto[]>([]);
  readonly cursor = signal<string | null>(null);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly cancelling = signal<string | null>(null);

  readonly live = computed(() => this.runs().filter((r) => r.status === 'Running'));
  readonly changedByAdmin = computed(() => this.runs().reduce((sum, r) => sum + (r.autoChangedByAdmin ?? 0), 0));

  readonly progress = runProgress;
  readonly waiting = waitingLabel;

  private poll: Subscription | null = null;

  ngOnInit(): void {
    this.load();
    this.destroyRef.onDestroy(() => this.poll?.unsubscribe());
  }

  trigger(run: MetadataMatchRunDto): string {
    return RUN_TRIGGER_LABELS[run.trigger] ?? run.trigger;
  }

  load(more = false): void {
    if (!more) this.loading.set(this.runs().length === 0 && !this.error());
    this.api.getRuns(null, more ? this.cursor() : null).subscribe({
      next: (page) => {
        this.status.set(page.status);
        this.runs.update((prev) => (more ? [...prev, ...page.items] : page.items));
        if (!more) this.cursor.set(page.hasMore || page.nextCursor ? page.nextCursor ?? null : null);
        else this.cursor.set(page.nextCursor ?? null);
        this.error.set(null);
        this.loading.set(false);
        this.schedulePoll();
      },
      error: (err: ApiError & { status?: number }) => {
        this.loading.set(false);
        this.error.set(err?.status === 501 ? 'Runs are not available on this server yet.' : err?.message || 'Runs could not be loaded.');
      },
    });
  }

  cancel(run: MetadataMatchRunDto): void {
    this.cancelling.set(run.runId);
    this.api.cancelRun(run.runId).subscribe({
      next: (updated) => {
        this.cancelling.set(null);
        this.runs.update((list) => list.map((r) => (r.runId === run.runId ? updated : r)));
        this.reviewState.refresh();
        this.snackBar.open(`Run for ${run.libraryName} cancelled`, 'Close', { duration: 3000 });
      },
      error: (err: ApiError) => {
        this.cancelling.set(null);
        this.snackBar.open(`Failed: ${err?.message ?? 'error'}`, 'Close', { duration: 5000 });
      },
    });
  }

  /** One poll after RUNS_POLL_MS while a run is live (re-armed by each load). */
  private schedulePoll(): void {
    this.poll?.unsubscribe();
    this.poll = null;
    if (this.live().length === 0) return;
    this.poll = timer(RUNS_POLL_MS).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
      this.load();
      this.reviewState.refresh();
    });
  }
}
