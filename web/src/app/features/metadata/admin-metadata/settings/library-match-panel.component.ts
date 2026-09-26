import { ChangeDetectionStrategy, Component, OnInit, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';

import { ApiError, MetadataMatchEstimateDto, MetadataMatchRunDto } from '../../../../core/api/api-types';
import { MetadataApiService } from '../../metadata-api.service';
import { daysLabel, plural, waitingLabel } from '../metadata-admin-labels';

/**
 * "Match this library now" (stage 2, decision 1) for one library: the local estimate
 * (candidate folders, requests, days at the current budget, already linked), the
 * optional "review everything once" for a library's first run, and Start. Loading the
 * estimate makes no provider request; Start only queues work for the background worker,
 * which sends the requests inside the automatic gate and the one daily budget.
 */
@Component({
  selector: 'app-library-match-panel',
  standalone: true,
  imports: [MatButtonModule, MatCheckboxModule, MatIconModule, MatProgressSpinnerModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="panel" data-testid="match-panel">
      @if (loading()) {
        <mat-spinner diameter="20" />
      } @else if (estimate(); as e) {
        <p class="figures" data-testid="match-estimate">
          <strong>{{ plural(e.candidates, 'folder') }}</strong> to match ·
          about {{ plural(e.estimatedRequests, 'request') }} ·
          {{ days(e.estimatedDays) }} at {{ e.dailyBudget.toLocaleString('en-US') }} a day ·
          {{ e.alreadyLinked.toLocaleString('en-US') }} already linked
          @if (e.unmatched) { · {{ e.unmatched.toLocaleString('en-US') }} unmatched }
        </p>
        <p class="note">Today {{ e.budgetUsedToday.toLocaleString('en-US') }} of {{ e.dailyBudget.toLocaleString('en-US') }} requests are used.
          The requests come out of the one daily budget; Identify waits too once it is spent.</p>
        <mat-checkbox [checked]="retryUnmatched()" (change)="setRetry($event.checked)" data-testid="match-retry">
          Also retry folders that found no match before
        </mat-checkbox>
        @if (e.firstRun) {
          <mat-checkbox [checked]="reviewFirst()" (change)="reviewFirst.set($event.checked)" data-testid="match-review-first">
            Review everything once - nothing goes live until you accept it in Review
          </mat-checkbox>
        }
        @if (!e.automaticAvailable) {
          <p class="warn" data-testid="match-unavailable"><mat-icon inline>info</mat-icon> {{ unavailable(e.unavailableCode) }}</p>
        }
        <div class="actions">
          <button mat-flat-button type="button" [disabled]="starting() || !e.automaticAvailable || e.candidates === 0"
                  (click)="start()" data-testid="match-start">Start matching</button>
          <button mat-button type="button" (click)="closed.emit()">Cancel</button>
        </div>
      }
      @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
    </div>
  `,
  styles: [`
    .panel { padding: 10px 12px; margin: 4px 0 8px; border-radius: 8px; background: rgba(255, 255, 255, 0.04); display: flex; flex-direction: column; gap: 4px; }
    .figures { margin: 0; font-size: 14px; }
    .note { margin: 0 0 4px; font-size: 12px; color: #9a9aa8; }
    .warn { color: #ffb300; font-size: 13px; margin: 4px 0; }
    .error { color: #f44336; font-size: 13px; }
    .actions { display: flex; gap: 8px; margin-top: 6px; }
  `],
})
export class LibraryMatchPanelComponent implements OnInit {
  private readonly api = inject(MetadataApiService);

  readonly libraryId = input.required<string>();
  readonly started = output<MetadataMatchRunDto>();
  readonly closed = output<void>();

  readonly loading = signal(true);
  readonly starting = signal(false);
  readonly estimate = signal<MetadataMatchEstimateDto | null>(null);
  readonly error = signal<string | null>(null);
  readonly retryUnmatched = signal(false);
  readonly reviewFirst = signal(false);

  readonly plural = plural;
  readonly days = daysLabel;

  ngOnInit(): void {
    this.load();
  }

  setRetry(on: boolean): void {
    this.retryUnmatched.set(on);
    this.load();
  }

  unavailable(code: string | null | undefined): string {
    return waitingLabel(code) || 'Automatic matching is not available for this library.';
  }

  start(): void {
    const e = this.estimate();
    if (!e) return;
    this.starting.set(true);
    this.error.set(null);
    this.api.matchLibrary(this.libraryId(), {
      reviewFirst: e.firstRun && this.reviewFirst(),
      retryUnmatched: this.retryUnmatched(),
    }).subscribe({
      next: (run) => {
        this.starting.set(false);
        this.started.emit(run);
      },
      error: (err: ApiError) => {
        this.starting.set(false);
        this.error.set(err?.message || 'Matching could not be started.');
      },
    });
  }

  private load(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.getMatchEstimate(this.libraryId(), this.retryUnmatched()).subscribe({
      next: (e) => {
        this.estimate.set(e);
        this.loading.set(false);
      },
      error: (err: ApiError) => {
        this.loading.set(false);
        this.error.set(err?.message || 'The estimate is not available.');
      },
    });
  }
}
