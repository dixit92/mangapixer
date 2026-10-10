import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { CatalogNodeDto, ReaderMode } from '../../core/api/api-types';
import { CoverImageDirective } from '../cover-image.directive';
import { FolderRollupBadgeComponent } from '../folder-rollup-badge/folder-rollup-badge.component';
import { SeriesInfoHoverDirective } from '../hover-info/series-info-hover.directive';
import { InfoToggleComponent } from '../info-toggle/info-toggle.component';
import { StarToggleComponent } from '../star-toggle/star-toggle.component';
import { VolumeIncompleteBadgeComponent } from '../volume-stack/volume-incomplete-badge.component';
import { OfficialReleaseBadgeComponent } from '../volume-stack/official-release-badge.component';
import { AlsoInVolumeBadgeComponent } from '../volume-stack/also-in-volume-badge.component';

/** Short label of a folder's direction override chip ("LTR", "RTL", "Vertical", "Spread"). */
export function directionShort(mode: ReaderMode): string {
  switch (mode) {
    case 'PagedLtr': return 'LTR';
    case 'PagedRtl': return 'RTL';
    case 'VerticalWebtoon': return 'Vertical';
    default: return 'Spread';
  }
}

/**
 * The LIST row of one catalog node (1.30.0, extracted from `library-browse.component.ts`), shared by the folder browse and
 * the volume stack page so the two cannot drift: a leading checkbox (selects without entering select mode), a small cover,
 * the title and a subtitle, and the trailing marker group - the favorite star, the (i), the read / reading / rollup badges,
 * the direction chip of a folder and the select check.
 *
 * It renders one row and owns no state: the host passes the node, whether select mode is on and whether the row is selected,
 * and gets the raw events back (`activate` = a click on the row, `rowSelect` = the checkbox, the `press*` trio = the touch
 * long-press, `selectToHere` / `dismissPrompt` = the range prompt over the cover). The host's `NodeSelection` interprets them.
 * `link` is null while select mode is on (a tap selects, it must not navigate).
 */
