import { Component, inject, signal, computed, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatChipsModule } from '@angular/material/chips';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatMenuModule } from '@angular/material/menu';
import { MatButtonModule } from '@angular/material/button';

import { ApiService } from '../../core/api/api.service';
import { CoverImageDirective } from '../../shared/cover-image.directive';
import {
  LibraryDto,
  ContinueReadingEntry,
  LibraryViewPreferencesDto,
  RecentChaptersDto,
  RecentChaptersLibraryGroup,
  RecentChapterStack,
} from '../../core/api/api-types';
import { readerModeGlyph } from '../../shared/reader-mode-glyph';

/**
 * Home page. As of 1.5.0 the library **sidebar was promoted to the app shell**
 * (`LibrarySidebarComponent`, rendered by `layout.component`), so home no longer
 * owns a sidebar and no longer filters its continue-reading by a locally selected
 * library. Home shows three sections:
 *   1. the consolidated **Continue reading** row across all (non-Private, while
 *      incognito) libraries,
 *   2. the **New chapters** row - as of 1.12.0 one STACKED card per top-level unit
 *      (a top-level folder with recently-added descendant archives, or a loose
 *      top-level archive), grouped by library, newest activity first, and
 *   3. the **library grid**, each card showing a reading-direction indicator
 *      (Task C) derived from `LibraryDto.defaultReaderMode`.
 *
 * 1.12.0 home polish (Lane B):
 *   - Stacked cards (`RecentChapterStack`): the cover, the unit's name, the newest
 *     chapter's name, and a "+N" badge when the stack holds more than one new
 *     chapter. Tapping a FOLDER stack opens that folder's browse with the
 *     "Recently updated" sort so the freshest chapter leads; tapping a loose
 *     ARCHIVE stack opens the reader on it.
 *   - Card-size slider: REUSES the library browse card-size preference
 *     (`LibraryViewPreferencesDto.cardSize`) so home and library cards stay the
 *     same size; no new backend field. The whole preferences blob is echoed back on
 *     save so the browse view's other fields are never clobbered.
 *   - Library picker: which libraries contribute cards is a PER-USER, SERVER-SIDE
 *     setting (`GET/PUT /reading/home-libraries`, an excluded set), so it follows
 *     the user across devices. Independent of the Private designation.
 *
 * Folder-tap sort: the browse view resolves its sort from the STORED per-user
 * preference (it loads `library-preferences` before its first browse request and
 * has no transient/query-param sort), so "open sorted recentlyUpdated" is
 * implemented by persisting `sort: 'recentlyUpdated'` (descending, per the 1.10.4
 * recency rule) and THEN navigating - the same effect as picking "Recently
 * updated" in the browse View menu. The PUT is skipped when it is already the
 * stored sort.
 *
 * Switching libraries is a shell-sidebar navigation (routes to
 * `/libraries/:id/browse`), not an in-home selection. Hiding of Private libraries
 * remains server-enforced via the `X-Incognito` header for every discovery call.
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
    MatMenuModule,
    MatButtonModule,
    CoverImageDirective,
  ],
  template: `
    <div class="home" [style.--card-size]="cardSize() + 'px'">
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

      <!-- New chapters (1.12.0): shown whenever the user can see at least one library
           (so the picker stays reachable even when every library is hidden or nothing
           is new); the body is the stacked cards or a one-line empty state. -->
      @if (libraries().length > 0 || visibleGroups().length > 0) {
        <section class="strip-section recent-section">
          <div class="section-head">
            <h3>New chapters</h3>
            <div class="section-tools">
              <!-- Card size (shared with the library browse view). Dragging resizes the
                   home cards live (input); releasing persists the preference (change). -->
              <div class="size-control" matTooltip="Card size">
                <mat-icon class="size-icon">zoom_out</mat-icon>
                <input type="range" class="size-slider" aria-label="Card size"
                       [min]="cardSizeMin" [max]="cardSizeMax" [step]="cardSizeStep"
                       [value]="cardSize()"
                       (input)="onCardSizeInput($event)"
                       (change)="onCardSizeChange($event)">
                <mat-icon class="size-icon">zoom_in</mat-icon>
              </div>
              <!-- Library picker: per-library show/hide for THIS row. The trigger wears
                   the accent while any library is hidden so the narrowed row reads at a
                   glance (same idiom as the browse filter button). -->
              @if (libraries().length > 0) {
                <button mat-icon-button class="lib-picker-toggle" [matMenuTriggerFor]="libMenu"
                        [class.filter-active]="excludedLibraryIds().size > 0"
                        matTooltip="Choose which libraries show new chapters"
                        aria-label="Choose which libraries show new chapters">
                  <mat-icon>tune</mat-icon>
                </button>
                <mat-menu #libMenu="matMenu" class="view-options-menu home-lib-menu">
                  <span class="menu-caption">Show new chapters from</span>
                  @for (lib of libraries(); track lib.id) {
                    <button mat-menu-item role="menuitemcheckbox" class="lib-pick"
                            [class.selected-option]="isLibraryShown(lib.id)"
                            [attr.aria-checked]="isLibraryShown(lib.id)"
                            (click)="toggleLibrary($event, lib.id)">
                      <mat-icon>{{ isLibraryShown(lib.id) ? 'check_box' : 'check_box_outline_blank' }}</mat-icon>
                      {{ lib.name }}
                    </button>
                  }
                </mat-menu>
              }
            </div>
          </div>

          @if (pickerError()) {
            <p class="error">{{ pickerError() }}</p>
          }

          @for (group of visibleGroups(); track group.libraryId) {
            <div class="recent-lib">
              <h4 [routerLink]="['/libraries', group.libraryId, 'browse']">{{ group.libraryName }}</h4>
              <div class="strip">
                @for (stack of group.stacks; track stack.id) {
                  @if (stack.isFolder) {
                    <!-- Folder stack: a real link (middle-click / open-in-new-tab keep
                         working) whose plain click persists the recentlyUpdated sort
                         before routing so the freshest chapter leads. -->
                    <a class="stack-card" [attr.href]="folderHref(group, stack)"
                       (click)="openFolder($event, group, stack)">
                      <ng-container *ngTemplateOutlet="stackBody; context: { $implicit: stack }" />
                    </a>
                  } @else {
                    <!-- Loose top-level archive: its own stack; tap opens the reader. -->
                    <a class="stack-card" [routerLink]="['/reader', stack.latestItemId]">
                      <ng-container *ngTemplateOutlet="stackBody; context: { $implicit: stack }" />
                    </a>
                  }
                }
              </div>
            </div>
          } @empty {
            <p class="muted empty">
              @if (excludedLibraryIds().size > 0) {
                Nothing new in the libraries shown here.
              } @else {
                Nothing new yet.
              }
            </p>
          }
        </section>
      }

      <!-- One stacked card. The "stacked" paper edges behind the cover appear only
           when the unit holds more than one new chapter; the +N badge says how many. -->
      <ng-template #stackBody let-stack>
        <div class="stack" [class.stacked]="stack.newCount > 1">
          <div class="cover">
            @if (stack.coverUrl) {
              <img appCover [src]="stack.coverUrl" alt="" loading="lazy">
            }
            <mat-icon class="cover-fallback">{{ stack.isFolder ? 'folder' : 'menu_book' }}</mat-icon>
            @if (stack.newCount > 1) {
              <span class="badge new" [matTooltip]="stack.newCount + ' new chapters'"
                    [attr.aria-label]="stack.newCount + ' new chapters'">+{{ stack.newCount }}</span>
            }
          </div>
        </div>
        <div class="cont-title" [title]="stack.displayName">{{ stack.displayName }}</div>
        @if (stack.isFolder) {
          <div class="cont-page latest" [title]="stack.latestItemName">{{ stack.latestItemName }}</div>
        } @else {
          <div class="cont-page latest">Added {{ stack.latestAddedAt | date:'mediumDate' }}</div>
        }
      </ng-template>

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
    .error { color: #ff8a80; font-size: 13px; margin: 0 0 8px; }
    /* Home is a plain content page now (the shell provides the gutter padding and
       the library sidebar); no in-component sidebar/flex layout remains. */
    .strip-section { margin-bottom: 28px; }
    /* Both cover strips size their cards from the shared --card-size custom property
       (the library browse card-size preference), so home and library cards match.
       The top/right padding leaves room for the stacked-paper edges behind a card. */
    .strip { display: flex; gap: 16px; overflow-x: auto; padding: 8px 8px 8px 0; }
    .cont-wrap { position: relative; flex: 0 0 auto; width: var(--card-size, 150px); }
    .cont-card, .stack-card {
      display: block; flex: 0 0 auto; width: var(--card-size, 150px);
      text-decoration: none; color: inherit;
    }
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
      position: relative; width: 100%; aspect-ratio: 2 / 3; border-radius: 8px;
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
    .latest { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    /* New chapters: a heading row with the card-size slider + library picker on the
       right, then one sub-row per library, headed by the library name (clickable
       into browse), then its stacks. */
    .section-head {
      display: flex; align-items: center; gap: 12px; flex-wrap: wrap; margin-bottom: 4px;
    }
    .section-head h3 { flex: 1 1 auto; margin-bottom: 8px; }
    .section-tools { display: flex; align-items: center; gap: 8px; }
    .size-control { display: flex; align-items: center; gap: 6px; flex: 0 0 auto; }
    .size-control .size-icon { font-size: 18px; width: 18px; height: 18px; color: #8a8a99; }
    .size-slider {
      width: 120px; max-width: 34vw; accent-color: #7c4dff; cursor: pointer;
      background: transparent;
    }
    .lib-picker-toggle { color: #8a8a99; }
    .lib-picker-toggle.filter-active { color: #b39dff; }
    .menu-caption {
      display: block; padding: 6px 16px 2px; font-size: 11px; font-weight: 600;
      text-transform: uppercase; letter-spacing: 0.5px; color: #8a8a99;
    }
    /* The picker renders in a CDK overlay outside this component's DOM, so its
       selected-state uses the same accent highlight idiom as the browse menus. */
    ::ng-deep .home-lib-menu .selected-option { background: rgba(124, 77, 255, 0.16); }
    ::ng-deep .home-lib-menu .selected-option,
    ::ng-deep .home-lib-menu .selected-option .mat-icon { color: #b39dff; }
    .empty { margin: 4px 0 0; }
    .recent-lib { margin-bottom: 12px; }
    .recent-lib h4 {
      margin: 4px 0 4px; font-size: 14px; font-weight: 500; color: #cfcfd4;
      cursor: pointer; text-decoration: none; display: inline-block;
    }
    .recent-lib h4:hover { color: #fff; }
    /* Stacked-paper affordance: two offset sheets behind the cover when the unit
       holds more than one new chapter. The cover sits above them. */
    .stack { position: relative; }
    .stack .cover { position: relative; z-index: 1; }
    .stack.stacked::before, .stack.stacked::after {
      content: ''; position: absolute; inset: 0; border-radius: 8px; z-index: 0;
      background: rgba(255,255,255,0.10); border: 1px solid rgba(255,255,255,0.08);
    }
    .stack.stacked::before { transform: translate(4px, -4px); }
    .stack.stacked::after { transform: translate(8px, -8px); opacity: 0.55; }
    .stack-card:hover .cover, .stack-card:focus-visible .cover { outline: 2px solid rgba(124,77,255,0.6); outline-offset: 1px; }
    .badge {
      position: absolute; top: 6px; right: 6px; z-index: 2;
      font-size: 11px; font-weight: 700; padding: 2px 7px; border-radius: 10px;
      background: rgba(124, 77, 255, 0.92); color: #fff; letter-spacing: 0.2px;
    }
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
  private readonly router = inject(Router);

  readonly loading = signal(true);
  readonly libraries = signal<LibraryDto[]>([]);
  readonly continueReading = signal<ContinueReadingEntry[]>([]);
  /** New chapters (1.12.0): per-library groups of stacks, as returned by the server. */
  readonly recentGroups = signal<RecentChaptersLibraryGroup[]>([]);

  /**
   * Home library visibility (1.12.0): the per-user, server-persisted EXCLUDED set.
   * A library is shown when it is not in this set. Groups of a just-hidden library
   * are dropped locally right away (`visibleGroups`); un-hiding refetches the row.
   */
  readonly excludedLibraryIds = signal<ReadonlySet<string>>(new Set());
  readonly pickerSaving = signal(false);
  readonly pickerError = signal<string | null>(null);

  /** Groups that have at least one stack and whose library is not hidden from home. */
  readonly visibleGroups = computed(() => {
    const excluded = this.excludedLibraryIds();
    return this.recentGroups().filter((g) => g.stacks.length > 0 && !excluded.has(g.libraryId));
  });

  // Card size (1.12.0): the SAME preference the library browse view uses
  // (LibraryViewPreferencesDto.cardSize, a stringified px value), same slider range.
  readonly cardSizeMin = 110;
  readonly cardSizeMax = 260;
  readonly cardSizeStep = 10;
  readonly defaultCardSize = 150;
  readonly cardSize = signal<number>(this.defaultCardSize);
  /** Last-loaded library-view preferences, echoed back on save so nothing else is lost. */
  private libraryPrefs: LibraryViewPreferencesDto | null = null;

  ngOnInit(): void {
    this.api.getLibraries().subscribe({
      next: (libs) => { this.libraries.set(libs); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
    this.api.getContinueReading(12).subscribe({
      next: (items) => this.continueReading.set(items),
      error: () => this.continueReading.set([]),
    });
    this.loadRecentChapters();
    this.api.getLibraryPreferences().subscribe({
      next: (p) => {
        this.libraryPrefs = p;
        this.cardSize.set(this.resolveCardSize(p));
      },
      error: () => { /* keep the default size; the slider still works this session */ },
    });
    this.api.getHomeLibraries().subscribe({
      next: (dto) => this.excludedLibraryIds.set(new Set(dto.excludedLibraryIds)),
      error: () => { /* leave the set empty: showing every library is the safe default */ },
    });
  }

  private loadRecentChapters(): void {
    this.api.getRecentChapters(12).subscribe({
      next: (dto: RecentChaptersDto) => this.recentGroups.set(dto.libraries),
      error: () => this.recentGroups.set([]),
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

  // --- Stacked cards (1.12.0) ---

  /** Folder-stack link target: the top-level folder's browse view. */
  folderHref(group: RecentChaptersLibraryGroup, stack: RecentChapterStack): string {
    return `/libraries/${group.libraryId}/browse/${stack.id}`;
  }

  /**
   * Open a folder stack with the freshest chapter leading. Modified/non-primary
   * clicks are left to the browser (open in new tab keeps the plain href); a plain
   * click persists the "Recently updated" browse sort (descending) and then routes,
   * so the browse view - which resolves its sort from the stored preference - lists
   * the folder newest-first. Navigation proceeds even if the persist fails.
   */
  openFolder(event: MouseEvent, group: RecentChaptersLibraryGroup, stack: RecentChapterStack): void {
    if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    event.preventDefault();
    const go = () => { void this.router.navigate(['/libraries', group.libraryId, 'browse', stack.id]); };
    const prefs = this.libraryPrefs;
    if (prefs && prefs.sort === 'recentlyUpdated' && prefs.direction === 'desc') { go(); return; }
    const body: LibraryViewPreferencesDto = {
      ...(prefs ?? this.defaultPrefs()),
      sort: 'recentlyUpdated',
      direction: 'desc',
    };
    this.api.setLibraryPreferences(body).subscribe({
      next: () => { this.libraryPrefs = body; go(); },
      error: () => go(),
    });
  }

  // --- Card size (1.12.0, shared with the library browse view) ---

  /** Live preview while dragging: updates only the signal (no PUT per pixel). */
  onCardSizeInput(event: Event): void {
    this.cardSize.set(this.clampCardSize(Number((event.target as HTMLInputElement).value)));
  }

  /** Commit the card size when the slider is released: persists it. */
  onCardSizeChange(event: Event): void {
    this.setCardSize(Number((event.target as HTMLInputElement).value));
  }

  /**
   * Set the card size (clamped to the slider range) and persist it into the shared
   * library-view preference, echoing the rest of the last-loaded blob so the browse
   * view's viewMode / sort / direction / page size round-trip untouched.
   */
  setCardSize(px: number): void {
    const size = this.clampCardSize(px);
    this.cardSize.set(size);
    const body: LibraryViewPreferencesDto = {
      ...(this.libraryPrefs ?? this.defaultPrefs()),
      cardSize: String(size),
    };
    this.api.setLibraryPreferences(body).subscribe({
      next: () => { this.libraryPrefs = body; },
      error: () => { /* non-fatal: the size still applies this session */ },
    });
  }

  private clampCardSize(px: number): number {
    if (!Number.isFinite(px)) return this.defaultCardSize;
    return Math.min(this.cardSizeMax, Math.max(this.cardSizeMin, Math.round(px)));
  }

  /**
   * Resolve the initial card size the same way the browse view does: the stored
   * numeric cardSize when usable, else the px width the legacy viewMode+density
   * combination used, else the default - so home matches the library exactly.
   */
  private resolveCardSize(p: LibraryViewPreferencesDto): number {
    const stored = Number(p.cardSize);
    if (p.cardSize != null && p.cardSize !== '' && Number.isFinite(stored)) {
      return this.clampCardSize(stored);
    }
    const compact = p.density === 'compact';
    if (p.viewMode === 'poster') return compact ? 168 : 210;
    if (p.viewMode === 'grid') return compact ? 112 : 150;
    return this.defaultCardSize;
  }

  /** Fallback blob when the preferences never loaded (mirrors the server defaults). */
  private defaultPrefs(): LibraryViewPreferencesDto {
    return { viewMode: 'card', density: 'comfortable', sort: 'name' };
  }

  // --- Library picker (1.12.0, per-user server setting) ---

  isLibraryShown(libraryId: string): boolean {
    return !this.excludedLibraryIds().has(libraryId);
  }

  /**
   * Show/hide one library on this row. Sends the whole updated excluded set
   * (replacement semantics) and keeps the menu open so several libraries can be
   * toggled in one go. Reverts on failure; refetches the row on success so a
   * just-shown library's stacks appear (a just-hidden one drops out immediately).
   */
  toggleLibrary(event: Event, libraryId: string): void {
    event.stopPropagation(); // keep the menu open (the panel closes on bubbled clicks)
    const previous = this.excludedLibraryIds();
    const next = new Set(previous);
    if (next.has(libraryId)) next.delete(libraryId); else next.add(libraryId);
    this.excludedLibraryIds.set(next);
    this.pickerSaving.set(true);
    this.pickerError.set(null);
    this.api.putHomeLibraries([...next]).subscribe({
      next: () => { this.pickerSaving.set(false); this.loadRecentChapters(); },
      error: (err: { message?: string }) => {
        this.excludedLibraryIds.set(previous);
        this.pickerSaving.set(false);
        this.pickerError.set(err?.message || 'Failed to update which libraries show new chapters');
      },
    });
  }
}
