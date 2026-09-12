import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatSlideToggleModule, MatSlideToggleChange } from '@angular/material/slide-toggle';

import { ApiService } from '../../core/api/api.service';
import { ApiError, UserPreferencesDto } from '../../core/api/api-types';

/**
 * Reading preferences card (1.9.0). Surfaces the "Always open read archives from the
 * start" toggle on the Settings screen.
 *
 * The toggle is a NON-DESTRUCTIVE, per-user preference: it only changes where an
 * already-READ archive reopens (the server resolves this at open time into
 * `ReadingProgressDto.openPageIndex`). When off (default), read titles reopen where
 * the reader left off; titles finished on the last page still start from page 1.
 * Unread/in-progress titles always resume and are unaffected.
 *
 * A new standalone component (kept out of the password/private-libraries
 * SettingsComponent) so the reading preference round-trip lives on its own. The full
 * preferences DTO is loaded first and echoed back on save, so the other reader
 * preferences the server persists are never clobbered by this single toggle.
 */
@Component({
  selector: 'app-reading-preferences-card',
  standalone: true,
  imports: [MatCardModule, MatSlideToggleModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-card>
      <mat-card-header>
        <mat-card-title>Reading</mat-card-title>
      </mat-card-header>
      <mat-card-content>
        @if (loaded()) {
          <mat-slide-toggle
            [checked]="alwaysOpenReadFromStart()"
            [disabled]="saving()"
            (change)="toggle($event)">
            Always open read archives from the start
          </mat-slide-toggle>
          <p class="hint">
            When off, read titles reopen where you left off; ones you finished still
            start from the first page.
          </p>
          @if (error()) {
            <div class="error">{{ error() }}</div>
          }
        } @else {
          <p class="muted">Loading…</p>
        }
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    mat-card { max-width: 600px; margin: 24px auto 0; }
    .hint { color: #999; font-size: 13px; margin: 10px 0 0; }
    .muted { color: #999; font-size: 14px; }
    .error { color: #f44336; font-size: 14px; margin-top: 8px; }
  `],
})
export class ReadingPreferencesCardComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly loaded = signal(false);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly alwaysOpenReadFromStart = signal(false);

  /** The last-loaded preferences, echoed back on save so other fields are preserved. */
  private prefs: UserPreferencesDto | null = null;

  ngOnInit(): void {
    this.api.getPreferences().subscribe({
      next: (p) => {
        this.prefs = p;
        this.alwaysOpenReadFromStart.set(!!p.alwaysOpenReadFromStart);
        this.loaded.set(true);
      },
      error: () => this.loaded.set(true), // show the toggle at its default (off)
    });
  }

  toggle(change: MatSlideToggleChange): void {
    const previous = this.alwaysOpenReadFromStart();
    const next = change.checked;
    this.alwaysOpenReadFromStart.set(next);
    this.saving.set(true);
    this.error.set(null);

    const body: UserPreferencesDto = { ...(this.prefs ?? {} as UserPreferencesDto), alwaysOpenReadFromStart: next };
    this.api.setPreferences(body).subscribe({
      next: () => { this.prefs = body; this.saving.set(false); },
      error: (err: ApiError) => {
        this.alwaysOpenReadFromStart.set(previous);
        this.saving.set(false);
        this.error.set(err.message || 'Failed to save reading preference');
      },
    });
  }
}
