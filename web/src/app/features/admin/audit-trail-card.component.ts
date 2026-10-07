import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatListModule } from '@angular/material/list';

import { ApiService } from '../../core/api/api.service';
import { AuditEventDto } from '../../core/api/api-types';

/**
 * Audit trail card (1.18.0, its own component since 1.36.0): the read side of the audit store, paged 50 at a time.
 *
 * Moved out of the admin page's Users column to the end of Administration, next to Debug logging (owner, 2026-10-07):
 * both are looked at when something went wrong, not every visit. Standalone and self-loading, like
 * `DebugLogCardComponent` and `UpdateCheckCardComponent`.
 */
@Component({
  selector: 'app-audit-trail-card',
  standalone: true,
  imports: [DatePipe, MatButtonModule, MatCardModule, MatListModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-card data-testid="audit-trail-card">
      <mat-card-header>
        <mat-card-title>Audit trail</mat-card-title>
      </mat-card-header>
      <mat-card-content>
        @if (loading()) {
          <p>Loading…</p>
        } @else if (events().length === 0) {
          <p class="empty">No audit events recorded yet.</p>
        } @else {
          <mat-list class="audit-list">
            @for (e of events(); track e.id) {
              <mat-list-item>
                <span matListItemTitle>{{ e.action }} · {{ e.result }}</span>
                <span matListItemLine>
                  {{ e.timestamp | date:'short' }}
                  @if (e.actorUserName) { · by {{ e.actorUserName }} }
                  @if (e.targetUserId !== null) { · target #{{ e.targetUserId }} }
                </span>
              </mat-list-item>
            }
          </mat-list>
          <div class="audit-pager">
            <button mat-stroked-button type="button" (click)="prevPage()" [disabled]="page() <= 1 || loading()">
              Previous
            </button>
            <span>Page {{ page() }} of {{ totalPages() }}</span>
            <button mat-stroked-button type="button" (click)="nextPage()" [disabled]="page() >= totalPages() || loading()">
              Next
            </button>
          </div>
        }
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    :host { display: block; }
    .empty { margin: 0 0 12px; font-size: 13px; opacity: 0.9; }
    .audit-list { max-height: 320px; overflow-y: auto; }
    .audit-pager { display: flex; align-items: center; gap: 12px; margin-top: 12px; font-size: 13px; }
  `],
})
export class AuditTrailCardComponent implements OnInit {
  private readonly api = inject(ApiService);

  static readonly PageSize = 50;

  readonly events = signal<AuditEventDto[]>([]);
  readonly loading = signal(true);
  readonly page = signal(1);
  readonly total = signal(0);
  readonly totalPages = computed(() => Math.max(1, Math.ceil(this.total() / AuditTrailCardComponent.PageSize)));

  ngOnInit(): void {
    this.load();
  }

  nextPage(): void {
    if (this.page() >= this.totalPages()) return;
    this.page.update((p) => p + 1);
    this.load();
  }

  prevPage(): void {
    if (this.page() <= 1) return;
    this.page.update((p) => p - 1);
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.api.getAuditTrail(this.page(), AuditTrailCardComponent.PageSize).subscribe({
      next: (result) => {
        this.events.set(result.items);
        this.total.set(result.totalCount);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }
}
