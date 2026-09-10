import { Component, inject, signal, computed, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatChipsModule } from '@angular/material/chips';
import { MatTooltipModule } from '@angular/material/tooltip';

import { ApiService } from '../../core/api/api.service';
import { CoverImageDirective } from '../../shared/cover-image.directive';
import { LibraryDto, ContinueReadingEntry } from '../../core/api/api-types';

/**
 * Continue-reading entry augmented with its library (Plex-pattern home, 1.4.0).
 *
 * The `libraryId`/`libraryName` fields are supplied by the incognito-and-continue-
 * data backend lane. Until that lane merges, `ContinueReadingEntry` has neither,
 * so these are modelled as an intersection with OPTIONAL fields — forward-safe:
 * when the backend later declares `libraryId` as required on the base type, the
 * intersection still resolves (required ∧ optional = required) with no conflict.
 */
type ContinueEntryView = ContinueReadingEntry & {
  libraryId?: string;
  libraryName?: string;
};

/**
 * Home page (Plex-pattern, 1.4.0). A library **sidebar** on the left; the main
 * area shows a flat, consolidated **Continue reading** row across all (non-Private,
 * while incognito) libraries. Selecting a library in the sidebar drills into that
 * library's own continue-reading. Hiding of Private libraries is enforced
 * server-side via the `X-Incognito` header (see IncognitoService) — this component
 * renders whatever the API returns.
 *
 * Deferred to 1.5.0: the second "New chapters" row (owner still designing it). The
 * layout leaves room for it; no New-chapters data is fetched yet.
 */
@Component({
  selector: 'app-home',
  standalone: true,
  imports: [
    CommonModule,
    RouterLink,
    MatCardModule,
    MatButtonModule,
    MatIconModule,
    MatChipsModule,
    MatTooltipModule,
    CoverImageDirective,
  ],
  template: `
    <div class="home">
      <nav class="sidebar" aria-label="Libraries">
        <button type="button" class="nav-item" [class.active]="selectedLibraryId() === null"
                (click)="select(null)">
          <mat-icon>home</mat-icon><span class="nav-label">Home</span>
        </button>
        @for (lib of libraries(); track lib.id) {
          <button type="button" class="nav-item" [class.active]="selectedLibraryId() === lib.id"
                  (click)="select(lib.id)">
            <mat-icon>folder</mat-icon>
            <span class="nav-label">{{ lib.name }}</span>
            @if (lib.itemCount !== null) { <span class="nav-count">{{ lib.itemCount }}</span> }
          </button>
        }
      </nav>

      <div class="main">
        <h2>{{ selectedLibrary()?.name ?? 'Home' }}</h2>

        @if (visibleContinue().length > 0) {
          <section class="strip-section">
            <h3>Continue reading</h3>
            <div class="strip">
              @for (item of visibleContinue(); track item.itemId) {
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
        } @else if (selectedLibraryId() !== null) {
          <p class="muted">Nothing in progress in this library yet.
            <a [routerLink]="['/libraries', selectedLibraryId()]">Browse it →</a></p>
        }

        @if (selectedLibraryId() === null) {
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
        } @else {
          <a mat-stroked-button [routerLink]="['/libraries', selectedLibraryId()]">
            <mat-icon>collections_bookmark</mat-icon> Browse this library
          </a>
        }
      </div>
    </div>
  `,
  styles: [`
    h2 { margin: 8px 0 12px; }
    h3 { margin: 8px 0 12px; }
    .muted { color: #999; font-size: 14px; }
    /* Plex-pattern two-column layout: library sidebar + main area. Collapses to a
       horizontal scroll rail above the content on narrow screens. */
    .home { display: flex; gap: 20px; align-items: flex-start; }
    .sidebar {
      flex: 0 0 208px; display: flex; flex-direction: column; gap: 2px;
      position: sticky; top: 16px;
    }
    .nav-item {
      display: flex; align-items: center; gap: 10px;
      width: 100%; padding: 8px 12px; border: none; border-radius: 8px;
      background: transparent; color: inherit; font: inherit; text-align: left;
      cursor: pointer; transition: background .12s ease;
    }
    .nav-item:hover { background: rgba(255,255,255,0.06); }
    .nav-item.active { background: rgba(124,77,255,0.18); color: #b39dff; }
    .nav-item mat-icon { font-size: 20px; width: 20px; height: 20px; flex: 0 0 auto; }
    .nav-label { flex: 1 1 auto; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .nav-count { flex: 0 0 auto; font-size: 12px; color: #999; }
    .main { flex: 1 1 auto; min-width: 0; }
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
    .library-card { cursor: pointer; }
    .lib-icon { font-size: 40px; width: 40px; height: 40px; color: #888; }

    @media (max-width: 700px) {
      .home { flex-direction: column; }
      .sidebar {
        flex: 0 0 auto; flex-direction: row; width: 100%; position: static;
        overflow-x: auto; padding-bottom: 6px;
      }
      .nav-item { width: auto; white-space: nowrap; }
      .nav-count { display: none; }
    }
  `],
})
export class HomeComponent implements OnInit {
  private readonly api = inject(ApiService);

  readonly loading = signal(true);
  readonly libraries = signal<LibraryDto[]>([]);
  readonly continueReading = signal<ContinueEntryView[]>([]);
  readonly selectedLibraryId = signal<string | null>(null);

  readonly selectedLibrary = computed(() => {
    const id = this.selectedLibraryId();
    return id ? this.libraries().find((l) => l.id === id) ?? null : null;
  });

  /**
   * Continue-reading shown in the main area: the full consolidated list on Home,
   * or just the selected library's entries when one is picked in the sidebar.
   *
   * SEAM (pending the incognito-and-continue-data lane): filtering is client-side
   * on the entry's `libraryId`. Once that lane ships `libraryId` on the entry AND
   * the per-library continue endpoint, the per-library view should fetch from that
   * endpoint (so it isn't bounded by the global continue limit). Until then, a
   * selected library simply shows the in-progress items present in the global list.
   */
  readonly visibleContinue = computed(() => {
    const id = this.selectedLibraryId();
    const all = this.continueReading();
    return id ? all.filter((e) => e.libraryId === id) : all;
  });

  ngOnInit(): void {
    this.api.getLibraries().subscribe({
      next: (libs) => { this.libraries.set(libs); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
    this.api.getContinueReading(12).subscribe({
      next: (items) => this.continueReading.set(items as ContinueEntryView[]),
      error: () => this.continueReading.set([]),
    });
  }

  select(libraryId: string | null): void {
    this.selectedLibraryId.set(libraryId);
  }

  coverUrl(itemId: string): string {
    return `/api/v1/items/${itemId}/cover`;
  }

  /** Remove an item from the Continue-reading strip (1.2.0) without marking it read. */
  dismiss(event: Event, item: ContinueEntryView): void {
    event.preventDefault();
    event.stopPropagation();
    this.api.dismissContinueReading(item.itemId).subscribe({
      next: () => this.continueReading.update((list) => list.filter((i) => i.itemId !== item.itemId)),
      error: () => { /* transient failure — leave the card in place */ },
    });
  }
}
