import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';

import { ApiService } from '../../core/api/api.service';
import { RatingStarsComponent } from './rating-stars.component';

/**
 * "Your rating" for one node (1.52.0): a caption and the editable `app-rating-stars`, for surfaces that know only the node id - the
 * Info sheet, the series page header and the reader's Rate sheet rows. It reads the viewer's own rating from the node (`GET
 * nodes/{id}`, `myRating`) unless the host passes it, and stays disabled until it is known (no write over an unknown value).
 */
@Component({
  selector: 'app-node-rating',
  standalone: true,
  imports: [RatingStarsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="node-rating" data-testid="node-rating">
      <span class="caption">{{ caption() }}</span>
      <app-rating-stars [nodeId]="nodeId()" [rating]="rating()" [disabled]="!loaded()"
                        [label]="caption() + (name() ? ': ' + name() : '')" />
    </div>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    .node-rating { display: flex; align-items: center; flex-wrap: wrap; gap: 2px 10px; margin: 6px 0; }
    .caption { font-size: 12px; font-weight: 600; letter-spacing: 0.4px; text-transform: uppercase; color: var(--mp-text-dim); }
  `],
})
export class NodeRatingComponent {
  private readonly api = inject(ApiService);
  readonly nodeId = input.required<string>();
  /** The rating when the host already has it (skips the node read); undefined = read it. */
  readonly known = input<number | null | undefined>(undefined);
  readonly caption = input<string>('Your rating');
  /** What is rated, for the slider's accessible name ("Your rating: Berserk"). */
  readonly name = input<string>('');

  readonly rating = signal<number | null>(null);
  readonly loaded = signal(false);

  constructor() {
    effect(() => {
      const id = this.nodeId();
      const known = this.known();
      untracked(() => this.load(id, known));
    });
  }

  private load(nodeId: string, known: number | null | undefined): void {
    if (known !== undefined) {
      this.rating.set(known);
      this.loaded.set(true);
      return;
    }
    this.loaded.set(false);
    this.api.getNode(nodeId).subscribe({
      next: (node) => {
        if (nodeId !== this.nodeId()) return; // a newer node was asked for meanwhile
        this.rating.set(node?.myRating ?? null);
        this.loaded.set(true);
      },
      error: () => this.loaded.set(false),
    });
  }
}
