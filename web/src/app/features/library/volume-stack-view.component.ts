import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { catchError, forkJoin, of } from 'rxjs';

import { ApiService } from '../../core/api/api.service';
import { VolumeSlotDto, VolumeStackDto } from '../../core/api/api-types';
import { CoverImageDirective } from '../../shared/cover-image.directive';
import { InfoToggleComponent } from '../../shared/info-toggle/info-toggle.component';
import { StarToggleComponent } from '../../shared/star-toggle/star-toggle.component';
import { MissingChapterCardComponent } from '../../shared/volume-stack/missing-chapter-card.component';

/**
 * The stack view of one virtual volume (1.29.0): `/libraries/:libraryId/browse/:nodeId/volume/:key`. A header (cover,
 * "Volume 3", "8 of 10 chapters - 1 extra", where the grouping came from), previous / next volume, then the ordered slots:
 * chapter cards (their own cover, read state, star and (i) as in the folder list) with a dashed placeholder where a whole
 * chapter is missing. Opening a chapter opens the reader as usual; the reader's previous / next stay folder-level, and the
 * breadcrumbs name the REAL folder. Extras (45.5) show in their place and are never "missing".
 */
@Component({
  selector: 'app-volume-stack-view',
  standalone: true,
  imports: [RouterLink, MatButtonModule, MatIconModule, CoverImageDirective, InfoToggleComponent, StarToggleComponent, MissingChapterCardComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
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
        </div>
      }
    </nav>

    @if (stack(); as s) {
      <header class="head">
        <div class="head-cover">
          @if (s.coverUrl) { <img appCover [src]="s.coverUrl" alt=""> }
          <mat-icon class="fallback">menu_book</mat-icon>
        </div>
        <div class="head-text">
          <h1 data-testid="stack-title">{{ s.label }}</h1>
          <p class="counts" data-testid="stack-counts">{{ counts() }}</p>
          <p class="source" data-testid="stack-source">{{ sourceText() }}</p>
        </div>
      </header>

      <div class="slots" data-testid="stack-slots">
        @for (slot of s.slots; track slotKey(slot)) {
          @if (slot.kind === 'Missing') {
            <app-missing-chapter-card [chapter]="slot.chapter ?? '?'" />
          } @else if (slot.item; as item) {
            <div class="slot-wrap">
              <a class="slot" [routerLink]="['/reader', item.id]" [attr.data-testid]="'stack-item'">
                <div class="cover">
                  @if (item.coverUrl) { <img appCover [src]="item.coverUrl" alt="" loading="lazy"> }
                  <mat-icon class="fallback">menu_book</mat-icon>
                  @if (item.isRead) {
                    <span class="badge read">✓ Read</span>
                  } @else if (item.readingState === 'InProgress') {
                    <span class="badge reading">Reading</span>
                  }
                  <app-star-toggle [nodeId]="item.id" [favorite]="!!item.isFavorite" [overlay]="true" [compact]="true" />
                  <app-info-toggle [nodeId]="item.id" [hasSeriesInfo]="!!item.hasSeriesInfo" [overlay]="true" />
                </div>
                <div class="text">
                  <div class="title" [title]="item.displayName">{{ item.displayName }}</div>
                  <div class="sub">
                    @if (slot.chapter) { Ch. {{ slot.chapter }} · }
                    @if (item.pageCount !== null && item.pageCount !== undefined) { {{ item.pageCount }} pages }
                  </div>
                </div>
              </a>
            </div>
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
    .crumbs { flex: 1 1 auto; min-width: 0; }
    .crumbs a { text-decoration: none; color: #b39dff; }
    .crumbs .current { color: #e6e6ee; font-weight: 500; }
    .nav { display: flex; gap: 8px; flex: 0 0 auto; }
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
    .slot { display: block; color: inherit; text-decoration: none; }
    .slot-wrap { position: relative; }
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
    .title { margin-top: 6px; font-size: 13px; font-weight: 500; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .sub { font-size: 12px; color: #999; min-height: 1em; }
    .empty { color: #999; padding: 32px; text-align: center; }
    @media (max-width: 599.98px) {
      .lbl { display: none; }
      .head-cover { width: 88px; }
      h1 { font-size: 20px; }
      .slots { grid-template-columns: repeat(auto-fill, minmax(110px, 1fr)); gap: 10px; }
    }
  `],
})
export class VolumeStackViewComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly api = inject(ApiService);

  readonly libraryId = signal('');
  readonly folderId = signal('');
  readonly libraryName = signal('');
  readonly folderName = signal('');
  readonly breadcrumbs = signal<{ id: string; displayName: string }[]>([]);
  readonly stack = signal<VolumeStackDto | null>(null);
  readonly failed = signal(false);

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

  ngOnInit(): void {
    this.route.paramMap.subscribe((params) => {
      const libraryId = params.get('libraryId') ?? '';
      const nodeId = params.get('nodeId') ?? '';
      const key = params.get('key') ?? '';
      this.libraryId.set(libraryId);
      this.folderId.set(nodeId);
      this.stack.set(null);
      this.failed.set(false);
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

  slotKey(slot: VolumeSlotDto): string {
    return slot.item?.id ?? `missing:${slot.chapter}`;
  }
}
