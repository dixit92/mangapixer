import { Component, inject, signal, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatChipsModule } from '@angular/material/chips';
import { MatTooltipModule } from '@angular/material/tooltip';

import { ApiService } from '../../core/api/api.service';
import { CoverImageDirective } from '../../shared/cover-image.directive';
import { LibraryDto, ContinueReadingEntry } from '../../core/api/api-types';
import { readerModeGlyph } from '../../shared/reader-mode-glyph';

/**
 * Home page. As of 1.5.0 the library **sidebar was promoted to the app shell**
 * (`LibrarySidebarComponent`, rendered by `layout.component`), so home no longer
 * owns a sidebar and no longer filters its continue-reading by a locally selected
 * library. Home now simplifies to two things:
 *   1. the consolidated **Continue reading** row across all (non-Private, while
 *      incognito) libraries, and
 *   2. the **library grid**, each card showing a reading-direction indicator
 *      (Task C) derived from `LibraryDto.defaultReaderMode`.
 *
 * Switching libraries is a shell-sidebar navigation now (routes to
 * `/libraries/:id/browse`), not an in-home selection. Hiding of Private libraries
 * remains server-enforced via the `X-Incognito` header.
 */
@Component({
  selector: 'app-home',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    MatCardModule,
    MatIconModule,
    MatChipsModule,
    MatTooltipModule,
    CoverImageDirective,
  ],
  template: `
    <div class="home">
      @if (continueReading().length > 0) {
        <section class="strip-section">
          <h3>Continue reading</h3>
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
                <button type="button" class="dismiss"
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
        <h3>Libraries</h3>
        @if (loading()) {
          <p class="muted">Loading…</p>
        } @else if (libraries().length === 0) {
          <p class="muted">No libraries yet. An administrator can add one under
            <a routerLink="/admin">Administration</a>.</p>
        } @else {
          <div class="library-grid">
            @for (lib of libraries(); track lib.id) {
              <mat-card [routerLink]="['/libraries', lib.id, 'browse']" class="library-card">
                <mat-card-content>
                  <mat-icon class="lib-icon">folder</mat-icon>
                  @if (glyph(lib); as g) {
                    <span class="card-dir" [matTooltip]="g.label"
                          [attr.aria-label]="'Reading direction: ' + g.label">
                      <mat-icon>{{ g.icon }}</mat-icon>
                    </span>
                  }
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
    </div>
  `,
  styles: [`
    h3 { margin: 8px 0 12px; }
    .muted { color: #999; font-size: 14px; }
    /* Home is a plain content page now (the shell provides the gutter padding and
       the library sidebar); no in-component sidebar/flex layout remains. */
    .strip-section { margin-bottom: 28px; }
    .strip { display: flex; gap: 16px; overflow-x: auto; padding-bottom: 8px; }
    .cont-wrap { position: relative; flex: 0 0 auto; width: 140px; }
    .cont-card { display: block; width: 140px; text-decoration: none; color: inherit; }
    .dismiss {
      position: absolute; top: 4px; right: 4px; z-index: 3;
      display: inline-flex; align-items: center; justify-content: center;
      width: 26px; height: 26px; padding: 0; border: none; border-radius: 50%;
      background: rgba(0, 0, 0, 0.6); color: #fff; opacity: 0.8; cursor: pointer;
      transition: opacity .12s ease, background .12s ease;
    }
    .dismiss:hover, .dismiss:focus-visible { opacity: 1; background: rgba(0, 0, 0, 0.82); outline: none; }
    .dismiss mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .cover {
      position: relative; width: 140px; height: 200px; border-radius: 8px;
      overflow: hidden; background: rgba(255,255,255,0.06);
      display: flex; align-items: center; justify-content: center;
    }
    .cover img { width: 100%; height: 100%; object-fit: cover; position: relative; z-index: 1; }
    .cover-fallback { position: absolute; font-size: 48px; width: 48px; height: 48px; color: #777; z-index: 0; }
    .cont-title {
      margin-top: 6px; font-size: 13px; font-weight: 500;
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .cont-page { font-size: 12px; color: #999; }
    .library-grid {
      display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 16px;
    }
    .library-card { cursor: pointer; position: relative; }
    .lib-icon { font-size: 40px; width: 40px; height: 40px; color: #888; }
    /* Reading-direction indicator (Task C): a subtle glyph in the card's top-right
       corner. The direction name is carried by the tooltip and aria-label. */
    .card-dir {
      position: absolute; top: 10px; right: 10px;
      display: inline-flex; align-items: center; justify-content: center;
      color: #8a8a99;
    }
    .card-dir mat-icon { font-size: 20px; width: 20px; height: 20px; }
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

  glyph(lib: LibraryDto) {
    return readerModeGlyph(lib.defaultReaderMode);
  }

  coverUrl(itemId: string): string {
    return `/api/v1/items/${itemId}/cover`;
  }

  /** Remove an item from the Continue-reading strip (1.2.0) without marking it read. */
  dismiss(event: Event, item: ContinueReadingEntry): void {
    event.preventDefault();
    event.stopPropagation();
    this.api.dismissContinueReading(item.itemId).subscribe({
      next: () => {
        this.continueReading.update((list) => list.filter((i) => i.itemId !== item.itemId));
      },
      error: () => { /* transient failure — leave the card in place */ },
    });
  }
}
