import { Component, inject, signal, computed, OnInit, OnDestroy, HostListener, ElementRef, viewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatSliderModule } from '@angular/material/slider';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSnackBarModule, MatSnackBar } from '@angular/material/snack-bar';

import { ApiService } from '../../core/api/api.service';
import { ManifestPageEntry, ItemManifest, ItemReadiness, ApiError, ReaderMode } from '../../core/api/api-types';

type ReaderPhase = 'preparing' | 'ready' | 'error';
type ReaderView = 'paged' | 'spread' | 'webtoon';
type FitMode = 'screen' | 'width' | 'height' | 'original';

/**
 * Manifest-first reader (audit defects D3, D14, D36) with paged / double-spread /
 * vertical-webtoon views. All views address pages by the manifest's opaque entry
 * keys, never numeric indices.
 *
 * Reader-view requirements (2026-09-07 review, see the MVP-gap checkpoint):
 *  1. Fit-to-screen (contain) is the default image fit.
 *  2. The prev/next chevron controls never swap position when direction flips —
 *     left is always "previous", right always "next". Direction changes only
 *     which physical page "next" advances to (and edge-tap / arrow-key mapping).
 *  3. The fullscreen / fit / direction / mode controls are hidden in fullscreen.
 *  4. Every control carries a tooltip AND an aria-label (tooltip is supplementary
 *     so touch devices are not left without an affordance).
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
    MatTooltipModule,
    MatSliderModule,
    MatProgressSpinnerModule,
    MatSnackBarModule,
  ],
  template: `
    <div class="reader-container">
      <mat-toolbar class="reader-toolbar">
        <button mat-icon-button (click)="goBack()" matTooltip="Back to library" aria-label="Back to library">
          <mat-icon>arrow_back</mat-icon>
        </button>
        <span class="page-info">
          @if (phase() === 'ready') { {{ currentPage() + 1 }} / {{ pageCount() }} }
        </span>
        <span class="spacer"></span>

        <!-- Requirement 3: hide these controls in fullscreen -->
        @if (phase() === 'ready' && !isFullscreen()) {
          <button mat-icon-button [matMenuTriggerFor]="modeMenu" matTooltip="Reading mode" aria-label="Reading mode">
            <mat-icon>{{ viewIcon() }}</mat-icon>
          </button>
          <mat-menu #modeMenu="matMenu">
            <button mat-menu-item (click)="setView('paged')"><mat-icon>crop_portrait</mat-icon> Single page</button>
            <button mat-menu-item (click)="setView('spread')"><mat-icon>import_contacts</mat-icon> Double spread</button>
            <button mat-menu-item (click)="setView('webtoon')"><mat-icon>view_day</mat-icon> Vertical (webtoon)</button>
          </mat-menu>

          @if (view() === 'webtoon') {
            <!-- Requirement 6: webtoon width slider replaces the inoperative fit menu. -->
            <mat-icon class="slider-icon" aria-hidden="true">width_normal</mat-icon>
            <mat-slider class="width-slider" min="30" max="100" step="5"
                        matTooltip="Page width" aria-label="Webtoon page width">
              <input matSliderThumb [value]="webtoonWidthPct()"
                     (valueChange)="setWebtoonWidth($event)" aria-label="Webtoon page width">
            </mat-slider>
          } @else {
            <button mat-icon-button [matMenuTriggerFor]="fitMenu" matTooltip="Image fit" aria-label="Image fit">
              <mat-icon>aspect_ratio</mat-icon>
            </button>
            <mat-menu #fitMenu="matMenu">
              <button mat-menu-item (click)="setFitMode('screen')">Fit screen</button>
              <button mat-menu-item (click)="setFitMode('width')">Fit width</button>
              <button mat-menu-item (click)="setFitMode('height')">Fit height</button>
              <button mat-menu-item (click)="setFitMode('original')">Original size</button>
            </mat-menu>
          }

          @if (view() !== 'webtoon') {
            <button mat-icon-button (click)="toggleDirection()"
                    [matTooltip]="direction() === 'rtl' ? 'Right-to-left (manga)' : 'Left-to-right'"
                    [attr.aria-label]="direction() === 'rtl' ? 'Switch to left-to-right' : 'Switch to right-to-left'">
              <mat-icon>{{ direction() === 'rtl' ? 'format_textdirection_r_to_l' : 'format_textdirection_l_to_r' }}</mat-icon>
            </button>
          }

          <button mat-icon-button (click)="toggleFullscreen()"
                  [matTooltip]="isFullscreen() ? 'Exit fullscreen' : 'Fullscreen'"
                  [attr.aria-label]="isFullscreen() ? 'Exit fullscreen' : 'Enter fullscreen'">
            <mat-icon>{{ isFullscreen() ? 'fullscreen_exit' : 'fullscreen' }}</mat-icon>
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
      } @else if (view() === 'webtoon') {
        <!-- Vertical continuous scroll; progress tracked by scroll position. -->
        <div class="reader-viewport webtoon" #scroller (scroll)="onWebtoonScroll()">
          @for (entry of pages(); track entry.entryKey) {
            <img class="webtoon-page" [src]="pageUrlFor(entry)" loading="lazy"
                 [style.width.%]="webtoonWidthPct()"
                 [attr.data-index]="$index" alt="Page {{ $index + 1 }}" />
          }
        </div>
      } @else {
        <!-- Paged or double-spread: fixed viewport, one screen at a time. -->
        <div class="reader-viewport">
          @if (pageLoading()) {
            <mat-spinner class="page-spinner" diameter="36"></mat-spinner>
          }
          <div class="spread-row" [class.rtl-flow]="direction() === 'rtl'">
            @for (entry of currentSpreadEntries(); track entry.entryKey) {
              <img
                [src]="pageUrlFor(entry)"
                [class.fit-screen]="fitMode() === 'screen'"
                [class.fit-width]="fitMode() === 'width'"
                [class.fit-height]="fitMode() === 'height'"
                [class.original]="fitMode() === 'original'"
                [class.paired]="currentSpreadEntries().length > 1"
                (load)="onPageLoaded()"
                (error)="onPageError()"
                alt="Page"
              />
            }
          </div>
          <button class="edge prev" (click)="onEdge('prev')" aria-label="Previous"></button>
          <button class="edge next" (click)="onEdge('next')" aria-label="Next"></button>
        </div>

        <!-- Requirement 2: left is ALWAYS previous, right ALWAYS next; no flip. -->
        <div class="reader-controls">
          <button mat-fab (click)="prevPage()" [disabled]="atStart()" matTooltip="Previous" aria-label="Previous">
            <mat-icon>chevron_left</mat-icon>
          </button>
          <button mat-fab (click)="nextPage()" [disabled]="atEnd()" matTooltip="Next" aria-label="Next">
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
    .page-spinner { position: absolute; z-index: 2; }
    /* Double-spread row. rtl-flow puts the earlier page on the right. */
    .spread-row { display: flex; align-items: center; justify-content: center; max-width: 100%; max-height: 100%; }
    .spread-row.rtl-flow { flex-direction: row-reverse; }
    img { max-width: 100%; max-height: 100%; }
    /* Requirement 1: fit-screen (contain) is the default. */
    img.fit-screen { max-width: 100%; max-height: 100%; width: auto; height: auto; }
    img.fit-width { width: 100%; height: auto; max-height: none; }
    img.fit-height { height: 100%; width: auto; max-width: none; }
    img.original { max-width: none; max-height: none; }
    /* When two pages are paired, each takes at most half the width. */
    .spread-row img.paired { max-width: 50%; }
    /* Webtoon: full-width column, natural vertical scroll. */
    .reader-viewport.webtoon { flex-direction: column; align-items: center; }
    /* Width is driven by the webtoon width slider (requirement 6), 30–100% of viewport. */
    .webtoon-page { height: auto; display: block; max-width: 100%; }
    .width-slider { width: 140px; }
    .slider-icon { opacity: 0.7; margin-right: 2px; }
    .edge {
      position: absolute; top: 0; bottom: 0; width: 30%;
      background: transparent; border: 0; cursor: pointer; padding: 0; z-index: 1;
    }
    .edge.prev { left: 0; }
    .edge.next { right: 0; }
    .reader-controls {
      position: fixed; bottom: 24px; left: 50%; transform: translateX(-50%);
      display: flex; gap: 24px; z-index: 1001;
    }
  `],
})
export class ReaderComponent implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly api = inject(ApiService);
  private readonly snackBar = inject(MatSnackBar);

  private readonly scroller = viewChild<ElementRef<HTMLElement>>('scroller');

  readonly itemId = signal('');
  readonly phase = signal<ReaderPhase>('preparing');
  readonly statusMessage = signal('Loading…');
  readonly currentPage = signal(0);
  readonly pageCount = computed(() => this.pages().length);
  readonly pages = signal<ManifestPageEntry[]>([]);
  readonly pageLoading = signal(true);
  readonly fitMode = signal<FitMode>('screen'); // Requirement 1
  readonly direction = signal<'ltr' | 'rtl'>('ltr');
  readonly view = signal<ReaderView>('paged');
  readonly isFullscreen = signal(false);
  readonly coverIsStandalone = signal(true); // first page shown alone in spread view
  readonly webtoonWidthPct = signal<number>(this.loadWebtoonWidth()); // requirement 6

  readonly viewIcon = computed(() =>
    this.view() === 'webtoon' ? 'view_day' : this.view() === 'spread' ? 'import_contacts' : 'crop_portrait');

  /** The page indices shown together on the current screen (1 for paged, 1–2 for spread). */
  readonly currentSpreadEntries = computed<ManifestPageEntry[]>(() => {
    const all = this.pages();
    if (all.length === 0) return [];
    if (this.view() !== 'spread') {
      const p = all[this.currentPage()];
      return p ? [p] : [];
    }
    const spread = this.spreads().find((s) => s.includes(this.currentPage())) ?? [this.currentPage()];
    return spread.map((i) => all[i]).filter(Boolean);
  });

  /** Grouping of page indices into spreads (double-page view). */
  readonly spreads = computed<number[][]>(() => this.computeSpreads());

  readonly atStart = computed(() => this.currentPage() <= 0);
  readonly atEnd = computed(() => this.currentPage() >= this.pageCount() - 1);

  private contentVersion = 0;
  private revision = 0;
  private pollAttempts = 0;
  private pollTimer: ReturnType<typeof setTimeout> | null = null;
  private webtoonSaveTimer: ReturnType<typeof setTimeout> | null = null;
  private destroyed = false;

  pageUrlFor(entry: ManifestPageEntry | undefined): string {
    return entry ? `/api/v1/items/${this.itemId()}/pages/${encodeURIComponent(entry.entryKey)}` : '';
  }

  ngOnInit(): void {
    // Honor the user's default reading mode/direction; failure falls back to paged LTR.
    this.api.getPreferences().subscribe({
      next: (prefs) => this.applyDefaultMode(prefs.defaultReaderMode),
      error: () => { /* keep defaults */ },
    });
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
    if (this.webtoonSaveTimer) clearTimeout(this.webtoonSaveTimer);
    this.saveProgress();
  }

  private applyDefaultMode(mode: ReaderMode): void {
    switch (mode) {
      case 'PagedRtl': this.view.set('paged'); this.direction.set('rtl'); break;
      case 'DoubleSpread': this.view.set('spread'); break;
      case 'VerticalWebtoon': this.view.set('webtoon'); break;
      default: this.view.set('paged'); this.direction.set('ltr');
    }
  }

  @HostListener('document:fullscreenchange')
  onFullscreenChange(): void {
    // Keep our signal in sync when the browser exits fullscreen via Esc.
    this.isFullscreen.set(!!document.fullscreenElement);
  }

  @HostListener('window:keydown', ['$event'])
  onKeyDown(event: KeyboardEvent): void {
    const target = event.target as HTMLElement;
    if (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA') return;
    if (this.phase() !== 'ready') return;
    if (this.view() === 'webtoon') return; // native scroll drives webtoon

    switch (event.key) {
      case 'ArrowLeft': this.direction() === 'rtl' ? this.nextPage() : this.prevPage(); break;
      case 'ArrowRight': this.direction() === 'rtl' ? this.prevPage() : this.nextPage(); break;
      case 'Home': this.goToPage(0); break;
      case 'End': this.goToPage(this.pageCount() - 1); break;
      case 'f': this.toggleFullscreen(); break;
      case 'Escape': this.isFullscreen() ? this.toggleFullscreen() : this.goBack(); break;
    }
  }

  // --- Loading / readiness (unchanged manifest-first flow) ---

  private loadManifest(): void {
    this.phase.set('preparing');
    this.statusMessage.set(this.pollAttempts === 0 ? 'Loading…' : 'Preparing this chapter…');

    this.api.getManifest(this.itemId()).subscribe({
      next: (res) => {
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
    if (terminal) { this.fail(terminal); return; }
    this.scheduleRetry();
  }

  private onManifestReady(manifest: ItemManifest): void {
    this.pages.set(manifest.pages);
    this.contentVersion = manifest.contentVersion;
    if (manifest.pages.length === 0) {
      this.fail('This chapter has no readable pages.');
      return;
    }
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
    if (this.view() === 'webtoon') {
      // Scroll the saved page into view once the DOM is present.
      queueMicrotask(() => this.scrollWebtoonTo(index));
    }
  }

  private onLoadError(err: ApiError): void {
    if (err?.error === 'not_analyzed' || err?.error === 'preparing') { this.scheduleRetry(); return; }
    this.fail(this.mapErrorCode(err?.error, err?.message));
  }

  private scheduleRetry(): void {
    if (this.destroyed) return;
    this.pollAttempts++;
    this.statusMessage.set('Preparing this chapter…');
    if (this.pollAttempts > 40) {
      this.fail('Preparing is taking longer than expected. Please try again.');
      return;
    }
    const delay = Math.min(1000 + this.pollAttempts * 500, 5000);
    this.clearPoll();
    this.pollTimer = setTimeout(() => this.loadManifest(), delay);
  }

  private terminalReadinessMessage(r: ItemReadiness): string | null {
    const s = String(r.state);
    if (s === 'Failed') return this.mapErrorCode(r.error, 'This chapter could not be analyzed.');
    if (s === 'Unsupported') return 'This archive format is not supported.';
    if (s === 'Encrypted') return 'This archive is password-protected and cannot be opened.';
    if (s === 'Missing') return 'The source file is no longer available.';
    return null;
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

  retry(): void { this.pollAttempts = 0; this.loadManifest(); }

  // --- Page image lifecycle ---

  onPageLoaded(): void { this.pageLoading.set(false); }
  onPageError(): void {
    this.pageLoading.set(false);
    this.snackBar.open('This page could not be loaded.', 'Dismiss', { duration: 4000 });
  }

  // --- Navigation ---

  /** Advance toward the end (next screen). In spread view, jumps a whole spread. */
  nextPage(): void { this.goToPage(this.nextIndexFrom(this.currentPage(), +1)); }
  /** Advance toward the start (previous screen). */
  prevPage(): void { this.goToPage(this.nextIndexFrom(this.currentPage(), -1)); }

  /** Next index in reading order, spread-aware (steps over the current spread). */
  private nextIndexFrom(from: number, dir: 1 | -1): number {
    if (this.view() !== 'spread') return from + dir;
    const groups = this.spreads();
    const gi = groups.findIndex((g) => g.includes(from));
    if (gi === -1) return from + dir;
    const target = groups[gi + dir];
    return target ? target[0] : from + dir;
  }

  private goToPage(index: number): void {
    const clamped = Math.min(Math.max(index, 0), this.pageCount() - 1);
    if (clamped === this.currentPage()) return;
    this.currentPage.set(clamped);
    this.pageLoading.set(true);
    this.saveProgress();
  }

  /**
   * Edge-tap navigation. The invisible left/right zones ARE direction-aware
   * (tap the right side in RTL to go back) — this is expected reader behavior and
   * distinct from requirement 2, which is about the visible chevron controls.
   */
  onEdge(side: 'prev' | 'next'): void {
    const forward = side === 'next';
    (forward !== (this.direction() === 'rtl')) ? this.nextPage() : this.prevPage();
  }

  goBack(): void { this.saveProgress(); this.router.navigate(['/']); }

  toggleFullscreen(): void {
    if (!document.fullscreenElement) {
      document.documentElement.requestFullscreen?.();
    } else {
      document.exitFullscreen?.();
    }
    // isFullscreen() is updated by the fullscreenchange listener.
  }

  setFitMode(mode: FitMode): void { this.fitMode.set(mode); }
  toggleDirection(): void { this.direction.update((d) => (d === 'ltr' ? 'rtl' : 'ltr')); }

  // --- Webtoon width (requirement 6): per-device preference in localStorage ---

  private static readonly WebtoonWidthKey = 'mangaplex-webtoon-width';

  setWebtoonWidth(pct: number): void {
    const clamped = Math.min(100, Math.max(30, Math.round(pct)));
    this.webtoonWidthPct.set(clamped);
    try { localStorage.setItem(ReaderComponent.WebtoonWidthKey, String(clamped)); } catch { /* private mode */ }
  }

  private loadWebtoonWidth(): number {
    try {
      const raw = localStorage.getItem(ReaderComponent.WebtoonWidthKey);
      const n = raw ? parseInt(raw, 10) : NaN;
      if (!Number.isNaN(n)) return Math.min(100, Math.max(30, n));
    } catch { /* private mode / unavailable */ }
    return 70; // sensible default
  }

  setView(view: ReaderView): void {
    const wasWebtoon = this.view() === 'webtoon';
    this.view.set(view);
    if (view === 'webtoon' && !wasWebtoon) {
      queueMicrotask(() => this.scrollWebtoonTo(this.currentPage()));
    }
  }

  // --- Webtoon scroll tracking ---

  onWebtoonScroll(): void {
    const el = this.scroller()?.nativeElement;
    if (!el) return;
    // The "current" page is the one crossing the vertical center of the viewport.
    const center = el.scrollTop + el.clientHeight / 2;
    const imgs = el.querySelectorAll<HTMLElement>('.webtoon-page');
    let idx = this.currentPage();
    for (let i = 0; i < imgs.length; i++) {
      const top = imgs[i].offsetTop;
      const bottom = top + imgs[i].offsetHeight;
      if (center >= top && center < bottom) { idx = i; break; }
    }
    if (idx !== this.currentPage()) {
      this.currentPage.set(idx);
      // Debounce progress writes while scrolling.
      if (this.webtoonSaveTimer) clearTimeout(this.webtoonSaveTimer);
      this.webtoonSaveTimer = setTimeout(() => this.saveProgress(), 600);
    }
  }

  private scrollWebtoonTo(index: number): void {
    const el = this.scroller()?.nativeElement;
    if (!el) return;
    const img = el.querySelectorAll<HTMLElement>('.webtoon-page')[index];
    if (img) el.scrollTop = img.offsetTop;
  }

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

  /**
   * Group page indices into double-spread pairs. A standalone cover (page 0) and
   * an odd trailing page each occupy a spread alone; everything else is paired.
   * Indices are ascending within a pair — the template's `.rtl-flow` handles
   * right-to-left placement, so navigation can step whole groups either way.
   */
  private computeSpreads(): number[][] {
    const n = this.pageCount();
    if (n === 0) return [];
    const groups: number[][] = [];
    let i = 0;
    if (this.coverIsStandalone()) { groups.push([0]); i = 1; }
    for (; i < n; i += 2) {
      groups.push(i + 1 < n ? [i, i + 1] : [i]);
    }
    return groups;
  }
}
