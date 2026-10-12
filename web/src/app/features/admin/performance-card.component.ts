import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';

import { ApiService } from '../../core/api/api.service';
import { ApiError, PerformanceSettingsDto, UpdatePerformanceSettingsRequest } from '../../core/api/api-types';

const GiB = 1024 * 1024 * 1024;
const MiB = 1024 * 1024;

/** The page cache sizes offered (bytes); the stored or configured value is added when it is not one of them. */
const CACHE_STEPS = [256 * MiB, 512 * MiB, GiB, 2 * GiB, 4 * GiB, 8 * GiB, 16 * GiB, 32 * GiB, 64 * GiB];

/** Human size: "512 MB", "1 GB", "1.5 GB" (binary units, written as MB / GB like the rest of the admin page). */
export function formatBytes(bytes: number): string {
  if (bytes >= GiB) {
    const g = bytes / GiB;
    return `${Number.isInteger(g) ? g : g.toFixed(1)} GB`;
  }
  return `${Math.round(bytes / MiB)} MB`;
}

interface BackgroundOption {
  value: string;
  label: string;
  hint: string;
}

const BACKGROUND_OPTIONS: BackgroundOption[] = [
  { value: 'pause', label: 'Pause', hint: 'No background work while someone reads; it resumes 30 seconds after the last page.' },
  { value: 'slow', label: 'Slow', hint: 'Background work keeps one worker while someone reads; readers get the rest first.' },
  { value: 'normal', label: 'Normal', hint: 'Background work may use every worker but one while someone reads.' },
];

/**
 * Performance card (1.42.0): the media workers, the page cache size and what background work (analysis, cover
 * thumbnails, cover look-ups) may do while someone reads. Every change applies at once, without a restart. A value
 * set in the server configuration is shown as such and cannot be changed here (configuration wins over this card,
 * which wins over the default). Choosing the default option clears the stored value, so the card follows a later
 * change of the default.
 */
