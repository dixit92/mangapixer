import { ChangeDetectionStrategy, Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';

import { MetadataSettingsDto } from '../../../core/api/api-types';
import { MetadataApiService } from '../metadata-api.service';
import { MetadataReviewStateService } from '../metadata-review-state.service';
import { AdminMetadataTab } from '../admin-metadata/metadata-admin-labels';

/**
 * Metadata Manager summary tile (owner decision 2, 1.27.0). Moved from a link-out card
 * on the main admin page to the TOP of `/admin/metadata` itself, above the tabs, where
 * it replaces the page's old one-line summary so nothing is shown twice - `inPage` (set
 * there) makes its four stats switch that page's own tabs in place (`tabSelect`:
 * to-review/flags go to Review/Flags, the budget goes to Settings, automatic matching
 * goes to Runs) instead of navigating. The main admin page no longer shows this tile at
 * all (admins reach the page from the account menu, which keeps its own attention
 * badge). The component stays reusable as a link-out card (`inPage` false, the default)
 * for anywhere else that might want one. Local reads only (settings + review summary);
 * a server without stage 2 shows dashes.
 */
@Component({
  selector: 'app-metadata-summary-tile',
  standalone: true,
  imports: [RouterLink, MatButtonModule, MatCardModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-card data-testid="metadata-summary-tile" [class.in-page]="inPage()">
      @if (!inPage()) {
        <mat-card-header>
          <mat-card-title>Metadata Manager</mat-card-title>
          <mat-card-subtitle>Series information from ComicInfo.xml and, if you allow it, MangaUpdates</mat-card-subtitle>
        </mat-card-header>
      }
      <mat-card-content>
        <div class="stats">
          @if (inPage()) {
            <button type="button" class="stat" [class.hot]="needsReview() > 0" (click)="tabSelect.emit('review')" data-testid="tile-review">
              <span class="value">{{ summary() ? needsReview() : '–' }}</span>
              <span class="label">to review</span>
            </button>
            <button type="button" class="stat" [class.hot]="openFlags() > 0" (click)="tabSelect.emit('flags')" data-testid="tile-flags">
              <span class="value">{{ summary() ? openFlags() : '–' }}</span>
              <span class="label">open flag{{ openFlags() === 1 ? '' : 's' }}</span>
            </button>
            <button type="button" class="stat" (click)="tabSelect.emit('settings')" data-testid="tile-budget">
              <span class="value">{{ settings() ? settings()!.budgetUsedToday.toLocaleString('en-US') : '–' }}<span class="of">
                / {{ settings() ? settings()!.dailyBudget.toLocaleString('en-US') : '–' }}</span></span>
              <span class="label">requests today</span>
            </button>
            <button type="button" class="stat" (click)="tabSelect.emit('runs')" data-testid="tile-auto">
              <span class="value" [class.on]="autoOn()">{{ settings() ? (autoOn() ? 'On' : 'Off') : '–' }}</span>
              <span class="label">automatic matching</span>
            </button>
          } @else {
            <a class="stat" routerLink="/admin/metadata" [queryParams]="{ tab: 'review' }" [class.hot]="needsReview() > 0"
               data-testid="tile-review">
              <span class="value">{{ summary() ? needsReview() : '–' }}</span>
              <span class="label">to review</span>
            </a>
            <a class="stat" routerLink="/admin/metadata" [queryParams]="{ tab: 'flags' }" [class.hot]="openFlags() > 0"
               data-testid="tile-flags">
              <span class="value">{{ summary() ? openFlags() : '–' }}</span>
              <span class="label">open flag{{ openFlags() === 1 ? '' : 's' }}</span>
            </a>
            <a class="stat" routerLink="/admin/metadata" data-testid="tile-budget">
              <span class="value">{{ settings() ? settings()!.budgetUsedToday.toLocaleString('en-US') : '–' }}<span class="of">
                / {{ settings() ? settings()!.dailyBudget.toLocaleString('en-US') : '–' }}</span></span>
              <span class="label">requests today</span>
            </a>
            <a class="stat" routerLink="/admin/metadata" [queryParams]="{ tab: 'runs' }" data-testid="tile-auto">
              <span class="value" [class.on]="autoOn()">{{ settings() ? (autoOn() ? 'On' : 'Off') : '–' }}</span>
              <span class="label">automatic matching</span>
            </a>
          }
        </div>
      </mat-card-content>
      @if (!inPage()) {
        <mat-card-actions align="end">
          <a mat-flat-button routerLink="/admin/metadata" data-testid="tile-open">
            <mat-icon>tune</mat-icon> Open Metadata Manager
          </a>
        </mat-card-actions>
      }
    </mat-card>
  `,
  styles: [`
    mat-card { margin-bottom: 16px; }
    mat-card.in-page { margin-bottom: 20px; }
    .stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 10px; margin-top: 8px; }
    .in-page .stats { margin-top: 0; }
    .stat { display: flex; flex-direction: column; gap: 2px; padding: 12px 14px; border-radius: 10px; text-decoration: none; color: inherit;
      background: rgba(255, 255, 255, 0.04); border: 1px solid rgba(255, 255, 255, 0.06); font: inherit; text-align: left; cursor: pointer; }
    .stat:hover { background: rgba(255, 255, 255, 0.07); }
    .stat:focus-visible { outline: 2px solid #b39dff; }
    .stat.hot { border-color: #7c4dff; }
    .value { font-size: 22px; font-weight: 600; font-variant-numeric: tabular-nums; }
    .value.on { color: #81c784; }
    .of { font-size: 13px; font-weight: 400; color: #9a9aa8; }
    .label { font-size: 12px; color: #9a9aa8; }
    mat-card-actions mat-icon { margin-right: 4px; }
  `],
})
export class MetadataSummaryTileComponent implements OnInit {
  private readonly api = inject(MetadataApiService);
  private readonly reviewState = inject(MetadataReviewStateService);

  /** True on `/admin/metadata` itself: stats switch that page's tabs instead of navigating. */
  readonly inPage = input(false);
  readonly tabSelect = output<AdminMetadataTab>();

  readonly settings = signal<MetadataSettingsDto | null>(null);
  readonly summary = this.reviewState.summary;
  readonly needsReview = computed(() => this.summary()?.needsReview ?? 0);
  readonly openFlags = computed(() => this.summary()?.openFlags ?? 0);
  readonly autoOn = computed(() => !!this.settings()?.autoMatchEnabled);

  ngOnInit(): void {
    this.reviewState.refresh();
    this.api.getSettings().subscribe({
      next: (s) => this.settings.set(s),
      error: () => this.settings.set(null),
    });
  }
}
