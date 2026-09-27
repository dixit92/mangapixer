import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatTabsModule } from '@angular/material/tabs';

import { ApiService } from '../../../core/api/api.service';
import { MetadataReviewTab } from '../../../core/api/api-types';
import { MetadataReviewStateService } from '../metadata-review-state.service';
import { ReviewDashboardComponent, ReviewLibraryOption } from '../review/review-dashboard.component';
import { MetadataFlagsComponent } from './flags/metadata-flags.component';
import { REVIEW_TABS } from './metadata-admin-labels';
import { MetadataRunsComponent } from './runs/metadata-runs.component';
import { MetadataSettingsComponent } from './settings/metadata-settings.component';

export type AdminMetadataTab = 'settings' | 'review' | 'flags' | 'runs';
export const ADMIN_METADATA_TABS: readonly AdminMetadataTab[] = ['settings', 'review', 'flags', 'runs'];

/**
 * `/admin/metadata` (metadata stage 2, decision 4c): the dedicated series-metadata admin
 * page with tabs Settings / Review / Flags / Runs; the main admin page keeps a summary
 * tile that links here. The tab (and the review list + library) live in the query string
 * (`?tab=review&list=AutoLinked&library=...`), so the tile, the nav badge and a bookmark
 * open the right place. Each tab's content is created when it is first shown.
 */
@Component({
  selector: 'app-admin-metadata',
  standalone: true,
  imports: [
    RouterLink, MatIconModule, MatTabsModule, MetadataSettingsComponent, ReviewDashboardComponent, MetadataFlagsComponent,
    MetadataRunsComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page" data-testid="admin-metadata">
      <nav class="back"><a routerLink="/admin"><mat-icon>arrow_back</mat-icon> MangaPixer Administration</a></nav>
      <header class="head">
        <h1>Series metadata</h1>
        @if (reviewState.summary(); as s) {
          <p class="sub" data-testid="admin-metadata-summary">
            {{ s.needsReview }} to review · {{ s.openFlags }} open flag{{ s.openFlags === 1 ? '' : 's' }} ·
            {{ s.autoLinked }} auto-linked · {{ s.confirmed }} confirmed
          </p>
        }
      </header>
      <mat-tab-group [selectedIndex]="index()" (selectedIndexChange)="select($event)" animationDuration="0ms"
                     mat-stretch-tabs="false" mat-align-tabs="start">
        <mat-tab label="Settings">
          <ng-template matTabContent>
            <div class="tab"><app-metadata-settings (runStarted)="select(3)" /></div>
          </ng-template>
        </mat-tab>
        <mat-tab>
          <ng-template mat-tab-label>
            Review @if (reviewState.summary()?.needsReview; as n) { <span class="count" data-testid="tab-review-count">{{ n }}</span> }
          </ng-template>
          <ng-template matTabContent>
            <div class="tab">
              <app-metadata-review [libraries]="libraries()" [initialTab]="list()" [initialLibrary]="library()"
                                   (openFlags)="select(2)" (stateChange)="onReviewState($event)" />
            </div>
          </ng-template>
        </mat-tab>
        <mat-tab>
          <ng-template mat-tab-label>
            Flags @if (reviewState.summary()?.openFlags; as n) { <span class="count" data-testid="tab-flags-count">{{ n }}</span> }
          </ng-template>
          <ng-template matTabContent>
            <div class="tab"><app-metadata-flags [libraries]="libraries()" [initialLibrary]="library()" /></div>
          </ng-template>
        </mat-tab>
        <mat-tab label="Runs">
          <ng-template matTabContent>
            <div class="tab"><app-metadata-runs /></div>
          </ng-template>
        </mat-tab>
      </mat-tab-group>
    </div>
  `,
  styles: [`
    :host { display: block; }
    .page { max-width: 1180px; margin: 0 auto; padding-bottom: 32px; }
    .back a { display: inline-flex; align-items: center; gap: 4px; color: #b39dff; text-decoration: none; font-size: 14px; }
    .back mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .head { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 16px; margin: 8px 0 4px; }
    h1 { font-size: 24px; font-weight: 500; margin: 0; }
    .sub { margin: 0; color: #9a9aa8; font-size: 14px; }
    .tab { padding: 16px 2px 0; }
    .count { margin-left: 6px; font-size: 11px; font-weight: 600; padding: 0 6px; border-radius: 9px; background: #7c4dff; color: #fff;
      line-height: 18px; }
    @media (max-width: 599.98px) {
      h1 { font-size: 20px; }
      .tab { padding-top: 12px; }
    }
  `],
})
export class AdminMetadataComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(ApiService);
  readonly reviewState = inject(MetadataReviewStateService);

  readonly tab = signal<AdminMetadataTab>('settings');
  readonly list = signal<MetadataReviewTab>('NeedsReview');
  readonly library = signal<string | null>(null);
  readonly libraries = signal<ReviewLibraryOption[]>([]);
  readonly index = computed(() => ADMIN_METADATA_TABS.indexOf(this.tab()));

  ngOnInit(): void {
    const q = this.route.snapshot.queryParamMap;
    const tab = q.get('tab') as AdminMetadataTab | null;
    if (tab && ADMIN_METADATA_TABS.includes(tab)) this.tab.set(tab);
    const list = q.get('list') as MetadataReviewTab | null;
    if (list && REVIEW_TABS.some((t) => t.tab === list)) this.list.set(list);
    this.library.set(q.get('library'));
    this.reviewState.refresh();
    this.api.getAllLibraries().subscribe({
      next: (libs) => this.libraries.set(libs.map((l) => ({ id: l.id, name: l.name }))),
      error: () => this.libraries.set([]),
    });
  }

  select(index: number): void {
    const tab = ADMIN_METADATA_TABS[index] ?? 'settings';
    if (tab === this.tab()) return;
    this.tab.set(tab);
    this.syncUrl();
  }

  onReviewState(state: { tab: MetadataReviewTab; library: string | null }): void {
    this.list.set(state.tab);
    this.library.set(state.library);
    this.syncUrl();
  }

  private syncUrl(): void {
    const review = this.tab() === 'review';
    void this.router.navigate([], {
      relativeTo: this.route,
      replaceUrl: true,
      queryParams: {
        tab: this.tab() === 'settings' ? null : this.tab(),
        list: review && this.list() !== 'NeedsReview' ? this.list() : null,
        library: review || this.tab() === 'flags' ? this.library() : null,
      },
    });
  }
}
