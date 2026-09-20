import { Component, inject, signal, computed, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatChipsModule } from '@angular/material/chips';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';

import { ApiService } from '../../core/api/api.service';
import { CoverImageDirective } from '../../shared/cover-image.directive';
import {
  CatalogNodeDto,
  LibraryDto,
  ContinueReadingEntry,
  LibraryReadStateFilter,
  LibraryViewPreferencesDto,
  RecentChaptersDto,
  RecentChaptersLibraryGroup,
  RecentChapterStack,
} from '../../core/api/api-types';
import { readerModeGlyph } from '../../shared/reader-mode-glyph';

/**
 * Home page. The library **sidebar was promoted to the app shell**
 * (`LibrarySidebarComponent`, rendered by `layout.component`), so home no longer
 * owns a sidebar and no longer filters its continue-reading by a locally selected
 * library. Home shows three sections:
 *   1. the consolidated **Continue reading** row across all (non-Private, while
 *      incognito) libraries,
 *   2. the **New chapters** row - one STACKED card per top-level unit (a
 *      top-level folder with recently-added descendant archives, or a loose
 *      top-level archive), grouped by library, newest activity first, and
 *   3. the **library grid**, each card showing a reading-direction indicator
 *      derived from `LibraryDto.defaultReaderMode`.
 *
 * Additional details:
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
 * Folder-tap sort: a folder card routes with a TRANSIENT `?sort=recentlyUpdated`
 * query param that the browse view honours for that view ONLY (descending, per the
 * 1.10.4 recency rule) - it does NOT persist to the user's library preference, so
 * opening a library normally stays Name-ascending. The
 * plain href carries the same query param so open-in-new-tab is consistent.
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
    MatButtonModule,
    MatCardModule,
    MatIconModule,
    MatChipsModule,
    MatMenuModule,
    MatTooltipModule,
    CoverImageDirective,
  ],
  template: `
    <div class="home" [style.--card-size]="cardSize() + 'px'">
      <!-- Home view toolbar (1.12.0; scope caption added 1.20.0): the card-size slider
           lives here because it sizes BOTH cover strips (Continue reading + New chapters),
           not just one row - mirroring the library browse view's top card-size control.
           Shown once there is any card content to size. The "Card size" caption + page-level
           icon label the control's SCOPE (all rows on Home) so it doesn't read as belonging
           to whichever section happens to sit under it. -->
      @if (continueReading().length > 0 || visibleGroups().length > 0) {
        <div class="home-toolbar">
          <div class="toolbar-scope" matTooltip="Applies to all rows on Home">
            <mat-icon class="size-icon">dashboard</mat-icon>
            <span class="scope-label">Card size</span>
          </div>
          <div class="size-control">
            <mat-icon class="size-icon">zoom_out</mat-icon>
            <input type="range" class="size-slider" aria-label="Card size"
                   [min]="cardSizeMin" [max]="cardSizeMax" [step]="cardSizeStep"
                   [value]="cardSize()"
                   (input)="onCardSizeInput($event)"
                   (change)="onCardSizeChange($event)">
            <mat-icon class="size-icon">zoom_in</mat-icon>
          </div>
        </div>
      }

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

      <!-- Favorites row (1.21.0): opt-in (showFavoritesHomeRow), rendered only when the
           user has favorites. Reuses the Continue-reading strip card styling. -->
      @if (showFavoritesRow() && favorites().length > 0) {
        <section class="strip-section">
          <h3>Favorites</h3>
          <div class="strip">
            @for (node of favorites(); track node.id) {
              <a class="cont-card" [routerLink]="favLink(node)">
                <div class="cover">
                  @if (favCover(node); as src) { <img appCover [src]="src" alt="" loading="lazy"> }
                  <mat-icon class="cover-fallback">{{ node.kind === 'Folder' ? 'folder' : 'menu_book' }}</mat-icon>
                </div>
                <div class="cont-title" [title]="node.displayName">{{ node.displayName }}</div>
              </a>
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
            <!-- Section-scope divider (1.20.0): a small accent between the heading and its
                 own filter, so the Filter button reads as tightly grouped with "New chapters"
                 rather than a stray control floating in the row. -->
            <span class="head-divider" aria-hidden="true"></span>
            <!-- Read-state filter (1.17.0): scoped to New chapters only, mirroring the
                 library browse view's filter but transient (session-only, not a
                 persisted preference) since this row is a discovery surface, not a
                 navigable listing. -->
            <button mat-stroked-button class="filter-toggle" [matMenuTriggerFor]="recentFilterMenu"
                    [class.filter-active]="recentReadStateFilter() !== 'all'"
                    matTooltip="Filter by read state" aria-label="Filter new chapters by read state">
              <mat-icon>filter_list</mat-icon> {{ recentReadStateLabel() }}
            </button>
            <mat-menu #recentFilterMenu="matMenu" class="view-options-menu">
              <span class="menu-caption">Show</span>
              @for (opt of recentReadStateOptions; track opt.value) {
                <button mat-menu-item role="menuitemradio"
                        [class.selected-option]="recentReadStateFilter() === opt.value"
                        [attr.aria-checked]="recentReadStateFilter() === opt.value"
                        (click)="setRecentReadStateFilter(opt.value)">
                  <mat-icon>{{ opt.icon }}</mat-icon>
                  {{ opt.label }}
                </button>
              }
            </mat-menu>
          </div>

          @for (group of visibleGroups(); track group.libraryId) {
            <div class="recent-lib">
              <h4 [routerLink]="['/libraries', group.libraryId, 'browse']">{{ group.libraryName }}</h4>
              <div class="strip">
                @for (stack of group.stacks; track stack.id) {
                  @if (stack.isFolder) {
                    <!-- Folder stack: a real link (middle-click / open-in-new-tab keep
                         working) whose plain click routes with a transient
                         recentlyUpdated sort so the freshest chapter leads, without
                         changing the user's persisted library sort. -->
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
            <!-- Read-state marker (1.20.0): same visual vocabulary as the library browse
                 view's per-card marker (green "Read", purple "Reading"); Unread renders
                 nothing, the quiet default. Placed top-left so it never collides with the
                 top-right "+N new" badge. -->
            @if (readStateView(stack.readState); as rv) {
              <span class="badge read-state" [class.read]="rv.kind === 'read'" [class.reading]="rv.kind === 'reading'"
                    [matTooltip]="rv.tooltip" [attr.aria-label]="rv.tooltip" role="img">{{ rv.text }}</span>
            }
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
    /* Home view toolbar: the card-size slider that sizes BOTH cover strips. Mirrors the
       library browse view's top card-size control; wraps on narrow phones and the slider
       stays wide enough to drag with a thumb. */
    .home-toolbar {
      display: flex; align-items: center; gap: 8px; flex-wrap: wrap;
      margin: 0 0 12px; padding: 0 8px 0 0;
    }
    /* Toolbar-scope caption (1.20.0): labels the slider's reach so it reads as page-level
       chrome, not a control belonging to whichever section sits under it. */
    .toolbar-scope {
      display: flex; align-items: center; gap: 6px; flex: 0 0 auto;
      color: #8a8a99; font-size: 11px; font-weight: 600; text-transform: uppercase;
      letter-spacing: .4px; padding-right: 8px; border-right: 1px solid rgba(255,255,255,.1);
    }
    .size-control { display: flex; align-items: center; gap: 6px; flex: 0 0 auto; }
    .size-control .size-icon { font-size: 18px; width: 18px; height: 18px; color: #8a8a99; }
    .size-slider {
      width: 180px; max-width: 60vw; accent-color: #7c4dff; cursor: pointer;
      background: transparent;
    }
    /* New chapters: a heading, then one sub-row per library headed by the library name
       (clickable into browse), then its stacks. Which libraries appear here is a per-user
       setting under Settings > New Chapters. */
    .section-head { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; margin-bottom: 4px; }
    .section-head h3 { margin-bottom: 8px; }
    /* Section-scope divider (1.20.0): ties the Filter button to the "New chapters" heading
       it scopes to, instead of floating unattached in the row. Purely decorative. */
    .head-divider { width: 1px; height: 16px; background: rgba(255,255,255,.14); margin: 0 -4px 8px 0; }
    /* Read-state filter (1.17.0): mirrors the library browse toolbar's filter button
       and menu styling so the two filters read as the same control at a glance. */
    .menu-caption {
      display: block; padding: 6px 16px 2px; font-size: 11px; font-weight: 600;
      text-transform: uppercase; letter-spacing: 0.5px; color: #8a8a99;
    }
    ::ng-deep .view-options-menu .selected-option {
      background: rgba(124, 77, 255, 0.16);
    }
    ::ng-deep .view-options-menu .selected-option,
    ::ng-deep .view-options-menu .selected-option .mat-icon {
      color: #b39dff;
    }
    .filter-toggle { margin-bottom: 8px; }
    .filter-toggle mat-icon { margin-right: 4px; }
    .filter-toggle.filter-active { border-color: #7c4dff; color: #b39dff; }
    .filter-toggle.filter-active mat-icon { color: #b39dff; }
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
    /* Read-state marker (1.20.0): top-left (browse's own Read/Reading colours), so it never
       collides with the top-right "+N new" badge; Unread renders nothing (quiet default). */
    .badge.read-state { left: 6px; right: auto; }
    .badge.read-state.read { background: rgba(76, 175, 80, .95); }
    .library-grid {
      display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 16px;
    }
    .library-card { cursor: pointer; position: relative; }
    .lib-icon { font-size: 40px; width: 40px; height: 40px; color: #888; }
    /* Reading-direction indicator: a subtle glyph in the card's top-right
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
  /** Opt-in Home "Favorites" row (1.21.0): hidden unless showFavoritesHomeRow is on. */
  readonly showFavoritesRow = signal(false);
  readonly favorites = signal<CatalogNodeDto[]>([]);
  /** New chapters (1.12.0): per-library groups of stacks, as returned by the server. */
  readonly recentGroups = signal<RecentChaptersLibraryGroup[]>([]);

  /**
   * Home library visibility (1.12.0): the per-user, server-persisted EXCLUDED set, read
   * on load to filter which libraries contribute New-chapters cards. The picker that
   * EDITS this set lives under Settings > New Chapters (1.12.0); home
   * only reads it here.
   */
  readonly excludedLibraryIds = signal<ReadonlySet<string>>(new Set());

  /** Groups that have at least one stack and whose library is not hidden from home. */
  readonly visibleGroups = computed(() => {
    const excluded = this.excludedLibraryIds();
    return this.recentGroups().filter((g) => g.stacks.length > 0 && !excluded.has(g.libraryId));
  });

  /**
   * New chapters read-state filter (1.17.0): restricts the row to All / Reading /
   * Read / Unread stacks, mirroring the library browse view's filter (server-side,
   * additive `readState` param on the recent-chapters endpoint). A TRANSIENT,
   * session-only toolbar control — not round-tripped through any persisted
   * preference — scoped to this row ONLY (the library grid and continue-reading
   * row are unaffected). 'all' sends no param (server default = unfiltered).
   */
  readonly recentReadStateFilter = signal<LibraryReadStateFilter>('all');
  readonly recentReadStateOptions: { value: LibraryReadStateFilter; label: string; icon: string }[] = [
    { value: 'all', label: 'All', icon: 'filter_list' },
    { value: 'reading', label: 'Reading', icon: 'auto_stories' },
    { value: 'read', label: 'Read', icon: 'check_circle' },
    { value: 'unread', label: 'Unread', icon: 'radio_button_unchecked' },
  ];

  /** Toolbar button label: "Filter" when inactive, else the active option's label. */
  readonly recentReadStateLabel = computed(() =>
    this.recentReadStateFilter() === 'all'
      ? 'Filter'
      : this.recentReadStateOptions.find((o) => o.value === this.recentReadStateFilter())?.label ?? 'Filter');

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
        // Home "Favorites" row (1.21.0): opt-in, off by default. Only fetch when on.
        this.showFavoritesRow.set(p.showFavoritesHomeRow ?? false);
        if (this.showFavoritesRow()) this.loadFavorites();
      },
      error: () => { /* keep the default size; the slider still works this session */ },
    });
    this.api.getHomeLibraries().subscribe({
      next: (dto) => this.excludedLibraryIds.set(new Set(dto.excludedLibraryIds)),
      error: () => { /* leave the set empty: showing every library is the safe default */ },
    });
  }

  private loadRecentChapters(): void {
    this.api.getRecentChapters(12, this.recentReadStateFilter()).subscribe({
      next: (dto: RecentChaptersDto) => this.recentGroups.set(dto.libraries),
      error: () => this.recentGroups.set([]),
    });
  }

  private loadFavorites(): void {
    this.api.getFavorites(null, 12).subscribe({
      next: (page) => this.favorites.set(page.items),
      error: () => this.favorites.set([]),
    });
  }

  /** Home row link: folders open browse, archives open the reader (1.21.0). */
  favLink(node: CatalogNodeDto): string[] {
    if (node.kind === 'Folder') return ['/libraries', node.libraryId, 'browse', node.id];
    return ['/reader', node.id];
  }

  /** Cover URL for a favorite row card: folder cover if resolved, else the archive cover. */
  favCover(node: CatalogNodeDto): string | null {
    return node.coverUrl ?? (node.kind === 'Archive' ? `/api/v1/items/${node.id}/cover` : null);
  }

  /** Change the New-chapters read-state filter and reload the row (1.17.0). */
  setRecentReadStateFilter(filter: LibraryReadStateFilter): void {
    if (this.recentReadStateFilter() === filter) return;
    this.recentReadStateFilter.set(filter);
    this.loadRecentChapters();
  }

  glyph(lib: LibraryDto) {
    return readerModeGlyph(lib.defaultReaderMode);
  }

  /**
   * Read-state marker view for a New-chapters stack (1.20.0): mirrors the library
   * browse view's per-card read marker (`✓ Read` green, `Reading` purple) and its
   * folder-rollup badge convention - `null` for Unread renders no marker at all, so
   * an unread stack looks exactly as quiet as an unread archive/folder card does in
   * browse. Exported logic kept inline (not a shared component) per this lane's
   * file ownership.
   */
  readStateView(state: RecentChapterStack['readState']): { kind: 'read' | 'reading'; text: string; tooltip: string } | null {
    switch (state) {
      case 'read':
        return { kind: 'read', text: '✓ Read', tooltip: 'All items read' };
      case 'reading':
        return { kind: 'reading', text: 'Reading', tooltip: 'Partially read' };
      default:
        return null;
    }
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
    return `/libraries/${group.libraryId}/browse/${stack.id}?sort=recentlyUpdated`;
  }

  /**
   * Open a folder stack with the freshest chapter leading. Modified/non-primary
   * clicks are left to the browser (open in new tab keeps the plain href, which
   * carries the same transient sort). A plain click routes with a TRANSIENT
   * `?sort=recentlyUpdated` query param that the browse view honours for this view
   * only - it does NOT change the user's persisted library sort, so opening a
   * library normally stays Name-ascending. (1.12.0)
   */
  openFolder(event: MouseEvent, group: RecentChaptersLibraryGroup, stack: RecentChapterStack): void {
    if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
    event.preventDefault();
    void this.router.navigate(['/libraries', group.libraryId, 'browse', stack.id],
      { queryParams: { sort: 'recentlyUpdated' } });
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

}
