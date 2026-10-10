import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import { SeriesInfoDto } from '../../core/api/api-types';
import { SeriesInfoSummaryComponent } from '../../features/metadata/series-info-summary.component';

/**
 * The hover summary (1.27.0): the compact series summary (the same component the side
 * panel uses, `compact` mode - title, two alternative titles, the facts line, three
 * genres, a six-line description) in a small card beside the hovered item. Read-only
 * and minimally interactive: the pointer may move into it (it stays open) so text
 * selection works, and its "More" opens the series page (owner, 1.27.0 review) instead of
 * expanding the description in place - the only thing in it that navigates. It is
 * never focused (keyboard and screen-reader users have the (i) and its side panel).
 */
@Component({
  selector: 'app-series-info-popover',
  standalone: true,
  imports: [SeriesInfoSummaryComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    role: 'tooltip',
    'data-testid': 'series-info-popover',
    '(pointerenter)': 'pointerEnter.emit()',
    '(pointerleave)': 'pointerLeave.emit()',
  },
  template: `<app-series-info-summary [info]="info()" [compact]="true" [moreLink]="['/series', info().anchorNodeId]" />`,
  styles: [`
    :host {
      display: block; box-sizing: border-box;
      width: 340px; max-width: calc(100vw - 16px); max-height: min(440px, calc(100vh - 16px));
      overflow: auto; padding: 12px 14px;
      background: var(--mp-surface-raised); color: var(--mp-text);
      border: 1px solid rgb(var(--mp-ink-rgb) / 0.1); border-radius: 8px;
      box-shadow: 0 8px 24px rgb(var(--mp-shade-rgb) / 0.5);
    }
    :host ::ng-deep .title { font-size: 16px; }
    :host ::ng-deep .poster { width: 72px; height: 102px; }
  `],
})
export class SeriesInfoPopoverComponent {
  readonly info = input.required<SeriesInfoDto>();
  readonly pointerEnter = output<void>();
  readonly pointerLeave = output<void>();
}
