import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import { SeriesInfoDto } from '../../core/api/api-types';
import { SeriesInfoSummaryComponent } from '../../features/metadata/series-info-summary.component';

/**
 * The hover summary (1.27.0): the compact series summary (the same component the side
 * panel uses, `compact` mode - title, two alternative titles, the facts line, three
 * genres, a six-line description) in a small card beside the hovered item. Read-only
 * and minimally interactive: the pointer may move into it (it stays open) so the
 * description's "More" toggle and text selection work; nothing in it navigates. It is
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
  template: `<app-series-info-summary [info]="info()" [compact]="true" />`,
  styles: [`
    :host {
      display: block; box-sizing: border-box;
      width: 340px; max-width: calc(100vw - 16px); max-height: min(440px, calc(100vh - 16px));
      overflow: auto; padding: 12px 14px;
      background: #1e1e28; color: #e6e6ee;
      border: 1px solid rgba(255, 255, 255, 0.1); border-radius: 8px;
      box-shadow: 0 8px 24px rgba(0, 0, 0, 0.5);
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
