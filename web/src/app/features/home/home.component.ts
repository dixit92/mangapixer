import { Component, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatChipsModule } from '@angular/material/chips';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';

import { ApiService } from '../../core/api/api.service';
import { CoverImageDirective } from '../../shared/cover-image.directive';
import { LibraryDto, ContinueReadingEntry } from '../../core/api/api-types';

/**
 * Home page: a continue-reading strip (in-progress items, most recent first)
 * and a grid of the libraries the user can access. Replaces the P00 placeholder
 * (audit defect D24).
 */
@Component({
  selector: 'app-home',
  standalone: true,
  imports: [CommonModule, RouterLink, MatCardModule, MatIconModule, MatChipsModule, MatButtonModule, MatTooltipModule, CoverImageDirective],
  template: `
    @if (continueReading().length > 0) {
      <section class="strip-section">
        <h2>Continue reading</h2>
        <div class="strip">
          @for (item of continueReading(); track item.itemId) {
            <div class="cont-wrap">
              <a class="cont-card" [routerLink]="['/reader', item.itemId]">
                <div class="cover">
                  <img appCover [src]="coverUrl(item.itemId)" alt="" loading="lazy">
                  <mat-icon class="cover-fallback">menu_book</mat-icon>
                </div>
                <div class="cont-title" [title]="item.displayName">{{ item.displayName }}</div>
                <div class="cont-page">Page {{ item.pageIndex + 1 }}</div>
              </a>
              <button class="dismiss" mat-icon-button
                      matTooltip="Remove from Continue reading"
                      aria-label="Remove from Continue reading"
                      (click)="dismiss($event, item)">
                <mat-icon>close</mat-icon>
              </button>
            </div>
          }
        </div>
      </section>
    }

    <section>
      <h2>Libraries</h2>
      @if (loading()) {
        <p class="muted">Loading…</p>
      } @else if (libraries().length === 0) {
        <p class="muted">No libraries yet. An administrator can add one under
          <a routerLink="/admin">Administration</a>.</p>
      } @else {
        <div class="library-grid">
          @for (lib of libraries(); track lib.id) {
            <mat-card [routerLink]="['/libraries', lib.id]" class="library-card">
              <mat-card-content>
                <mat-icon class="lib-icon">folder</mat-icon>
                <h3>{{ lib.name }}</h3>
                @if (lib.itemCount !== null) {
                  <p class="muted">{{ lib.itemCount }} items</p>
                }
                @if (lib.isScanning) {
                  <mat-chip-set><mat-chip>Scanning…</mat-chip></mat-chip-set>
                }
              </mat-card-content>
            </mat-card>
          }
        </div>
      }
    </section>
  `,
  styles: [`
    h2 { margin: 8px 0 12px; }
    .muted { color: #999; font-size: 14px; }
    .strip-section { margin-bottom: 28px; }
    .strip {
      display: flex;
      gap: 16px;
      overflow-x: auto;
      padding-bottom: 8px;
    }
    .cont-wrap { position: relative; flex: 0 0 auto; width: 140px; }
    .cont-card {
      display: block;
      width: 140px;
      text-decoration: none;
      color: inherit;
    }
    /* Dismiss (×): always visible (touch-friendly), subtle until hover. */
    .dismiss {
      position: absolute; top: 4px; right: 4px; z-index: 3;
      width: 28px; height: 28px; line-height: 28px;
      background: rgba(0, 0, 0, 0.55); color: #fff; opacity: 0.75;
    }
    .dismiss:hover, .dismiss:focus-visible { opacity: 1; background: rgba(0, 0, 0, 0.75); }
    .dismiss mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .cover {
      position: relative;
      width: 140px;
      height: 200px;
      border-radius: 8px;
      overflow: hidden;
      background: rgba(255,255,255,0.06);
      display: flex;
      align-items: center;
      justify-content: center;
    }
    .cover img {
      width: 100%;
      height: 100%;
      object-fit: cover;
      position: relative;
      z-index: 1;
    }
    .cover-fallback {
      position: absolute;
      font-size: 48px; width: 48px; height: 48px;
      color: #777; z-index: 0;
    }
    .cont-title {
      margin-top: 6px;
      font-size: 13px;
      font-weight: 500;
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .cont-page { font-size: 12px; color: #999; }
    .library-grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
      gap: 16px;
    }
    .library-card { cursor: pointer; }
    .lib-icon { font-size: 40px; width: 40px; height: 40px; color: #888; }
    h3 { margin: 8px 0 4px 0; }
  `],
})
export class HomeComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly loading = signal(true);
  readonly libraries = signal<LibraryDto[]>([]);
  readonly continueReading = signal<ContinueReadingEntry[]>([]);

  ngOnInit(): void {
    this.api.getLibraries().subscribe({
      next: (libs) => { this.libraries.set(libs); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
    this.api.getContinueReading(12).subscribe({
      next: (items) => this.continueReading.set(items),
      error: () => this.continueReading.set([]),
    });
  }

  coverUrl(itemId: string): string {
    return `/api/v1/items/${itemId}/cover`;
  }

  /** Remove an item from the Continue-reading strip (1.2.0) without marking it read. */
  dismiss(event: Event, item: ContinueReadingEntry): void {
    event.preventDefault();
    event.stopPropagation();
    this.api.dismissContinueReading(item.itemId).subscribe({
      next: () => this.continueReading.update((list) => list.filter((i) => i.itemId !== item.itemId)),
      error: () => { /* transient failure — leave the card in place */ },
    });
  }
}
