import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';

import { MetadataSettingsDto } from '../../../core/api/api-types';
import { MetadataApiService } from '../../metadata/metadata-api.service';
import { MetadataReviewStateService } from '../../metadata/metadata-review-state.service';

/**
 * Series metadata summary tile on the main admin page (stage 2, decision 4c): the
 * settings and review moved to `/admin/metadata`; this tile keeps the four numbers an
 * admin needs at a glance - items to review, open flags, today's requests against the
 * one daily budget, and whether automatic matching is on - each linking to its tab.
 * Local reads only (settings + review summary); a server without stage 2 shows dashes.
 */
@Component({
  selector: 'app-metadata-summary-tile',
  standalone: true,
  imports: [RouterLink, MatButtonModule, MatCardModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-card data-testid="metadata-summary-tile">
      <mat-card-header>
        <mat-card-title>Series metadata</mat-card-title>
        <mat-card-subtitle>Series information from ComicInfo.xml and, if you allow it, MangaUpdates</mat-card-subtitle>
      </mat-card-header>
      <mat-card-content>
        <div class="stats">
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
        </div>
      </mat-card-content>
      <mat-card-actions align="end">
        <a mat-flat-button routerLink="/admin/metadata" data-testid="tile-open">
          <mat-icon>tune</mat-icon> Open series metadata
        </a>
      </mat-card-actions>
    </mat-card>
  `,
  styles: [`
    mat-card { margin-bottom: 16px; }
    .stats { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 10px; margin-top: 8px; }
    .stat { display: flex; flex-direction: column; gap: 2px; padding: 12px 14px; border-radius: 10px; text-decoration: none; color: inherit;
      background: rgba(255, 255, 255, 0.04); border: 1px solid rgba(255, 255, 255, 0.06); }
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
