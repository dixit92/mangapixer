import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Subscription, timer } from 'rxjs';

import { ApiError, AuthorAliasRunDto, AuthorAliasStatusDto, MetadataSettingsDto } from '../../../core/api/api-types';
import { AuthorAliasesApiService } from './author-aliases-api.service';

/** How often the card asks for progress while a look-up runs. */
export const AUTHOR_ALIAS_POLL_MS = 2000;

/** "about 25 s" / "about 3 min" / "about 1 h 5 min" for N requests at the given pace. */
export function lookupDuration(requests: number, secondsPerRequest: number): string {
  const seconds = Math.max(0, requests) * Math.max(1, secondsPerRequest);
  if (seconds < 90) return `about ${seconds} s`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `about ${minutes} min`;
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  return rest ? `about ${hours} h ${rest} min` : `about ${hours} h`;
}

/** Why a look-up cannot start, in words (the server's refusal code). */
export function blockedLabel(code: string | null | undefined): string | null {
  switch (code) {
    case null:
    case undefined:
      return null;
    case 'metadata_disabled':
      return 'Turn on "Fetch from the web" (and accept the current consent) to look them up.';
    case 'metadata_network_disabled':
      return 'Web lookups are disabled by the server configuration.';
    case 'provider_not_allowed':
      return 'MangaUpdates is not on the allowed sites.';
    case 'library_metadata_disabled':
      return 'No library whose Fetch switch is on links a MangaUpdates series.';
    case 'budget_exhausted':
      return "Today's request budget is used up. It resets at midnight server time.";
    case 'provider_backoff':
      return 'MangaUpdates asked us to slow down. Try again later.';
    default:
      return 'A look-up cannot start right now.';
  }
}

/** How a look-up ended, in words. */
export function outcomeLabel(run: AuthorAliasRunDto): string {
  switch (run.outcome) {
    case 'completed':
      return 'finished';
    case 'cancelled':
      return 'cancelled - the rest stay to look up';
    case 'budget_exhausted':
      return "stopped: today's request budget is used up - the rest stay to look up";
    case 'provider_backoff':
      return 'stopped: MangaUpdates asked us to slow down - the rest stay to look up';
    case 'switched_off':
      return 'stopped: web lookups were switched off';
    case 'running':
      return 'running';
    default:
      return 'stopped by an error';
  }
}

/**
 * Metadata Manager > Settings "Artists' other names" card (1.38.0): the pen names, other spellings and other scripts of the
 * creators of linked series, read from their MangaUpdates author records - ONLY when an admin presses "Look up the rest" (one
 * request per second, counted in the daily budget, sending only MangaUpdates author numbers). Match folders by name reads the
 * stored names. Shows "<fetched> of <eligible> known authors fetched", the button with the number of requests and the time it
 * takes, progress with Cancel while it runs, and the last result (counts). Reloads when the page's settings change.
 */
