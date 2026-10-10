import { ChangeDetectionStrategy, Component, input } from '@angular/core';

import { ListCreditDto } from '../core/api/api-types';

/**
 * The small source line wherever a volume list was completed from a Wikipedia page (1.32.0): "Volume list completed from Wikipedia" with the
 * name linking the page. The link opens Wikipedia in a new tab WITHOUT a referrer, so the visit does not tell Wikipedia which server the
 * reader came from. Renders nothing without a credit.
 */
@Component({
  selector: 'app-list-credit',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (credit(); as c) {
      <span class="list-credit" data-testid="list-credit">Volume list completed from
        <a [href]="c.url" target="_blank" rel="noopener noreferrer" [title]="c.title" data-testid="list-credit-link">{{ c.name }}</a></span>
    }
  `,
  styles: [`
    :host { display: block; }
    .list-credit { color: var(--mp-text-dim); font-size: 12px; overflow-wrap: anywhere; }
    .list-credit a { color: var(--mp-accent); }
  `],
})
export class ListCreditComponent {
  readonly credit = input<ListCreditDto | null | undefined>(null);
}
