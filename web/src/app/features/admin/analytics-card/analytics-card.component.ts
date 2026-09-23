import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { forkJoin } from 'rxjs';

import { ApiService } from '../../../core/api/api.service';
import { AnalyticsOverviewDto, AnalyticsUserRowDto, ApiError } from '../../../core/api/api-types';

type SortColumn =
  | 'username'
  | 'isAdmin'
  | 'isActive'
  | 'lastLoginAt'
  | 'chaptersCompleted'
  | 'chaptersInProgress'
  | 'bookmarkCount'
  | 'favoriteCount'
  | 'lastReadingActivityAt';

type SortDirection = 'asc' | 'desc';

interface StatTile {
  label: string;
  value: number;
}

interface SortableColumn {
  key: SortColumn;
  label: string;
}

const COLUMNS: SortableColumn[] = [
  { key: 'username', label: 'User' },
  { key: 'isAdmin', label: 'Role' },
  { key: 'isActive', label: 'Status' },
  { key: 'lastLoginAt', label: 'Last login' },
  { key: 'chaptersCompleted', label: 'Completed' },
  { key: 'chaptersInProgress', label: 'In progress' },
  { key: 'bookmarkCount', label: 'Bookmarks' },
  { key: 'favoriteCount', label: 'Favorites' },
  { key: 'lastReadingActivityAt', label: 'Last activity' },
];

/**
 * Admin Analytics dashboard card (1.22.0 lane E, "Dashboard v1"). A section
 * of the existing admin page (owner decision 2026-09-22), not a new tab: an
 * overview of library/content/processing/engagement counts plus a per-user
 * engagement table. Admin-only server-side; every number here is a count or
 * a timestamp, never a title/item name/path.
 *
 * On-demand aggregation on the server (no rollup table) — the two GETs run
 * in parallel via forkJoin and refresh together. Standalone and self-loading,
 * the same pattern as UpdateCheckCardComponent.
 */