@Component({
  selector: 'app-node-row',
  standalone: true,
  imports: [
    RouterLink, MatIconModule, MatTooltipModule, CoverImageDirective, FolderRollupBadgeComponent, SeriesInfoHoverDirective,
    InfoToggleComponent, StarToggleComponent, VolumeIncompleteBadgeComponent, OfficialReleaseBadgeComponent, AlsoInVolumeBadgeComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="node-wrap" [class.selected]="selected()">
      @if (selectable()) {
        <button type="button" class="row-select" role="checkbox"
                [attr.aria-checked]="selected()"
                [attr.aria-label]="(selected() ? 'Deselect ' : 'Select ') + node().displayName"
                (click)="rowSelect.emit($event)">
          <mat-icon>{{ selected() ? 'check_box' : 'check_box_outline_blank' }}</mat-icon>
        </button>
      }
      <a #cardEl class="node-card" [routerLink]="link()"
         (click)="activate.emit($event)"
         (pointerdown)="pressStart.emit($event)"
         (pointerup)="pressEnd.emit()"
         (pointercancel)="pressEnd.emit()"
         (pointerleave)="pressEnd.emit()">
        <div class="cover" [appSeriesInfoHover]="hoverId()" [hoverAnchor]="cardEl">
          @if (node().coverUrl; as cover) {
            <img appCover [src]="cover" alt="" loading="lazy">
          }
          <mat-icon class="cover-fallback">{{ icon() }}</mat-icon>
          @if (node().volumeStack; as stack) {
            <app-volume-incomplete-badge [summary]="stack" />
          }
          @if (rangePrompt()) {
            <div class="range-prompt">
              <button type="button" (click)="selectToHere.emit($event)">Select to here</button>
              <button type="button" class="cancel" (click)="dismissPrompt.emit($event)">Cancel</button>
            </div>
          }
        </div>
        <div class="node-text">
          <div class="node-title" [title]="node().displayName" [appSeriesInfoHover]="hoverId()" [hoverAnchor]="cardEl">{{ node().displayName }}</div>
          <div class="node-sub">{{ subtitle() }}</div>
        </div>
        <div class="row-markers">
          <!-- 1.30.0 (reach): an official release of a volume held as chapters / a chapter a volume file here already holds. -->
          @if (isStack()) {
            <app-official-release-badge [language]="node().volumeStack?.officialRelease" [overlay]="false" />
          } @else {
            <app-also-in-volume-badge [volume]="node().alsoInVolume" [overlay]="false" />
          }
          @if (!isStack()) {
            <app-star-toggle [nodeId]="node().id" [favorite]="!!node().isFavorite" />
            @if (!selectMode()) {
              <app-info-toggle [nodeId]="node().id" [hasSeriesInfo]="!!node().hasSeriesInfo" [compact]="true"
                               [appSeriesInfoHover]="hoverId()" [hoverAnchor]="cardEl" />
            }
          }
          @if (node().isRead) {
            <span class="badge read" matTooltip="Read">✓ Read</span>
          } @else if (node().readingState === 'InProgress') {
            <span class="badge reading">Reading</span>
          }
          <app-folder-rollup-badge [rollup]="node().readRollup" />
          @if (showDirection() && node().kind === 'Folder' && node().readerDefault; as direction) {
            <span class="badge dir" matTooltip="Reading direction override">{{ directionLabel(direction) }}</span>
          }
          @if (selectMode() && selectable()) {
            <span class="check" [class.on]="selected()">
              <mat-icon>{{ selected() ? 'check_circle' : 'radio_button_unchecked' }}</mat-icon>
            </span>
          }
        </div>
      </a>
    </div>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    .node-wrap { position: relative; border-radius: 8px; min-width: 0; display: flex; align-items: center; gap: 4px; }
    .node-wrap.selected { outline: 2px solid var(--mp-accent-strong); outline-offset: 3px; }
    .row-select {
      flex: 0 0 auto; display: flex; align-items: center; justify-content: center;
      width: 28px; height: 28px; padding: 0; border: none; border-radius: 6px;
      background: transparent; color: var(--mp-text-dim); cursor: pointer;
    }
    .row-select mat-icon { font-size: 20px; width: 20px; height: 20px; }
    .row-select[aria-checked="true"] { color: var(--mp-accent-strong); }
    .node-card {
      flex: 1 1 auto; min-width: 0; cursor: pointer; text-decoration: none; color: inherit;
      display: flex; align-items: center; gap: 12px;
      padding: 6px; border-radius: 8px; background: rgb(var(--mp-ink-rgb) / 0.03);
    }
    .cover {
      position: relative; width: 46px; height: 66px; flex: 0 0 auto; border-radius: 4px; overflow: hidden;
      background: rgb(var(--mp-ink-rgb) / 0.06); display: flex; align-items: center; justify-content: center;
    }
    .cover img { width: 100%; height: 100%; object-fit: cover; position: relative; z-index: 1; }
    .cover-fallback { font-size: 24px; width: 24px; height: 24px; color: var(--mp-text-faint); position: absolute; z-index: 0; }
    .node-text { flex: 1 1 auto; min-width: 0; }
    .node-title { font-size: 13px; font-weight: 500; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .node-sub { font-size: 12px; color: var(--mp-text-muted); }
    /* The trailing marker group sits in normal flow at the right of the row, not over the 46px thumbnail. The rollup badge
       lives in a shared child component, hence ::ng-deep for that rule (its own list-view sizing keys off the browse page). */
    .row-markers { flex: 0 0 auto; display: flex; align-items: center; gap: 6px; padding-right: 4px; }
    .badge {
      position: static; font-size: 11px; font-weight: 600; padding: 2px 6px; border-radius: 10px;
      background: rgb(var(--mp-accent-strong-rgb) / 0.9); color: var(--mp-on-accent);
    }
    :host .node-card .row-markers ::ng-deep .badge { position: static; font-size: 11px; padding: 2px 6px; }
    .badge.read { background: rgb(var(--mp-success-strong-rgb) / 0.95); color: var(--mp-on-scrim); }
    .badge.dir { background: rgb(var(--mp-shade-rgb) / 0.65); color: var(--mp-on-scrim); }
    .check { line-height: 0; color: var(--mp-on-scrim); border-radius: 50%; background: transparent; }
    .check.on { color: var(--mp-accent-strong); background: var(--mp-on-scrim); }
    .check mat-icon { font-size: 24px; width: 24px; height: 24px; }
    /* Touch range fill (long-press "Select to here"): a small floating action over the long-pressed row's cover. */
    .range-prompt {
      position: absolute; inset: 0; z-index: 4;
      display: flex; flex-direction: column; align-items: center; justify-content: center;
      gap: 6px; padding: 8px; background: rgb(var(--mp-shade-rgb) / 0.85); border-radius: 8px;
    }
    .range-prompt button {
      border: none; border-radius: 6px; padding: 6px 10px; font-size: 12px; font-weight: 600;
      cursor: pointer; background: var(--mp-accent-strong); color: var(--mp-on-accent); width: 100%;
    }
    .range-prompt button.cancel { background: color-mix(in srgb, var(--mp-on-scrim) 12%, transparent); color: var(--mp-on-scrim); }
  `],
})
export class NodeRowComponent {
  readonly node = input.required<CatalogNodeDto>();

  /** Where a click goes; null while select mode is on (a tap selects). */
  readonly link = input<string[] | null>(null);

  /** The subtitle line ("20 pages", "3 items", "Ch. 12 · 20 pages", "8 of 9 chapters"). */
  readonly subtitle = input('');

  readonly selectMode = input(false);
  readonly selected = input(false);

  /** The row can be selected: the leading checkbox and (in select mode) the check show. */
  readonly selectable = input(true);

  /** This row shows the touch "Select to here" prompt. */
  readonly rangePrompt = input(false);

  /** The hover-summary zone id (only an item that shows the (i), never in select mode), or null. */
  readonly hoverId = input<string | null>(null);

  /** Admins see a folder's reading-direction override as a chip. */
  readonly showDirection = input(false);

  readonly activate = output<MouseEvent>();
  readonly rowSelect = output<Event>();
  readonly pressStart = output<PointerEvent>();
  readonly pressEnd = output<void>();
  readonly selectToHere = output<Event>();
  readonly dismissPrompt = output<Event>();

  readonly isStack = computed(() => this.node().kind === 'VolumeStack');
  readonly icon = computed(() => {
    const kind = this.node().kind;
    return kind === 'Folder' ? 'folder' : kind === 'VolumeStack' ? 'collections_bookmark' : 'menu_book';
  });

  directionLabel(mode: ReaderMode): string {
    return directionShort(mode);
  }
}
