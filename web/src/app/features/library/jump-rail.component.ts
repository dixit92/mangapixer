import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';

import { JumpIndexBucketDto, JumpIndexMode } from '../../core/api/api-types';
import { JumpScrubStripComponent } from './jump-scrub-strip.component';

/**
 * A level shorter than this has no rail (1.49.0, owner decision 13): a handful of rows needs no navigation aid. The level's rows are the
 * sum of the buckets' counts - the FILTERED level while a filter is on.
 */
export const MIN_ROWS_FOR_RAIL = 12;

/**
 * The jump rail of a browse level (1.41.0, moved out of `LibraryBrowseComponent`): a sticky row of chips on desktop / tablet, one
 * picker button + menu on the phone breakpoint (1.10.1). The server decides the chips - letters (A-Z, `#`, script groups; `[A` ... `[Z`
 * in a doujin level whose names open with a tag), round number ranges (`0`, `50`, `100` ... with `50-99` in the tooltip and the phone
 * menu) or, under the date sorts, time chips (`Today`, `This week`, `This month`, `This year`, years), plus `Other` / `Folders` /
 * `Files` - and each chip is one contiguous run of the listing, so the chips follow the listing (reversed in descending order). At a
 * level whose folders and files both carry their own chips (`group`), a small label names each kind's run. The host owns the cursor
 * jump and the scroll-spy (`active` is the bucket of the topmost visible card); a single chip navigates nowhere and a level under
 * {@link MIN_ROWS_FOR_RAIL} rows needs none, so the rail shows only with two or more chips over that many rows.
 *
 * Inputs (stable; lane P's phone strip mounts beside this component with the same data): `buckets` (required, display order),
 * `mode`, `active`, `top`. Output: `jump` (the clicked bucket).
 */
@Component({
  selector: 'app-jump-rail',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule, JumpScrubStripComponent],
  template: `
    @if (shown()) {
      <!-- Sticky (1.8.0): stacked directly under the sticky top bar, whose measured height is the rail's sticky offset. -->
      <nav class="jump-rail" [attr.aria-label]="ariaLabel()" [style.top.px]="top()">
        @for (bucket of buckets(); track $index) {
          @if (groupLabel($index); as heading) {
            <span class="jump-group">{{ heading }}</span>
          }
          <button class="jump-chip" type="button"
                  (click)="jump.emit(bucket)"
                  [class.active]="active() === bucket"
                  [class.wide]="bucket.label.length > 3"
                  [matTooltip]="titleOf(bucket) + ' (' + bucket.count + ')'">
            {{ bucket.label }}
          </button>
        }
      </nav>
      <!-- Phone-only (1.10.1): one button that opens a compact picker; numbers show their whole range there. -->
      <!-- No focus restore on close: the trigger scrolls with the list, so focusing it would undo the jump just made. -->
      <button mat-stroked-button class="jump-menu-trigger" [matMenuTriggerFor]="jumpMenu" [matMenuTriggerRestoreFocus]="false"
              [attr.aria-label]="ariaLabel()">
        <mat-icon>{{ icon() }}</mat-icon> {{ triggerText() }}
      </button>
      <mat-menu #jumpMenu="matMenu" class="jump-picker-menu">
        <!-- A pick closes the menu (1.41.0; it used to stay open over the list it had just moved). -->
        <div class="jump-picker">
          @for (bucket of buckets(); track $index) {
            @if (groupLabel($index); as heading) {
              <span class="jump-group jump-group-menu">{{ heading }}</span>
            }
            <button class="jump-chip" type="button"
                    (click)="jump.emit(bucket)"
                    [class.active]="active() === bucket">
              {{ titleOf(bucket) }}
            </button>
          }
        </div>
      </mat-menu>
      <!-- Phone-only, long levels (1.49.0, lane P): a right-edge strip a thumb drags through the chips; the picker above stays the accessible path. -->
      <app-jump-scrub-strip [buckets]="buckets()" [active]="active()" [top]="top()" (jump)="jump.emit($event)" />
    }
  `,
  styles: [`
    :host { display: contents; }
    /* Sticky under the top bar (1.8.0): top = measured bar height, z-index below the bar's 20; opaque so cards don't show through. */
    .jump-rail {
      position: sticky; z-index: 10;
      display: flex; flex-wrap: wrap; gap: 4px;
      margin-bottom: 12px; padding: 6px 8px;
      background: var(--mp-surface); border-radius: 8px;
    }
    /* Base chip look shared by the sticky rail and the phone picker menu (a CDK overlay outside this component: ::ng-deep). */
    .jump-chip,
    ::ng-deep .jump-picker-menu .jump-chip {
      border: none; cursor: pointer; background: transparent; color: var(--mp-accent);
      border-radius: 6px; font-weight: 600; line-height: 1; font-variant-numeric: tabular-nums;
    }
    .jump-chip { min-width: 28px; padding: 4px 8px; font-size: 12px; transition: background 0.1s; }
    .jump-chip.wide { padding: 4px 10px; }
    /* The label in front of a kind's chips at a level where folders and files both have their own (1.49.0). */
    .jump-group {
      align-self: center; padding: 0 4px 0 8px; font-size: 11px; font-weight: 600; letter-spacing: 0.04em;
      text-transform: uppercase; color: var(--mp-text-muted);
    }
    .jump-group:first-child { padding-left: 0; }
    ::ng-deep .jump-picker-menu .jump-group-menu { flex-basis: 100%; padding: 4px 4px 0; }
    .jump-chip:hover { background: rgb(var(--mp-accent-strong-rgb) / 0.18); }
    /* Declared after :hover (same specificity) so an active chip stays solid even while hovered. */
    .jump-chip.active,
    ::ng-deep .jump-picker-menu .jump-chip.active { background: var(--mp-accent-strong); color: var(--mp-on-accent); }
    /* Phone picker (1.10.1): the trigger is hidden on desktop / tablet, shown on the phone breakpoint. */
    .jump-menu-trigger { display: none; margin-bottom: 12px; }
    ::ng-deep .jump-picker-menu .jump-picker {
      display: flex; flex-wrap: wrap; gap: 4px; padding: 8px; max-width: 300px;
    }
    ::ng-deep .jump-picker-menu .jump-chip { min-width: 34px; padding: 8px 10px; font-size: 14px; }
    @media (max-width: 599.98px) {
      .jump-rail { display: none; }
      .jump-menu-trigger { display: inline-flex; }
    }
  `],
})
export class JumpRailComponent {
  /** The level's buckets in display order (`JumpIndexDto.buckets`). */
  readonly buckets = input.required<JumpIndexBucketDto[]>();
  /** What the chips stand for; `numbers` changes the icon, the labels in the phone menu and the accessible name. */
  readonly mode = input<JumpIndexMode | null | undefined>('letters');
  /** The bucket of the topmost visible card (scroll-spy), or null. */
  readonly active = input<JumpIndexBucketDto | null>(null);
  /** Sticky offset: the measured height of the browse top bar. */
  readonly top = input(0);
  /** A chip was clicked. */
  readonly jump = output<JumpIndexBucketDto>();

