import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectChange, MatSelectModule } from '@angular/material/select';

import { ApiService } from '../../core/api/api.service';
import { ApiError, LogCategoryLevelDto, LogLevel, LogLevelDto } from '../../core/api/api-types';

/** Select value meaning "no override - follow the global level". */
export const INHERIT = '';

/**
 * Debug log UI (1.17.0): per-category runtime log-level control for admins.
 *
 * The backend shipped in 1.6.0 ("Debug Log Taming"): GET/PUT
 * /api/v1/operations/logging exposes a fixed catalog of debug categories
 * (Scanning, Media, Reading), each with an effective level and an `inherited`
 * flag. This card lists them and lets an admin set or clear ONE category's
 * override at a time, so a single noisy subsystem can be raised to Debug
 * without turning on the global firehose.
 *
 * The PUT is a partial update: only `categories` is sent, so the global level
 * is never touched from here (it stays owned by the Diagnostics card in
 * AdminComponent). A null category level clears the override. The response
 * carries the full live state, which replaces the local state - the UI shows
 * what the server actually applied, not an optimistic guess.
 *
 * Standalone and self-loading so it works both as a routed page and embedded
 * as `<app-debug-log-card />` inside the admin screen.
 */
@Component({
  selector: 'app-debug-log-card',
  standalone: true,
  imports: [MatCardModule, MatFormFieldModule, MatIconModule, MatSelectModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-card>
      <mat-card-header>
        <mat-card-title>Debug Logging</mat-card-title>
        <mat-card-subtitle>Per-category log level</mat-card-subtitle>
      </mat-card-header>
      <mat-card-content>
        @if (loading()) {
          <p class="muted">Loading…</p>
        } @else if (loadFailed()) {
          <div class="error" role="alert">{{ error() }}</div>
        } @else {
          <p class="global">Global level: <strong>{{ globalLevel() }}</strong></p>
          @for (cat of categories(); track cat.name) {
            <div class="category-row" [attr.data-category]="cat.name">
              <mat-form-field appearance="fill" subscriptSizing="dynamic">
                <mat-label>{{ cat.name }}</mat-label>
                <mat-select
                  [value]="selectValue(cat)"
                  [disabled]="isSaving(cat.name)"
                  (selectionChange)="setCategoryLevel(cat, $event)">
                  <mat-option [value]="inherit">Inherit ({{ globalLevel() }})</mat-option>
                  @for (level of levels; track level) {
                    <mat-option [value]="level">{{ level }}</mat-option>
                  }
                </mat-select>
              </mat-form-field>
              <span class="state" [class.override]="!cat.inherited">
                {{ cat.inherited ? 'Inherited' : 'Override' }}
              </span>
            </div>
          } @empty {
            <p class="muted">This server exposes no debug categories.</p>
          }
          @if (error()) {
            <div class="error" role="alert">{{ error() }}</div>
          }
          <p class="hint">
            <mat-icon inline>info</mat-icon>
            Ephemeral - overrides reset on restart. A category set to Inherit follows the global level.
          </p>
        }
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    mat-card { max-width: 600px; margin: 24px auto 0; }
    .global { font-size: 14px; margin: 0 0 12px; }
    .category-row { display: flex; align-items: center; gap: 16px; margin-bottom: 12px; }
    .category-row mat-form-field { flex: 1 1 auto; }
    .state { flex: 0 0 72px; font-size: 13px; color: #999; }
    .state.override { color: #ffb300; font-weight: 500; }
    .hint { color: #999; font-size: 13px; margin: 4px 0 0; }
    .muted { color: #999; font-size: 14px; }
    .error { color: #f44336; font-size: 14px; margin: 8px 0; }
  `],
})
export class DebugLogCardComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly inherit = INHERIT;
  readonly levels: LogLevel[] = ['Verbose', 'Debug', 'Information', 'Warning', 'Error', 'Fatal'];

  readonly loading = signal(true);
  readonly loadFailed = signal(false);
  readonly error = signal<string | null>(null);
  readonly globalLevel = signal('Information');
  readonly categories = signal<LogCategoryLevelDto[]>([]);
  /** Names of categories with a PUT in flight (per-row double-submit guard). */
  private readonly saving = signal<Set<string>>(new Set());

  ngOnInit(): void {
    this.api.getLoggingLevel().subscribe({
      next: (dto) => {
        this.apply(dto);
        this.loading.set(false);
      },
      error: (err: ApiError) => {
        this.error.set(err?.message || 'Failed to load log levels');
        this.loadFailed.set(true);
        this.loading.set(false);
      },
    });
  }

  /** The select's value for a category: its override level, or INHERIT when none. */
  selectValue(cat: LogCategoryLevelDto): string {
    return cat.inherited ? INHERIT : cat.level;
  }

  isSaving(name: string): boolean {
    return this.saving().has(name);
  }

  setCategoryLevel(cat: LogCategoryLevelDto, change: MatSelectChange): void {
    if (this.isSaving(cat.name)) return;
    const previous = this.selectValue(cat);
    const next = change.value as string;
    if (next === previous) return;

    this.markSaving(cat.name, true);
    this.error.set(null);
    this.api.setLoggingCategories([{ name: cat.name, level: next === INHERIT ? null : next }]).subscribe({
      next: (dto) => {
        this.apply(dto);
        this.markSaving(cat.name, false);
      },
      error: (err: ApiError) => {
        // The model never changed, so the [value] binding will not re-push;
        // put the select itself back to what the server still has.
        if (change.source) change.source.value = previous;
        this.markSaving(cat.name, false);
        this.error.set(err?.message || `Failed to set ${cat.name} log level`);
      },
    });
  }

  private apply(dto: LogLevelDto): void {
    this.globalLevel.set(dto.level);
    this.categories.set(dto.categories ?? []);
  }

  private markSaving(name: string, on: boolean): void {
    const next = new Set(this.saving());
    if (on) next.add(name); else next.delete(name);
    this.saving.set(next);
  }
}