@Component({
  selector: 'app-author-aliases-card',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatProgressBarModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="card" aria-labelledby="md-authors-h" data-testid="md-authors">
      <h3 id="md-authors-h"><mat-icon aria-hidden="true">badge</mat-icon> Artists' other names</h3>
      <p class="note">Pen names, other spellings and other scripts of the creators of your linked series, from their MangaUpdates
        author pages. <strong>Match folders by name</strong> uses them to recognise an artist's folder. Looked up only when you ask:
        one request per second, counted in the daily budget, sending only MangaUpdates author numbers.</p>
      @if (status(); as st) {
        <p class="status" data-testid="md-authors-status">
          {{ st.fetched }} of {{ st.eligible }} known author{{ st.eligible === 1 ? '' : 's' }} fetched
          @if (st.withOtherNames > 0) { <span class="muted">· {{ st.withOtherNames }} with other names</span> }
        </p>
        @if (st.running; as run) {
          <mat-progress-bar mode="determinate" [value]="percent(run)" aria-label="Author look-up progress" />
          <div class="row">
            <span class="small" data-testid="md-authors-progress">Looking up… {{ run.requests }} of {{ run.total }}</span>
            <button mat-stroked-button type="button" [disabled]="busy()" (click)="cancel()" data-testid="md-authors-cancel">Cancel</button>
          </div>
        } @else if (blocked(); as why) {
          <p class="muted small" data-testid="md-authors-blocked">{{ why }}</p>
        } @else if (st.toFetch > 0) {
          <button mat-stroked-button type="button" [disabled]="busy()" (click)="start()" data-testid="md-authors-start">
            <mat-icon>travel_explore</mat-icon> Look up the rest</button>
          <p class="muted small">{{ st.toFetch }} request{{ st.toFetch === 1 ? '' : 's' }}, {{ duration(st) }}.</p>
        } @else if (st.eligible === 0) {
          <p class="muted small">No linked MangaUpdates series names an author yet.</p>
        } @else {
          <p class="muted small">All known authors are looked up; each is asked again after {{ st.refreshAfterDays }} days.</p>
        }
        @if (st.lastRun; as last) {
          @if (!st.running) {
            <p class="small" data-testid="md-authors-last">Last look-up {{ outcome(last) }}: {{ last.stored }} found, {{ last.notFound }} not on
              MangaUpdates{{ last.failed ? ', ' + last.failed + ' failed' : '' }} ({{ last.requests }} request{{ last.requests === 1 ? '' : 's' }}).</p>
          }
        }
      } @else if (!error()) {
        <p class="muted small">Loading…</p>
      }
      @if (error()) { <p class="error small" role="alert" data-testid="md-authors-error">{{ error() }}</p> }
    </section>
  `,
  styles: [`
    :host { display: contents; }
    .card { background: #1c1c26; border: 1px solid rgba(255, 255, 255, 0.07); border-radius: 12px; padding: 14px 18px; min-width: 0; }
    h3 { display: flex; align-items: center; gap: 8px; margin: 0 0 10px; font-size: 16px; font-weight: 500; }
    h3 mat-icon { font-size: 20px; width: 20px; height: 20px; color: #b39dff; }
    .note { font-size: 12px; color: #9a9aa8; margin: 4px 0 8px; }
    .status { margin: 8px 0; }
    .row { display: flex; align-items: center; flex-wrap: wrap; gap: 8px 12px; margin-top: 8px; }
    .small { font-size: 12px; }
    .muted { color: #9a9aa8; }
    .error { color: #f44336; }
    mat-progress-bar { margin-top: 4px; }
    @media (max-width: 599.98px) { .card { padding: 12px; } }
  `],
})
export class AuthorAliasesCardComponent {
  private readonly api = inject(AuthorAliasesApiService);
  private poll: Subscription | null = null;

  /** The page's settings: a change (Fetch, consent, allowlist, a library) reloads the status. */
  readonly settings = input<MetadataSettingsDto | null>(null);

  readonly status = signal<AuthorAliasStatusDto | null>(null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly blocked = computed(() => blockedLabel(this.status()?.blockedReason));

  constructor() {
    effect(() => {
      this.settings();
      untracked(() => this.load());
    });
    inject(DestroyRef).onDestroy(() => this.stopPolling());
  }

  percent(run: AuthorAliasRunDto): number {
    return run.total > 0 ? Math.min(100, Math.round((run.requests / run.total) * 100)) : 0;
  }

  duration(st: AuthorAliasStatusDto): string {
    return lookupDuration(st.toFetch, st.secondsPerRequest);
  }

  outcome(run: AuthorAliasRunDto): string {
    return outcomeLabel(run);
  }

  load(): void {
    this.api.status().subscribe({
      next: (s) => this.show(s),
      error: (e: ApiError) => this.error.set(e.message || 'The status could not be loaded.'),
    });
  }

  start(): void {
    this.busy.set(true);
    this.error.set(null);
    this.api.start().subscribe({
      next: (s) => {
        this.busy.set(false);
        this.show(s);
      },
      error: (e: ApiError) => {
        this.busy.set(false);
        this.error.set(e.message || 'The look-up could not start.');
        this.load();
      },
    });
  }

  cancel(): void {
    this.busy.set(true);
    this.api.cancel().subscribe({
      next: (s) => {
        this.busy.set(false);
        this.show(s);
      },
      error: (e: ApiError) => {
        this.busy.set(false);
        this.error.set(e.message || 'The look-up could not be cancelled.');
      },
    });
  }

  private show(s: AuthorAliasStatusDto): void {
    this.status.set(s);
    if (s.running) this.startPolling();
    else this.stopPolling();
  }

  private startPolling(): void {
    if (this.poll) return;
    this.poll = timer(AUTHOR_ALIAS_POLL_MS, AUTHOR_ALIAS_POLL_MS).subscribe(() => {
      this.api.status().subscribe({ next: (s) => this.show(s), error: () => undefined });
    });
  }

  private stopPolling(): void {
    this.poll?.unsubscribe();
    this.poll = null;
  }
}