@Component({
  selector: 'app-analytics-card',
  standalone: true,
  imports: [CommonModule, MatButtonModule, MatCardModule, MatIconModule, MatProgressSpinnerModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-card>
      <mat-card-header>
        <mat-card-title>Analytics</mat-card-title>
        <mat-card-subtitle>Instance overview and per-user engagement (admin-only)</mat-card-subtitle>
      </mat-card-header>
      <mat-card-content>
        @if (loading()) {
          <p class="muted">Loading…</p>
        } @else if (error()) {
          <div class="error" role="alert">{{ error() }}</div>
        } @else {
          <div class="tiles">
            @for (tile of tiles(); track tile.label) {
              <div class="tile">
                <span class="tile-value">{{ tile.value }}</span>
                <span class="tile-label">{{ tile.label }}</span>
              </div>
            }
          </div>

          <p class="scope-note">
            Counts and timestamps only. Never chapter/item titles or paths. Reading done
            in a library a user marked Private is excluded from that user's own counts.
          </p>

          <div class="table-scroll">
            <table aria-label="Per-user analytics">
              <caption class="sr-only">Per-user engagement, sortable by column</caption>
              <thead>
                <tr>
                  @for (col of columns; track col.key) {
                    <th scope="col" [attr.aria-sort]="ariaSort(col.key)">
                      <button type="button" class="sort-btn" (click)="sortBy(col.key)">
                        {{ col.label }}
                        @if (sortColumn() === col.key) {
                          <mat-icon inline class="sort-icon">
                            {{ sortDirection() === 'asc' ? 'arrow_upward' : 'arrow_downward' }}
                          </mat-icon>
                        }
                      </button>
                    </th>
                  }
                </tr>
              </thead>
              <tbody>
                @for (u of sortedUsers(); track u.id) {
                  <tr>
                    <td>{{ u.username }}</td>
                    <td>{{ u.isAdmin ? 'Admin' : 'User' }}</td>
                    <td>
                      @if (!u.isActive) {
                        Disabled
                      } @else if (u.isPendingActivation) {
                        Pending
                      } @else {
                        Active
                      }
                    </td>
                    <td>{{ u.lastLoginAt ? (u.lastLoginAt | date: 'short') : '—' }}</td>
                    <td>{{ u.chaptersCompleted }}</td>
                    <td>{{ u.chaptersInProgress }}</td>
                    <td>{{ u.bookmarkCount }}</td>
                    <td>{{ u.favoriteCount }}</td>
                    <td>{{ u.lastReadingActivityAt ? (u.lastReadingActivityAt | date: 'short') : '—' }}</td>
                  </tr>
                } @empty {
                  <tr><td [attr.colspan]="columns.length" class="muted">No users yet.</td></tr>
                }
              </tbody>
            </table>
          </div>

          <button mat-stroked-button type="button" [disabled]="loading()" (click)="load()">
            Refresh
          </button>
        }
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    mat-card { margin-bottom: 16px; }
    .muted { color: #999; font-size: 14px; }
    .error { color: #f44336; font-size: 14px; margin: 8px 0; }
    .scope-note { color: #999; font-size: 13px; margin: 8px 0 12px; }
    .tiles {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(120px, 1fr));
      gap: 8px;
      margin-bottom: 12px;
    }
    .tile {
      display: flex;
      flex-direction: column;
      padding: 10px 12px;
      border-radius: 6px;
      background: rgba(255, 255, 255, 0.06);
    }
    .tile-value { font-size: 20px; font-weight: 600; line-height: 1.2; }
    .tile-label { font-size: 12px; color: #999; }
    .table-scroll { overflow-x: auto; margin-bottom: 12px; }
    table { border-collapse: collapse; width: 100%; font-size: 13px; }
    th, td { padding: 6px 10px; text-align: left; white-space: nowrap; }
    thead th { border-bottom: 1px solid rgba(255, 255, 255, 0.12); }
    tbody tr:nth-child(even) { background: rgba(255, 255, 255, 0.03); }
    .sort-btn {
      background: none;
      border: none;
      color: inherit;
      font: inherit;
      font-weight: 600;
      cursor: pointer;
      display: inline-flex;
      align-items: center;
      gap: 2px;
      padding: 0;
    }
    .sort-icon { font-size: 14px; width: 14px; height: 14px; }
    .sr-only {
      position: absolute;
      width: 1px; height: 1px;
      padding: 0; margin: -1px;
      overflow: hidden;
      clip: rect(0, 0, 0, 0);
      white-space: nowrap;
      border: 0;
    }
  `],
})
export class AnalyticsCardComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly columns = COLUMNS;

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly overview = signal<AnalyticsOverviewDto | null>(null);
  readonly users = signal<AnalyticsUserRowDto[]>([]);

  readonly sortColumn = signal<SortColumn>('username');
  readonly sortDirection = signal<SortDirection>('asc');

  readonly tiles = computed<StatTile[]>(() => {
    const o = this.overview();
    if (!o) return [];
    return [
      { label: 'Libraries', value: o.libraryCount },
      { label: 'Archives', value: o.archiveNodeCount },
      { label: 'Folders', value: o.folderNodeCount },
      { label: 'Tombstoned', value: o.tombstonedNodeCount },
      { label: 'Analyzed', value: o.analyzedItemCount },
      { label: 'Analysis pending', value: o.pendingItemCount },
      { label: 'Analysis failed', value: o.failedItemCount },
      { label: 'Users', value: o.userCount },
      { label: 'Active users', value: o.activeUserCount },
      { label: 'Admins', value: o.adminCount },
      { label: 'Pending activation', value: o.pendingActivationCount },
      { label: 'Chapters completed', value: o.completedItemCount },
      { label: 'Chapters in progress', value: o.inProgressItemCount },
      { label: 'Bookmarks', value: o.bookmarkCount },
      { label: 'Favorites', value: o.favoriteCount },
      { label: 'Active sessions', value: o.activeSessionCount },
    ];
  });

  readonly sortedUsers = computed(() => {
    const column = this.sortColumn();
    const direction = this.sortDirection();
    const rows = [...this.users()];
    rows.sort((a, b) => {
      const cmp = compareValues(a[column], b[column]);
      return direction === 'asc' ? cmp : -cmp;
    });
    return rows;
  });

  ngOnInit(): void {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.error.set(null);
    forkJoin({
      overview: this.api.getAnalyticsOverview(),
      users: this.api.getAnalyticsUsers(),
    }).subscribe({
      next: ({ overview, users }) => {
        this.overview.set(overview);
        this.users.set(users);
        this.loading.set(false);
      },
      error: (err: ApiError) => {
        this.error.set(err?.message || 'Failed to load analytics');
        this.loading.set(false);
      },
    });
  }

  sortBy(column: SortColumn): void {
    if (this.sortColumn() === column) {
      this.sortDirection.set(this.sortDirection() === 'asc' ? 'desc' : 'asc');
    } else {
      this.sortColumn.set(column);
      this.sortDirection.set('asc');
    }
  }

  ariaSort(column: SortColumn): 'ascending' | 'descending' | 'none' {
    if (this.sortColumn() !== column) return 'none';
    return this.sortDirection() === 'asc' ? 'ascending' : 'descending';
  }
}

function compareValues(a: string | number | boolean | null, b: string | number | boolean | null): number {
  if (a === b) return 0;
  if (a === null) return -1;
  if (b === null) return 1;
  if (typeof a === 'string' && typeof b === 'string') return a.localeCompare(b);
  return a > b ? 1 : -1;
}