  readonly numbers = computed(() => this.mode() === 'numbers');
  readonly dates = computed(() => this.mode() === 'dates');
  /** The rail has something to navigate: two or more chips over at least {@link MIN_ROWS_FOR_RAIL} rows. */
  readonly shown = computed(() => {
    const buckets = this.buckets();
    return buckets.length > 1 && buckets.reduce((rows, b) => rows + b.count, 0) >= MIN_ROWS_FOR_RAIL;
  });
  readonly icon = computed(() => (this.numbers() ? 'format_list_numbered' : this.dates() ? 'schedule' : 'sort_by_alpha'));
  readonly ariaLabel = computed(() => (this.numbers() ? 'Jump to number' : this.dates() ? 'Jump to date' : 'Jump to letter'));
  readonly triggerText = computed(() => {
    const active = this.active();
    if (active) return this.titleOf(active);
    return this.numbers() || this.dates() ? 'Jump' : 'A-Z';
  });

  /**
   * The heading to show before bucket `index`: `Folders` / `Files` where a bucket's `group` differs from the previous one's (1.49.0), else
   * null. Groups appear only at a level whose two kinds both have their own chips.
   */
  groupLabel(index: number): string | null {
    const group = this.buckets()[index]?.group ?? null;
    if (!group || group === (index > 0 ? this.buckets()[index - 1]?.group ?? null : null)) return null;
    return group === 'folders' ? 'Folders' : 'Files';
  }

  /** The chip's long form: the range of a number chip (`50-99`), else its label. */
  titleOf(bucket: JumpIndexBucketDto): string {
    return bucket.range ?? bucket.label;
  }
}

/**
 * The bucket index of every loaded card (1.41.0 scroll-spy by position): a card belongs to the last bucket whose first row
 * (`firstNodeId`) is at or above it. Cards above the window's first bucket start belong to the bucket before that one (the window
 * began mid-bucket after a jump); -1 when no bucket starts in the window (nothing to tell - the caller keeps its current chip).
 */
export function bucketOwners(nodes: readonly { id: string }[], buckets: readonly JumpIndexBucketDto[]): number[] {
  const startOf = new Map<string, number>();
  buckets.forEach((b, i) => { if (b.firstNodeId) startOf.set(b.firstNodeId, i); });
  const firstStart = nodes.findIndex((n) => startOf.has(n.id));
  let current = firstStart === -1 ? -1 : startOf.get(nodes[firstStart].id)! - 1;
  return nodes.map((n) => {
    const start = startOf.get(n.id);
    if (start !== undefined) current = start;
    return current;
  });
}
