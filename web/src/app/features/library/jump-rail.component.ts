import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatTooltipModule } from '@angular/material/tooltip';

import { JumpIndexBucketDto, JumpIndexMode } from '../../core/api/api-types';

/**
 * The jump rail of a browse level (1.41.0, moved out of `LibraryBrowseComponent`): a sticky row of chips on desktop / tablet, one
 * picker button + menu on the phone breakpoint (1.10.1). The server decides the chips - letters (A-Z, `#`, script groups) or round
 * number ranges (`0`, `50`, `100` ... with `50-99` in the tooltip and the phone menu), plus `Other` / `Folders` / `Files` - and each
 * chip is one contiguous run of the listing, so the chips follow the listing (reversed in descending order). The host owns the
 * cursor jump and the scroll-spy (`active` is the bucket of the topmost visible card); a single chip navigates nowhere, so the rail
 * shows only with two or more.
 */
@Component({
  selector: 'app-jump-rail',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatIconModule, MatMenuModule, MatTooltipModule],
  template: `
    @if (buckets().length > 1) {
      <!-- Sticky (1.8.0): stacked directly under the sticky top bar, whose measured height is the rail's sticky offset. -->
      <nav class="jump-rail" [attr.aria-label]="ariaLabel()" [style.top.px]="top()">
        @for (bucket of buckets(); track $index) {
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
      <button mat-stroked-button class="jump-menu-trigger" [matMenuTriggerFor]="jumpMenu" [attr.aria-label]="ariaLabel()">
        <mat-icon>{{ numbers() ? 'format_list_numbered' : 'sort_by_alpha' }}</mat-icon> {{ triggerText() }}
      </button>
      <mat-menu #jumpMenu="matMenu" class="jump-picker-menu">
        <!-- role="presentation": only stops menu-close-on-click; each chip carries its own click / keyboard semantics. -->
        <div class="jump-picker" role="presentation" (click)="$event.stopPropagation()">
          @for (bucket of buckets(); track $index) {
            <button class="jump-chip" type="button"
                    (click)="jump.emit(bucket)"
                    [class.active]="active() === bucket">
              {{ titleOf(bucket) }}
            </button>
          }
        </div>
      </mat-menu>
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
  readonly ariaLabel = computed(() => (this.numbers() ? 'Jump to number' : 'Jump to letter'));
  readonly triggerText = computed(() => {
    const active = this.active();
    if (active) return this.titleOf(active);
    return this.numbers() ? 'Jump' : 'A-Z';
  });

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
