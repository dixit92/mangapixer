import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { catchError, forkJoin, of } from 'rxjs';

import { ApiService } from '../../core/api/api.service';
import { AuthService } from '../../core/auth/auth.service';
import { FavoritesStateService } from '../../core/favorites/favorites-state.service';
import { ReadStateService } from '../../core/reading/read-state.service';
import { CatalogNodeDto, LibraryViewMode, VolumeSlotDto, VolumeStackDto } from '../../core/api/api-types';
import { CoverImageDirective } from '../../shared/cover-image.directive';
import { CoverSelectionActionComponent } from '../../shared/cover-picker/cover-selection-action.component';
import { CoverStateService } from '../../shared/cover-picker/cover-state.service';
import { InfoToggleComponent } from '../../shared/info-toggle/info-toggle.component';
import { NodeRowComponent } from '../../shared/node-row/node-row.component';
import { NodeSelection } from '../../shared/selection/node-selection';
import { SelectionBarComponent } from '../../shared/selection/selection-bar.component';
import { StarToggleComponent } from '../../shared/star-toggle/star-toggle.component';
import { MissingChapterCardComponent } from '../../shared/volume-stack/missing-chapter-card.component';
import { MetadataStateService } from '../metadata/metadata-state.service';
import { SeriesSelectionActionsComponent } from '../metadata/series-selection-actions.component';
import { OfficialReleaseBadgeComponent } from '../../shared/volume-stack/official-release-badge.component';
import { AlsoInVolumeBadgeComponent } from '../../shared/volume-stack/also-in-volume-badge.component';

/**
 * The stack view of one virtual volume (1.29.0): `/libraries/:libraryId/browse/:nodeId/volume/:key`. A header (cover,
 * "Volume 3", "8 of 10 chapters - 1 extra", where the grouping came from), previous / next volume, then the ordered slots:
 * chapter cards (their own cover, read state, star and (i) as in the folder list) with a dashed placeholder where a whole
 * chapter is missing. Opening a chapter opens the reader as usual; the reader's previous / next stay folder-level, and the
 * breadcrumbs name the REAL folder. Extras (45.5) show in their place and are never "missing".
 *
 * 1.30.0: the page follows the viewer's library view mode (Card or List - the same per-user preference the folder browse
 * uses) and offers the browse Select mode: a Select button, tap-to-select (phone included), Shift-click and long-press range
 * fill, and the shared selection bar (mark read / unread, favorites, and for admins the archive actions of browse: series
 * metadata, cover). The list row is the shared `app-node-row`; the selection is the shared `NodeSelection`. A missing-chapter
 * placeholder is never selectable.
 */
