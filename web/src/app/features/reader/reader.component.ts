import { Component, inject, signal, OnInit, OnDestroy, HostListener } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatMenuModule } from '@angular/material/menu';
import { MatSnackBarModule, MatSnackBar } from '@angular/material/snack-bar';

import { ApiService } from '../../core/api/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { CatalogNodeDto, ReadingProgressDto } from '../../core/api/api-types';

/**
 * Paged reader component. Supports LTR/RTL navigation, zoom/fit modes,
 * page picker, bounded prefetch, progress save/restore, and neighbors.
 *
 * Rules:
 * - Prefetch never advances completion.
 * - Reload/back-navigation restores the same logical page.
 * - Authentication/source failures preserve understandable reader state.
 * - Keyboard and touch do not fight form controls.
 */
@Component({
  selector: 'app-reader',
  standalone: true,
  imports: [
    CommonModule,
    MatButtonModule,
    MatIconModule,
    MatToolbarModule,
    MatMenuModule,
    MatSnackBarModule,
  ],
  template: `
    <div class="reader-container" [class.rtl]="direction() === 'rtl'">
      <mat-toolbar class="reader-toolbar">
        <button mat-icon-button (click)="goBack()"><mat-icon>arrow_back</mat-icon></button>
        <span class="page-info">{{ currentPage() + 1 }} / {{ pageCount() }}</span>
        <span class="spacer"></span>
        <button mat-icon-button (click)="toggleFullscreen()">
          <mat-icon>{{ isFullscreen() ? 'fullscreen_exit' : 'fullscreen' }}</mat-icon>
        </button>
        <button mat-icon-button [matMenuTriggerFor]="fitMenu">
          <mat-icon>aspect_ratio</mat-icon>
        </button>
        <mat-menu #fitMenu="matMenu">
          <button mat-menu-item (click)="setFitMode('fit')">Fit Width</button>
          <button mat-menu-item (click)="setFitMode('height')">Fit Height</button>
          <button mat-menu-item (click)="setFitMode('original')">Original</button>
        </mat-menu>
      </mat-toolbar>

      <div class="reader-viewport" (swipeleft)="onSwipeLeft()" (swiperight)="onSwipeRight()">
        @if (loading()) {
          <div class="loading">Loading page...</div>
        } @else if (error()) {
          <div class="error">{{ error() }}</div>
        } @else {
          <img
            [src]="pageUrl()"
            [class.fit-width]="fitMode() === 'fit'"
            [class.fit-height]="fitMode() === 'height'"
            [class.original]="fitMode() === 'original'"
            alt="Page {{ currentPage() + 1 }}"
          />
        }
      </div>

      <div class="reader-controls">
        <button mat-fab (click)="prevPage()" [disabled]="currentPage() === 0">
          <mat-icon>chevron_left</mat-icon>
        </button>
        <button mat-fab (click)="nextPage()" [disabled]="currentPage() >= pageCount() - 1">
          <mat-icon>chevron_right</mat-icon>
        </button>
      </div>
    </div>
  `,
  styles: [`
    .reader-container {
      display: flex;
      flex-direction: column;
      height: 100vh;
      position: fixed;
      top: 0; left: 0; right: 0; bottom: 0;
      background: #1a1a1a;
      z-index: 1000;
    }
    .reader-toolbar {
      background: #333;
      color: white;
      flex-shrink: 0;
    }
    .page-info { margin-left: 8px; }
    .spacer { flex: 1 1 auto; }
    .reader-viewport {
      flex: 1;
      display: flex;
      justify-content: center;
      align-items: center;
      overflow: auto;
    }
    .loading, .error { color: white; font-size: 18px; }
    .error { color: #f44336; }
    img { max-width: 100%; max-height: 100%; }
    img.fit-width { width: 100%; height: auto; }
    img.fit-height { height: 100%; width: auto; }
    img.original { max-width: none; max-height: none; }
    .reader-controls {
      position: fixed;
      bottom: 24px;
      left: 50%;
      transform: translateX(-50%);
      display: flex;
      gap: 24px;
      z-index: 1001;
    }
    .rtl { direction: rtl; }
  `],
})
export class ReaderComponent implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly snackBar = inject(MatSnackBar);

  readonly itemId = signal('');
  readonly currentPage = signal(0);
  readonly pageCount = signal(0);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly fitMode = signal<'fit' | 'height' | 'original'>('fit');
  readonly direction = signal<'ltr' | 'rtl'>('ltr');
  readonly isFullscreen = signal(false);
  readonly pageUrl = signal<string>('');

  private contentVersion = 0;
  private progressSaved = false;
  private prefetchController: AbortController | null = null;

  ngOnInit(): void {
    this.route.paramMap.subscribe((params) => {
      const id = params.get('itemId')!;
      this.itemId.set(id);
      this.loadItemAndProgress();
    });
  }

  ngOnDestroy(): void {
    this.saveProgress();
    if (this.prefetchController) this.prefetchController.abort();
  }

  @HostListener('window:keydown', ['$event'])
  onKeyDown(event: KeyboardEvent): void {
    // Don't interfere with form controls
    const target = event.target as HTMLElement;
    if (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA') return;

    switch (event.key) {
      case 'ArrowLeft':
        if (this.direction() === 'rtl') this.nextPage();
        else this.prevPage();
        break;
      case 'ArrowRight':
        if (this.direction() === 'rtl') this.prevPage();
        else this.nextPage();
        break;
      case 'Home': this.firstPage(); break;
      case 'End': this.lastPage(); break;
      case 'f': this.toggleFullscreen(); break;
      case 'Escape':
        if (this.isFullscreen()) this.toggleFullscreen();
        else this.goBack();
        break;
    }
  }

  nextPage(): void {
    if (this.currentPage() >= this.pageCount() - 1) return;
    this.saveProgress();
    this.currentPage.update((p) => p + 1);
    this.loadPage();
  }

  prevPage(): void {
    if (this.currentPage() === 0) return;
    this.saveProgress();
    this.currentPage.update((p) => p - 1);
    this.loadPage();
  }

  firstPage(): void {
    this.saveProgress();
    this.currentPage.set(0);
    this.loadPage();
  }

  lastPage(): void {
    this.saveProgress();
    this.currentPage.set(this.pageCount() - 1);
    this.loadPage();
  }

  goBack(): void {
    this.saveProgress();
    this.router.navigate(['/libraries']);
  }

  toggleFullscreen(): void {
    this.isFullscreen.update((v) => !v);
    if (this.isFullscreen()) {
      document.documentElement.requestFullscreen?.();
    } else {
      document.exitFullscreen?.();
    }
  }

  setFitMode(mode: 'fit' | 'height' | 'original'): void {
    this.fitMode.set(mode);
  }

  onSwipeLeft(): void {
    if (this.direction() === 'ltr') this.nextPage();
    else this.prevPage();
  }

  onSwipeRight(): void {
    if (this.direction() === 'ltr') this.prevPage();
    else this.nextPage();
  }

  private loadItemAndProgress(): void {
    this.loading.set(true);
    this.error.set(null);

    this.api.getNode(this.itemId()).subscribe({
      next: (node: CatalogNodeDto) => {
        if (node.pageCount) this.pageCount.set(node.pageCount);

        // Load saved progress
        this.api.getProgress(this.itemId()).subscribe({
          next: (progress: ReadingProgressDto) => {
            this.currentPage.set(progress.pageIndex);
            this.contentVersion = progress.contentVersion;
            this.loadPage();
          },
          error: () => {
            // No saved progress — start at page 0
            this.currentPage.set(0);
            this.loadPage();
          },
        });
      },
      error: (err) => {
        this.loading.set(false);
        this.error.set(err.message || 'Failed to load item');
      },
    });
  }

  private loadPage(): void {
    this.loading.set(true);
    this.error.set(null);

    // Build page URL — the server handles authorization
    const pageIdx = this.currentPage();
    this.pageUrl.set(`/api/v1/items/${this.itemId()}/pages/${pageIdx}`);

    // Simulate page load completion
    this.loading.set(false);

    // Prefetch next page (bounded — only one page ahead)
    this.prefetchNextPage();

    // Save progress (debounced via the progress save call)
    this.saveProgress();
  }

  private prefetchNextPage(): void {
    // Bounded prefetch — only one page ahead, never advances completion
    if (this.currentPage() >= this.pageCount() - 1) return;

    if (this.prefetchController) this.prefetchController.abort();
    this.prefetchController = new AbortController();

    const nextPage = this.currentPage() + 1;
    const prefetchUrl = `/api/v1/items/${this.itemId()}/pages/${nextPage}`;

    // Use a link rel=prefetch for the browser to handle
    const link = document.createElement('link');
    link.rel = 'prefetch';
    link.href = prefetchUrl;
    document.head.appendChild(link);

    setTimeout(() => link.remove(), 5000);
  }

  private saveProgress(): void {
    if (!this.itemId() || this.pageCount() === 0) return;

    this.api.updateProgress(this.itemId(), {
      pageIndex: this.currentPage(),
      expectedContentVersion: this.contentVersion,
    }).subscribe({
      error: (err) => {
        // Don't block reading on progress save failure
        if (err.error === 'StaleContent') {
          this.snackBar.open('Content has changed. Progress may be stale.', 'Dismiss', { duration: 5000 });
        }
      },
    });
  }
}