@Component({
  selector: 'app-performance-card',
  standalone: true,
  imports: [MatCardModule, MatFormFieldModule, MatSelectModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-card class="performance-card" data-testid="performance-card">
      <mat-card-header>
        <mat-card-title>Performance</mat-card-title>
        <mat-card-subtitle>Workers, page cache and background work</mat-card-subtitle>
      </mat-card-header>
      <mat-card-content>
        @if (loading()) {
          <p class="muted">Loading…</p>
        } @else if (settings(); as s) {
          <div class="field">
            <mat-form-field appearance="fill" subscriptSizing="dynamic">
              <mat-label>Media workers</mat-label>
              <mat-select data-testid="perf-workers" [value]="s.mediaWorkers"
                          [disabled]="saving() || s.mediaWorkersSource === 'configuration'"
                          (selectionChange)="setWorkers($event.value)">
                @for (n of workerOptions(); track n) {
                  <mat-option [value]="n">{{ n }}{{ n === s.mediaWorkersDefault ? ' (default)' : '' }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
            <p class="hint">
              @if (s.mediaWorkersSource === 'configuration') {
                <span class="pinned">Set by the server configuration.</span>
              }
              Pages and covers are prepared by this many worker processes at once. The default is a quarter of the
              server's {{ s.processorCount }} processors, between 2 and 4. Each worker uses some memory.
            </p>
          </div>

          <div class="field">
            <mat-form-field appearance="fill" subscriptSizing="dynamic">
              <mat-label>Page cache size</mat-label>
              <mat-select data-testid="perf-cache" [value]="s.pageCacheBytes"
                          [disabled]="saving() || s.pageCacheSource === 'configuration'"
                          (selectionChange)="setCache($event.value)">
                @for (b of cacheOptions(); track b) {
                  <mat-option [value]="b">{{ size(b) }}{{ b === s.pageCacheDefaultBytes ? ' (default)' : '' }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
            <p class="hint">
              @if (s.pageCacheSource === 'configuration') {
                <span class="pinned">Set by the server configuration.</span>
              }
              Prepared pages are kept for quick re-reading; now {{ size(s.pageCacheUsageBytes) }} used. A smaller size
              frees space at once. The cache starts empty after every restart.
            </p>
          </div>

          <div class="field">
            <mat-form-field appearance="fill" subscriptSizing="dynamic">
              <mat-label>Background work while someone reads</mat-label>
              <mat-select data-testid="perf-background" [value]="s.backgroundWhileReading"
                          [disabled]="saving() || s.backgroundWhileReadingSource === 'configuration'"
                          (selectionChange)="setBackground($event.value)">
                @for (o of backgroundOptions; track o.value) {
                  <mat-option [value]="o.value">{{ o.label }}{{ o.value === s.backgroundWhileReadingDefault ? ' (default)' : '' }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
            <p class="hint">
              @if (s.backgroundWhileReadingSource === 'configuration') {
                <span class="pinned">Set by the server configuration.</span>
              }
              {{ backgroundHint() }} Pages someone is reading always come first.
            </p>
          </div>

          @if (saved()) {
            <p class="saved" role="status">Saved. Applies now.</p>
          }
        }
        @if (error(); as e) {
          <div class="error" role="alert">{{ e }}</div>
        }
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    mat-card { margin: 0; }
    .field { margin-bottom: 12px; min-width: 0; }
    mat-form-field { width: 100%; max-width: 360px; }
    .hint { color: var(--mp-text-muted); font-size: 13px; margin: 4px 0 0; }
    .pinned { color: var(--mp-warn-strong); font-weight: 500; }
    .muted { color: var(--mp-text-muted); font-size: 14px; }
    .saved { color: var(--mp-success-strong); font-size: 13px; margin: 4px 0 0; }
    .error { color: var(--mp-error-strong); font-size: 14px; margin: 8px 0; }
  `],
})
export class PerformanceCardComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly loading = signal(true);
  readonly saving = signal(false);
  readonly saved = signal(false);
  readonly error = signal<string | null>(null);
  readonly settings = signal<PerformanceSettingsDto | null>(null);
  readonly backgroundOptions = BACKGROUND_OPTIONS;

  readonly workerOptions = computed(() => {
    const s = this.settings();
    if (!s) return [];
    const options: number[] = [];
    for (let n = s.mediaWorkersMin; n <= s.mediaWorkersMax; n++) options.push(n);
    if (!options.includes(s.mediaWorkers)) options.push(s.mediaWorkers); // a configured value outside the range
    return options.sort((a, b) => a - b);
  });

  readonly cacheOptions = computed(() => {
    const s = this.settings();
    if (!s) return [];
    const options = CACHE_STEPS.filter((b) => b >= s.pageCacheMinBytes && b <= s.pageCacheMaxBytes);
    if (!options.includes(s.pageCacheBytes)) options.push(s.pageCacheBytes);
    return options.sort((a, b) => a - b);
  });

  readonly backgroundHint = computed(() =>
    BACKGROUND_OPTIONS.find((o) => o.value === this.settings()?.backgroundWhileReading)?.hint ?? '');

  ngOnInit(): void {
    this.api.getPerformanceSettings().subscribe({
      next: (s) => {
        this.settings.set(s);
        this.loading.set(false);
      },
      error: (err: ApiError) => {
        this.error.set(err?.message || 'Failed to load the performance settings');
        this.loading.set(false);
      },
    });
  }

  size(bytes: number): string {
    return formatBytes(bytes);
  }

  setWorkers(value: number): void {
    const s = this.settings();
    if (!s) return;
    this.save(value === s.mediaWorkersDefault ? { reset: ['mediaWorkers'] } : { mediaWorkers: value });
  }

  setCache(value: number): void {
    const s = this.settings();
    if (!s) return;
    this.save(value === s.pageCacheDefaultBytes ? { reset: ['pageCacheBytes'] } : { pageCacheBytes: value });
  }

  setBackground(value: string): void {
    const s = this.settings();
    if (!s) return;
    this.save(value === s.backgroundWhileReadingDefault ? { reset: ['backgroundWhileReading'] } : { backgroundWhileReading: value });
  }

  private save(request: UpdatePerformanceSettingsRequest): void {
    this.saving.set(true);
    this.saved.set(false);
    this.error.set(null);
    this.api.updatePerformanceSettings(request).subscribe({
      next: (s) => {
        this.settings.set(s);
        this.saving.set(false);
        this.saved.set(true);
      },
      error: (err: ApiError) => {
        this.saving.set(false);
        this.error.set(err?.message || 'Failed to save the performance settings');
      },
    });
  }
}