@Component({
  selector: 'app-volume-stack-view',
  standalone: true,
  imports: [
    RouterLink, MatButtonModule, MatIconModule, CoverImageDirective, InfoToggleComponent, StarToggleComponent,
    MissingChapterCardComponent, NodeRowComponent, SelectionBarComponent, SeriesSelectionActionsComponent,
    CoverSelectionActionComponent, OfficialReleaseBadgeComponent, AlsoInVolumeBadgeComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (selection.mode()) {
      <div class="bar selecting" role="toolbar" aria-label="Selection actions">
        <app-selection-bar [count]="selection.selected().size" [busy]="busy()" [selectDisabled]="items().length === 0"
                           (selectAll)="selection.selectAll()" (selectUnread)="selection.selectWhere(isUnread)"
                           (selectRead)="selection.selectWhere(isRead)" (markRead)="markRead($event)"
                           (favorite)="favorite($event)" (done)="selection.toggleMode()">
          @if (auth.isAdmin()) {
            <app-series-selection-actions [nodes]="items()" [selected]="selection.selected()" [disabled]="busy()" />
            <app-cover-selection-action [nodes]="items()" [selected]="selection.selected()" [disabled]="busy()" />
          }
        </app-selection-bar>
      </div>
    } @else {
      <nav class="bar" aria-label="Breadcrumb">
        <div class="crumbs">
          <a [routerLink]="['/libraries', libraryId(), 'browse']">{{ libraryName() || 'Library' }}</a>
          @for (crumb of breadcrumbs(); track crumb.id) {
            <span class="sep"> / </span>
            <a [routerLink]="['/libraries', libraryId(), 'browse', crumb.id]">{{ crumb.displayName }}</a>
          }
          @if (folderName()) {
            <span class="sep"> / </span>
            <a [routerLink]="folderLink()" data-testid="stack-folder-link">{{ folderName() }}</a>
          }
          @if (stack(); as s) {
            <span class="sep"> / </span>
            <span class="current" aria-current="page">{{ s.label }}</span>
          }
        </div>
        @if (stack(); as s) {
          <div class="nav">
            @if (s.previousKey) {
              <a mat-stroked-button [routerLink]="volumeLink(s.previousKey)" data-testid="stack-prev" aria-label="Previous volume">
                <mat-icon>chevron_left</mat-icon><span class="lbl">Previous</span>
              </a>
            }
            @if (s.nextKey) {
              <a mat-stroked-button [routerLink]="volumeLink(s.nextKey)" data-testid="stack-next" aria-label="Next volume">
                <span class="lbl">Next</span><mat-icon iconPositionEnd>chevron_right</mat-icon>
              </a>
            }
            @if (items().length > 0) {
              <button mat-stroked-button type="button" class="select-toggle" (click)="selection.toggleMode()" data-testid="stack-select">
                <mat-icon>checklist</mat-icon><span class="lbl">Select</span>
              </button>
            }
          </div>
        }
      </nav>
    }

    @if (stack(); as s) {
      <header class="head">
        <div class="head-cover">
          @if (s.coverUrl) { <img appCover [src]="s.coverUrl" alt=""> }
          <mat-icon class="fallback">menu_book</mat-icon>
        </div>
        <div class="head-text">
          <h1 data-testid="stack-title">{{ s.label }}</h1>
          <app-official-release-badge [language]="s.officialRelease" [overlay]="false" />
          <p class="counts" data-testid="stack-counts">{{ counts() }}</p>
          <p class="source" data-testid="stack-source">{{ sourceText() }}</p>
        </div>
      </header>

      <div class="slots" [class.list]="viewMode() === 'list'" [class.selecting]="selection.mode()"
           [style.--list-columns]="listColumns()" data-testid="stack-slots">
        @for (slot of s.slots; track slotKey(slot)) {
          @if (slot.kind === 'Missing') {
            <app-missing-chapter-card [chapter]="slot.chapter ?? '?'" [compact]="viewMode() === 'list'" />
          } @else if (slot.item; as item) {
            @if (viewMode() === 'list') {
              <app-node-row [node]="item" [link]="selection.mode() ? null : readerLink(item)" [subtitle]="subtitle(slot, item)"
                            [selectMode]="selection.mode()" [selected]="selection.isSelected(item)" [selectable]="true"
                            [rangePrompt]="selection.rangePromptNode()?.id === item.id" data-testid="stack-row"
                            (activate)="selection.click($event, item)" (rowSelect)="selection.rowSelect($event, item)"
                            (pressStart)="selection.pointerDown($event, item)" (pressEnd)="selection.pointerEnd()"
                            (selectToHere)="selection.selectToHere($event)" (dismissPrompt)="selection.dismissPrompt($event)" />
            } @else {
              <div class="slot-wrap" [class.selected]="selection.isSelected(item)">
                <a class="slot" [routerLink]="selection.mode() ? null : readerLink(item)" data-testid="stack-item"
                   (click)="selection.click($event, item)"
                   (pointerdown)="selection.pointerDown($event, item)"
                   (pointerup)="selection.pointerEnd()"
                   (pointercancel)="selection.pointerEnd()"
                   (pointerleave)="selection.pointerEnd()">
                  <div class="cover">
                    @if (item.coverUrl) { <img appCover [src]="item.coverUrl" alt="" loading="lazy"> }
                    <mat-icon class="fallback">menu_book</mat-icon>
                    <app-also-in-volume-badge [volume]="item.alsoInVolume" />
                    @if (item.isRead) {
                      <span class="badge read">✓ Read</span>
                    } @else if (item.readingState === 'InProgress') {
                      <span class="badge reading">Reading</span>
                    }
                    @if (selection.mode()) {
                      <span class="check" [class.on]="selection.isSelected(item)" data-testid="stack-check">
                        <mat-icon>{{ selection.isSelected(item) ? 'check_circle' : 'radio_button_unchecked' }}</mat-icon>
                      </span>
                    } @else {
                      <app-star-toggle [nodeId]="item.id" [favorite]="!!item.isFavorite" [overlay]="true" [compact]="true" />
                      <app-info-toggle [nodeId]="item.id" [hasSeriesInfo]="!!item.hasSeriesInfo" [overlay]="true" />
                    }
                    @if (selection.rangePromptNode()?.id === item.id) {
                      <div class="range-prompt">
                        <button type="button" (click)="selection.selectToHere($event)">Select to here</button>
                        <button type="button" class="cancel" (click)="selection.dismissPrompt($event)">Cancel</button>
                      </div>
                    }
                  </div>
                  <div class="text">
                    <div class="title" [title]="item.displayName">{{ item.displayName }}</div>
                    <div class="sub">{{ subtitle(slot, item) }}</div>
                  </div>
                </a>
              </div>
            }
          }
        }
      </div>
    } @else if (failed()) {
      <p class="empty" role="alert" data-testid="stack-unavailable">
        This volume is not available. <a [routerLink]="folderLink()">Back to the folder</a>
      </p>
    }
  `,
  styles: [`
    :host { display: block; }
    .bar {
      display: flex; align-items: center; gap: 12px; margin-bottom: 16px; padding: 10px 12px;
      background: #14141c; border: 1px solid rgba(255, 255, 255, 0.08); border-radius: 10px;
    }
    /* Select mode: the bar turns into the selection bar and stays reachable while a long volume scrolls (as in browse). */
    .bar.selecting { position: sticky; top: 0; z-index: 20; background: #1c1730; border-color: rgba(124, 77, 255, 0.5); }
    .crumbs { flex: 1 1 auto; min-width: 0; }
    .crumbs a { text-decoration: none; color: #b39dff; }
    .crumbs .current { color: #e6e6ee; font-weight: 500; }
    .nav { display: flex; gap: 8px; flex: 0 0 auto; }
    .select-toggle mat-icon { margin-right: 4px; }
    .head { display: flex; gap: 16px; align-items: flex-end; margin-bottom: 20px; }
    .head-cover {
      position: relative; width: 120px; aspect-ratio: 2 / 3; border-radius: 8px; overflow: hidden; flex: 0 0 auto;
      background: rgba(255, 255, 255, 0.06); display: flex; align-items: center; justify-content: center;
    }
    .head-cover img { position: relative; z-index: 1; width: 100%; height: 100%; object-fit: cover; }
    .fallback { position: absolute; z-index: 0; font-size: 40px; width: 40px; height: 40px; color: #777; }
    h1 { margin: 0 0 6px; font-size: 24px; }
    .counts { margin: 0 0 4px; color: #e6e6ee; }
    .source { margin: 0; color: #8a8a99; font-size: 13px; }
    .slots { display: grid; gap: 14px; grid-template-columns: repeat(auto-fill, minmax(140px, 1fr)); }
    /* List view: the shared row, one column on a phone and the viewer's list-column count from 960px (as in browse). */
    .slots.list { grid-template-columns: minmax(0, 1fr); gap: 8px; }
    .slot { display: block; color: inherit; text-decoration: none; }
    .slot-wrap { position: relative; border-radius: 8px; }
    .slot-wrap.selected { outline: 2px solid #7c4dff; outline-offset: 3px; }
    .cover {
      position: relative; aspect-ratio: 2 / 3; border-radius: 8px; overflow: hidden;
      background: rgba(255, 255, 255, 0.06); display: flex; align-items: center; justify-content: center;
    }
    .cover img { position: relative; z-index: 1; width: 100%; height: 100%; object-fit: cover; }
    .slot:hover .cover, .slot:focus-visible .cover { outline: 2px solid rgba(124, 77, 255, 0.6); outline-offset: 1px; }
    .badge {
      position: absolute; top: 6px; right: 6px; z-index: 2; font-size: 11px; font-weight: 600;
      padding: 2px 6px; border-radius: 10px; background: rgba(124, 77, 255, 0.9); color: #fff;
    }
    .badge.read { background: rgba(76, 175, 80, 0.95); }
    .check {
      position: absolute; top: 6px; left: 6px; z-index: 3; color: #fff; line-height: 0;
      border-radius: 50%; background: rgba(0, 0, 0, 0.45);
    }
    .check.on { color: #7c4dff; background: #fff; }
    .check mat-icon { font-size: 24px; width: 24px; height: 24px; }
    .range-prompt {
      position: absolute; inset: 0; z-index: 4;
      display: flex; flex-direction: column; align-items: center; justify-content: center;
      gap: 6px; padding: 8px; background: rgba(10, 8, 20, 0.85); border-radius: 8px;
    }
    .range-prompt button {
      border: none; border-radius: 6px; padding: 6px 10px; font-size: 12px; font-weight: 600;
      cursor: pointer; background: #7c4dff; color: #fff; width: 100%;
    }
    .range-prompt button.cancel { background: rgba(255, 255, 255, 0.12); }
    .title { margin-top: 6px; font-size: 13px; font-weight: 500; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .sub { font-size: 12px; color: #999; min-height: 1em; }
    .empty { color: #999; padding: 32px; text-align: center; }
    @media (min-width: 960px) {
      .slots.list { grid-template-columns: repeat(var(--list-columns, 2), minmax(0, 1fr)); column-gap: 16px; }
    }
    @media (max-width: 599.98px) {
      .lbl { display: none; }
      .select-toggle mat-icon { margin-right: 0; }
      /* The selection bar wraps as browse's does: the count, then the action icons, then Done - never a squeezed column. */
      .bar.selecting { flex-wrap: wrap; }
      .head-cover { width: 88px; }
      h1 { font-size: 20px; }
      .slots { grid-template-columns: repeat(auto-fill, minmax(110px, 1fr)); gap: 10px; }
      .slots.list { grid-template-columns: minmax(0, 1fr); gap: 8px; }
    }
  `],
})
export class VolumeStackViewComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly api = inject(ApiService);
  private readonly snackBar = inject(MatSnackBar);
  private readonly readState = inject(ReadStateService);
  private readonly favorites = inject(FavoritesStateService);
  readonly auth = inject(AuthService);

  readonly libraryId = signal('');
  readonly folderId = signal('');
  readonly libraryName = signal('');
  readonly folderName = signal('');
  readonly breadcrumbs = signal<{ id: string; displayName: string }[]>([]);
  readonly stack = signal<VolumeStackDto | null>(null);
  readonly failed = signal(false);

  /** Card or List: the viewer's own library view preference, the one the folder browse uses (default Card). */
  readonly viewMode = signal<LibraryViewMode>('card');
  /** The list-view column count from 960px, from the same preference (1-3, default 2). */
  readonly listColumns = signal(2);

  /** The chapters and extras this volume holds, in order: what can be selected (a missing chapter is not among them). */
  readonly items = computed<CatalogNodeDto[]>(() =>
    (this.stack()?.slots ?? []).flatMap((slot) => (slot.item ? [slot.item] : [])));

  /** The browse Select mode (1.30.0): the shared state machine, over this volume's items. */
  readonly selection = new NodeSelection<CatalogNodeDto>(() => this.items());
  readonly busy = signal(false);

  readonly folderLink = computed(() => ['/libraries', this.libraryId(), 'browse', this.folderId()]);

  /** "8 of 10 chapters - 1 extra", "Volume file + 4 chapters", "10 chapters". */
  readonly counts = computed(() => {
    const s = this.stack();
    if (!s) return '';
    const items = s.slots.filter((x) => x.kind === 'Item');
    const hasVolumeFile = s.hasVolumeArchive ?? (items.length > 0 && !items[0].chapter);
    const files = items.filter((x) => !!x.chapter && !x.chapter.includes('.')).length;
    // Complete chapters: the server's count (a split chapter counts once, when all its listed parts are here).
    const whole = hasVolumeFile ? files : s.chaptersPresent ?? files;
    const parts: string[] = [];
    if (hasVolumeFile) parts.push('Volume file');
    if (s.chapterCount != null && !hasVolumeFile) {
      // "1 of 5 chapters" whenever fewer are here than the volume holds - also when the rest is not marked missing (the last
      // volume of an ongoing series); owner review, 1.29.0 RC.
      parts.push(whole < s.chapterCount ? `${whole} of ${s.chapterCount} chapters` : `${s.chapterCount} chapters`);
    } else if (whole > 0) {
      parts.push(`${hasVolumeFile ? '+ ' : ''}${whole} chapter${whole === 1 ? '' : 's'}`);
    }
    let text = parts.join(' ');
    if (s.extraCount > 0) text += ` - ${s.extraCount} extra${s.extraCount === 1 ? '' : 's'}`;
    return text;
  });

  /** Where the grouping came from, without naming a provider (the credit lives in Metadata Manager and the docs). */
  readonly sourceText = computed(() => {
    const s = this.stack();
    if (!s) return '';
    const base = s.source === 'FileNames' ? 'Grouped by the volume in the file names'
      : s.source === 'AniList' ? 'Volumes estimated from the published totals'
      : s.source === 'Mixed' ? 'Grouped by the volume list and the file names'
      : "Grouped by the series' volume list";
    return s.confidence === 'Estimated' ? `${base}. Volume boundaries are estimated.` : base;
  });

  readonly isRead = (item: CatalogNodeDto): boolean => item.isRead;
  readonly isUnread = (item: CatalogNodeDto): boolean => !item.isRead;

  constructor() {
    // A star, a series link or a cover changed anywhere (the card star, the selection bar, the dialogs) patches the item in
    // place, so a star / (i) re-created after leaving select mode seeds from it (as browse does for its cards).
    this.favorites.changed$.pipe(takeUntilDestroyed())
      .subscribe((c) => this.patchItems(new Set([c.nodeId]), { isFavorite: c.favorite }));
    inject(MetadataStateService).changed$.pipe(takeUntilDestroyed())
      .subscribe((c) => this.patchItems(new Set([c.nodeId]), { hasSeriesInfo: c.hasSeriesInfo }));
    inject(CoverStateService).changed$.pipe(takeUntilDestroyed())
      .subscribe((c) => this.patchItems(new Set([c.nodeId]), { coverUrl: c.coverUrl, coverSource: c.coverSource }));
  }

  ngOnInit(): void {
    // The viewer's view mode (Card / List) and list columns: the browse preference, read once.
    this.api.getLibraryPreferences().pipe(catchError(() => of(null))).subscribe((p) => {
      if (!p) return;
      this.viewMode.set(p.viewMode === 'list' ? 'list' : 'card');
      const columns = Number(p.listColumns);
      if (Number.isInteger(columns) && columns >= 1 && columns <= 3) this.listColumns.set(columns);
    });

    this.route.paramMap.subscribe((params) => {
      const libraryId = params.get('libraryId') ?? '';
      const nodeId = params.get('nodeId') ?? '';
      const key = params.get('key') ?? '';
      this.libraryId.set(libraryId);
      this.folderId.set(nodeId);
      this.stack.set(null);
      this.failed.set(false);
      this.selection.clear();
      this.api.getVolumeStack(nodeId, key).subscribe({
        next: (s) => this.stack.set(s),
        error: () => this.failed.set(true),
      });
      forkJoin({
        libs: this.api.getLibraries().pipe(catchError(() => of([]))),
        trail: this.api.getBreadcrumbs(nodeId).pipe(catchError(() => of({ nodeId, trail: [] }))),
        node: this.api.getNode(nodeId).pipe(catchError(() => of(null))),
      }).subscribe(({ libs, trail, node }) => {
        this.libraryName.set(libs.find((l) => l.id === libraryId)?.name ?? '');
        this.breadcrumbs.set(trail.trail);
        this.folderName.set(node?.displayName ?? '');
      });
    });
  }

  volumeLink(key: string): string[] {
    return ['/libraries', this.libraryId(), 'browse', this.folderId(), 'volume', key];
  }

  readerLink(item: CatalogNodeDto): string[] {
    return ['/reader', item.id];
  }

  slotKey(slot: VolumeSlotDto): string {
    return slot.item?.id ?? `missing:${slot.chapter}`;
  }

  /** "Ch. 12 - 20 pages": the chapter number the volume list gives the slot, then the page count. */
  subtitle(slot: VolumeSlotDto, item: CatalogNodeDto): string {
    const chapter = slot.chapter ? `Ch. ${slot.chapter}` : '';
    const pages = item.pageCount !== null && item.pageCount !== undefined ? `${item.pageCount} pages` : '';
    return [chapter, pages].filter((part) => part !== '').join(' · ');
  }

  // --- Selection actions (1.30.0) ---

  /** Mark the selected chapters read / unread (unread is the full reset browse does: mark, position and "Reading" state). */
  markRead(read: boolean): void {
    const ids = [...this.selection.selected()];
    if (ids.length === 0) return;
    this.busy.set(true);
    forkJoin(ids.map((id) => this.api.setItemRead(id, read))).subscribe({
      next: () => {
        this.patchItems(new Set(ids), read ? { isRead: true } : { isRead: false, readingState: 'Unread', lastReadPage: null });
        // The folder list this page came from keeps its own copy of the stack's read state: tell it to refresh.
        ids.forEach((id) => this.readState.notifyChanged(id));
        this.snackBar.open(`Marked ${read ? 'read' : 'unread'}: ${ids.length} item${ids.length === 1 ? '' : 's'}`, 'Close', { duration: 2500 });
        this.busy.set(false);
      },
      error: (err) => {
        this.busy.set(false);
        this.snackBar.open(`Failed: ${err.message}`, 'Close', { duration: 4000 });
      },
    });
  }

  /** Add the selected chapters to (or remove them from) the favorites. */
  favorite(favorite: boolean): void {
    const ids = [...this.selection.selected()];
    if (ids.length === 0) return;
    this.busy.set(true);
    forkJoin(ids.map((id) => this.favorites.setFavorite(id, favorite))).subscribe({
      next: () => {
        // Each item patches itself through the favorites channel (constructor); the folder list is told below.
        ids.forEach((id) => this.readState.notifyChanged(id));
        this.snackBar.open(`${favorite ? 'Added to' : 'Removed from'} favorites: ${ids.length} item${ids.length === 1 ? '' : 's'}`, 'Close', { duration: 2500 });
        this.busy.set(false);
      },
      error: (err) => {
        this.busy.set(false);
        this.snackBar.open(`Failed: ${err.message}`, 'Close', { duration: 4000 });
      },
    });
  }

  /** Patch the listed items with these ids in place (no reload, so the scroll position and the selection stay). */
  private patchItems(ids: ReadonlySet<string>, patch: Partial<CatalogNodeDto>): void {
    const current = this.stack();
    if (!current || !current.slots.some((slot) => slot.item && ids.has(slot.item.id))) return;
    this.stack.set({
      ...current,
      slots: current.slots.map((slot) => (slot.item && ids.has(slot.item.id) ? { ...slot, item: { ...slot.item, ...patch } } : slot)),
    });
  }
}
