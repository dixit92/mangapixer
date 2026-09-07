import { Component, inject, signal, computed, OnInit, OnDestroy, HostListener } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatMenuModule } from '@angular/material/menu';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBarModule, MatSnackBar } from '@angular/material/snack-bar';

import { ApiService } from '../../core/api/api.service';
import { ManifestPageEntry, ItemManifest, ItemReadiness, ApiError } from '../../core/api/api-types';

type ReaderPhase = 'preparing' | 'ready' | 'error';

/**
 * Manifest-first paged reader (audit defects D3, D14, D36).
 *
 * - Addresses pages by the manifest's opaque entry keys, never numeric indices.
 * - On an unanalyzed item the manifest endpoint returns 202; this polls with
 *   backoff (each poll also re-prioritizes analysis) until the manifest is ready
 *   or a terminal state is reached.
 * - Maps server error codes to human messages instead of raw HTTP status text.
 * - Loading state is tied to the actual <img> load/error, not a fake delay.
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
    MatProgressSpinnerModule,
    MatSnackBarModule,
  ],
  template: `
    <div class="reader-container" [class.rtl]="direction() === 'rtl'">
      <mat-toolbar class="reader-toolbar">
        <button mat-icon-button (click)="goBack()" aria-label="Back"><mat-icon>arrow_back</mat-icon></button>
        <span class="page-info">
          @if (phase() === 'ready') { {{ currentPage() + 1 }} / {{ pageCount() }} }
        </span>
        <span class="spacer"></span>
        @if (phase() === 'ready') {
          <button mat-icon-button (click)="toggleFullscreen()" aria-label="Fullscreen">
            <mat-icon>{{ isFullscreen() ? 'fullscreen_exit' : 'fullscreen' }}</mat-icon>
          </button>
          <button mat-icon-button [matMenuTriggerFor]="fitMenu" aria-label="Fit"><mat-icon>aspect_ratio</mat-icon></button>
          <mat-menu #fitMenu="matMenu">
            <button mat-menu-item (click)="setFitMode('fit')">Fit width</button>
            <button mat-menu-item (click)="setFitMode('height')">Fit height</button>
            <button mat-menu-item (click)="setFitMode('original')">Original</button>
          </mat-menu>
          <button mat-icon-button (click)="toggleDirection()" [title]="direction() === 'rtl' ? 'Right-to-left' : 'Left-to-right'">
            <mat-icon>{{ direction() === 'rtl' ? 'format_textdirection_r_to_l' : 'format_textdirection_l_to_r' }}</mat-icon>
          </button>
        }
      </mat-toolbar>

      @if (phase() === 'preparing') {
        <div class="status">
          <mat-spinner diameter="40"></mat-spinner>
          <p>{{ statusMessage() }}</p>
        </div>
      } @else if (phase() === 'error') {
        <div class="status error">
          <mat-icon>error_outline</mat-icon>
          <p>{{ statusMessage() }}</p>
          <button mat-stroked-button (click)="retry()">Try again</button>
        </div>
      } @else {
        <div class="reader-viewport">
          @if (pageLoading()) {
            <mat-spinner class="page-spinner" diameter="36"></mat-spinner>
          }
          <img
            [src]="pageUrl()"
            [class.fit-width]="fitMode() === 'fit'"
            [class.fit-height]="fitMode() === 'height'"
            [class.original]="fitMode() === 'original'"
            [class.hidden]="pageLoading()"
            (load)="onPageLoaded()"
            (error)="onPageError()"
            alt="Page {{ currentPage() + 1 }}"
          />
          <button class="edge prev" (click)="onEdge('prev')" aria-label="Previous page"></button>
          <button class="edge next" (click)="onEdge('next')" aria-label="Next page"></button>
        </div>

        <div class="reader-controls">
          <button mat-fab (click)="prevPage()" [disabled]="currentPage() === 0" aria-label="Previous">
            <mat-icon>chevron_left</mat-icon>
          </button>
          <button mat-fab (click)="nextPage()" [disabled]="currentPage() >= pageCount() - 1" aria-label="Next">
            <mat-icon>chevron_right</mat-icon>
          </button>
        </div>
      }
    </div>
  `,
  styles: [`
    .reader-container {
      display: flex; flex-direction: column;
      position: fixed; inset: 0;
      background: #101012; z-index: 1000;
    }
    .reader-toolbar { background: #1c1c1f; color: #eee; flex-shrink: 0; }
    .page-info { margin-left: 8px; font-variant-numeric: tabular-nums; }
    .spacer { flex: 1 1 auto; }
    .status {
      flex: 1; display: flex; flex-direction: column;
      align-items: center; justify-content: center; gap: 16px;
      color: #ccc; text-align: center; padding: 24px;
    }
    .status.error mat-icon { font-size: 48px; width: 48px; height: 48px; color: #f4756a; }
    .reader-viewport {
      flex: 1; position: relative;
      display: flex; justify-content: center; align-items: center; overflow: auto;
    }
    .page-spinner { position: absolute; }
    img { max-width: 100%; max-height: 100%; }
    img.fit-width { width: 100%; height: auto; }
    img.fit-height { height: 100%; width: auto; }
    img.original { max-width: none; max-height: none; }
    img.hidden { visibility: hidden; }
    .edge {
      position: absolute; top: 0; bottom: 0; width: 30%;
      background: transparent; border: 0; cursor: pointer; padding: 0;
    }
    .edge.prev { left: 0; }
    .edge.next { right: 0; }
    .reader-controls {
      position: fixed; bottom: 24px; left: 50%; transform: translateX(-50%);
      display: flex; gap: 24px; z-index: 1001;
    }
    .rtl .reader-controls { flex-direction: row-reverse; }
  `],
})
export class ReaderComponent implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(ApiService);
  private readonly snackBar = inject(MatSnackBar);

  readonly itemId = signal('');
  readonly phase = signal<ReaderPhase>('preparing');
  readonly statusMessage = signal('Loading…');
  readonly currentPage = signal(0);
  readonly pageCount = computed(() => this.pages().length);
  readonly pages = signal<ManifestPageEntry[]>([]);
  readonly pageLoading = signal(true);
  readonly fitMode = signal<'fit' | 'height' | 'original'>('fit');
  readonly direction = signal<'ltr' | 'rtl'>('ltr');
  readonly isFullscreen = signal(false);
  readonly pageUrl = computed(() => {
    const entry = this.pages()[this.currentPage()];
    return entry ? `/api/v1/items/${this.itemId()}/pages/${encodeURIComponent(entry.entryKey)}` : '';
  });

  private contentVersion = 0;
  private revision = 0;
  private pollAttempts = 0;
  private pollTimer: ReturnType<typeof setTimeout> | null = null;
  private destroyed = false;

  ngOnInit(): void {
    this.route.paramMap.subscribe((params) => {
      const id = params.get('itemId') ?? '';
      this.itemId.set(id);
      this.pollAttempts = 0;
      this.loadManifest();
    });
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.clearPoll();
    this.saveProgress();
  }

  @HostListener('window:keydown', ['$event'])
  onKeyDown(event: KeyboardEvent): void {
    const target = event.target as HTMLElement;
    if (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA') return;
    if (this.phase() !== 'ready') return;

    switch (event.key) {
      case 'ArrowLeft': this.direction() === 'rtl' ? this.nextPage() : this.prevPage(); break;
      case 'ArrowRight': this.direction() === 'rtl' ? this.prevPage() : this.nextPage(); break;
      case 'Home': this.goToPage(0); break;
      case 'End': this.goToPage(this.pageCount() - 1); break;
      case 'f': this.toggleFullscreen(); break;
      case 'Escape': this.isFullscreen() ? this.toggleFullscreen() : this.goBack(); break;
    }
  }

  // --- Loading / readiness ---

  private loadManifest(): void {
    this.phase.set('preparing');
    this.statusMessage.set(this.pollAttempts === 0 ? 'Loading…' : 'Preparing this chapter…');

    this.api.getManifest(this.itemId()).subscribe({
      next: (res) => {
        // getManifest returns the manifest (200) or a readiness body (202).
        if (this.looksLikeManifest(res)) {
          this.onManifestReady(res as ItemManifest);
        } else {
          this.onReadiness(res as unknown as ItemReadiness);
        }
      },
      error: (err: ApiError) => this.onLoadError(err),
    });
  }

  private looksLikeManifest(res: unknown): boolean {
    return !!res && Array.isArray((res as ItemManifest).pages);
  }

  private onReadiness(readiness: ItemReadiness): void {
    const terminal = this.terminalReadinessMessage(readiness);
    if (terminal) {
      this.fail(terminal);
      return;
    }
    // Still analyzing — poll again with capped backoff.
    this.scheduleRetry();
  }

  private onManifestReady(manifest: ItemManifest): void {
    this.pages.set(manifest.pages);
    this.contentVersion = manifest.contentVersion;
    if (manifest.pages.length === 0) {
      this.fail('This chapter has no readable pages.');
      return;
    }

    // Restore saved progress, clamped to the available range.
    this.api.getProgress(this.itemId()).subscribe({
      next: (progress) => {
        this.revision = progress.revision;
        const start = Math.min(Math.max(progress.pageIndex, 0), manifest.pages.length - 1);
        this.showPage(start);
      },
      error: () => this.showPage(0),
    });
  }

  private showPage(index: number): void {
    this.currentPage.set(index);
    this.pageLoading.set(true);
    this.phase.set('ready');
  }

  private onLoadError(err: ApiError): void {
    // Some servers surface "not analyzed" as an error rather than a 202 body.
    if (err?.error === 'not_analyzed' || err?.error === 'preparing') {
      this.scheduleRetry();
      return;
    }
    this.fail(this.mapErrorCode(err?.error, err?.message));
  }

  private scheduleRetry(): void {
    if (this.destroyed) return;
    this.pollAttempts++;
    this.statusMessage.set('Preparing this chapter…');
    // Backoff: 1s, 1.5s, 2s … capped at 5s; give up after ~2 minutes.
    if (this.pollAttempts > 40) {
      this.fail('Preparing is taking longer than expected. Please try again.');
      return;
    }
    const delay = Math.min(1000 + this.pollAttempts * 500, 5000);
    this.clearPoll();
    this.pollTimer = setTimeout(() => this.loadManifest(), delay);
  }

  private terminalReadinessMessage(r: ItemReadiness): string | null {
    // state may arrive as a string enum or a number depending on serialization.
    const s = String(r.state);
    if (s === 'Failed' || s === '2') return this.mapErrorCode(r.error, 'This chapter could not be analyzed.');
    if (s === 'Unsupported' || s === '3') return 'This archive format is not supported.';
    if (s === 'Encrypted' || s === '4') return 'This archive is password-protected and cannot be opened.';
    if (s === 'Missing' || s === '5') return 'The source file is no longer available.';
    return null; // Ready/Pending → keep polling (Ready would have had pages)
  }

  private mapErrorCode(code: string | null | undefined, fallback?: string | null): string {
    switch (code) {
      case 'not_analyzed':
      case 'preparing': return 'Preparing this chapter…';
      case 'source_missing': return 'The source file is no longer available.';
      case 'not_readable':
      case 'unsupported': return "This item can't be read.";
      case 'encrypted': return 'This archive is password-protected.';
      case 'page_not_found': return 'That page could not be found.';
      case 'extraction_failed':
      case 'extraction_error': return 'This page could not be extracted from the archive.';
      case 'not_found': return 'This item no longer exists.';
      default: return fallback || 'Something went wrong loading this chapter.';
    }
  }

  private fail(message: string): void {
    this.clearPoll();
    this.statusMessage.set(message);
    this.phase.set('error');
  }

  retry(): void {
    this.pollAttempts = 0;
    this.loadManifest();
  }

  // --- Page image lifecycle ---

  onPageLoaded(): void { this.pageLoading.set(false); }

  onPageError(): void {
    this.pageLoading.set(false);
    this.snackBar.open('This page could not be loaded.', 'Dismiss', { duration: 4000 });
  }

  // --- Navigation ---

  nextPage(): void { this.goToPage(this.currentPage() + 1); }
  prevPage(): void { this.goToPage(this.currentPage() - 1); }

  private goToPage(index: number): void {
    const clamped = Math.min(Math.max(index, 0), this.pageCount() - 1);
    if (clamped === this.currentPage()) return;
    this.currentPage.set(clamped);
    this.pageLoading.set(true);
    this.saveProgress();
  }

  onEdge(side: 'prev' | 'next'): void {
    const forward = side === 'next';
    (forward !== (this.direction() === 'rtl')) ? this.nextPage() : this.prevPage();
  }

  goBack(): void {
    this.saveProgress();
    this.router.navigate(['/']);
  }

  toggleFullscreen(): void {
    this.isFullscreen.update((v) => !v);
    if (this.isFullscreen()) document.documentElement.requestFullscreen?.();
    else document.exitFullscreen?.();
  }

  setFitMode(mode: 'fit' | 'height' | 'original'): void { this.fitMode.set(mode); }
  toggleDirection(): void { this.direction.update((d) => (d === 'ltr' ? 'rtl' : 'ltr')); }

  private clearPoll(): void {
    if (this.pollTimer) { clearTimeout(this.pollTimer); this.pollTimer = null; }
  }

  private saveProgress(): void {
    if (this.phase() !== 'ready' || this.pageCount() === 0) return;
    const entry = this.pages()[this.currentPage()];
    this.api.updateProgress(this.itemId(), {
      pageIndex: this.currentPage(),
      expectedContentVersion: this.contentVersion,
      mutationId: this.newMutationId(),
      entryKey: entry?.entryKey,
    }, this.revision).subscribe({
      next: (res) => { this.revision = res.revision; },
      error: (err: ApiError) => {
        if (err?.error === 'precondition_failed') {
          // Our revision is stale (another device advanced). Re-sync silently so
          // the next save uses the current revision.
          this.api.getProgress(this.itemId()).subscribe({
            next: (p) => { this.revision = p.revision; },
            error: () => { /* leave revision as-is */ },
          });
        }
      },
    });
  }

  private newMutationId(): string {
    const c = globalThis.crypto as Crypto | undefined;
    return c?.randomUUID ? c.randomUUID() : `${Date.now()}-${Math.random().toString(36).slice(2)}`;
  }
}
