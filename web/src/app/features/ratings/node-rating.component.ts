import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { filter } from 'rxjs/operators';

import { ApiService } from '../../core/api/api.service';
import { ServerRatingDto } from '../../core/api/api-types';
import { RatingStarsComponent } from './rating-stars.component';
import { RatingsStateService } from './ratings-state.service';
import { serverAverageLabel, serverAverageText } from './rating-scale';

/**
 * "Your rating" for one node (1.52.0): a caption and the editable `app-rating-stars`, for surfaces that know only the node id - the
 * Info sheet, the series page header and the reader's Rate sheet rows. It reads the viewer's own rating from the node (`GET
 * nodes/{id}`, `myRating`) unless the host passes it, and stays disabled until it is known (no write over an unknown value).
 *
 * 1.53.0 (audit V4): one column - the caption is always a label above the stars, whatever the width. 1.53.0 (shared ratings, audit N1):
 * under the stars, the server average as ONE star and a number ("SERVER ★ 4.0 · 3 ratings"), read from the same node answer
 * (`serverRating`) - nothing and no space while there is none (the admin switch off, or nobody who shares rated it). After the viewer
 * rates, the node is read again so the line follows.
 */
@Component({
  selector: 'app-node-rating',
  standalone: true,
  imports: [MatIconModule, MatTooltipModule, RatingStarsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="node-rating" data-testid="node-rating">
      <span class="caption">{{ caption() }}</span>
      <app-rating-stars [nodeId]="nodeId()" [rating]="rating()" [disabled]="!loaded()"
                        [label]="caption() + (name() ? ': ' + name() : '')" />
      @if (server(); as s) {
        <span class="server" role="img" data-testid="server-rating" [attr.aria-label]="serverLabel()" [matTooltip]="serverLabel()">
          <span class="caption" aria-hidden="true">Server</span>
          <mat-icon class="server-star" aria-hidden="true">star</mat-icon>
          <span class="server-value" aria-hidden="true">{{ serverText() }}</span>
        </span>
      }
    </div>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    .node-rating { display: grid; grid-template-columns: 1fr; gap: 2px; margin: 6px 0; justify-items: start; }
    .caption { font-size: 12px; font-weight: 600; letter-spacing: 0.4px; text-transform: uppercase; color: var(--mp-text-dim); }
    .server { display: inline-flex; align-items: center; margin-top: 4px; white-space: nowrap; }
    .server .caption { margin-right: 8px; }
    .server-star { font-size: 15px; width: 15px; height: 15px; color: var(--mp-warn-strong); margin-right: 2px; }
    .server-value { font-size: 13px; color: var(--mp-text-muted); }
  `],
})
export class NodeRatingComponent {
  private readonly api = inject(ApiService);
  private readonly ratings = inject(RatingsStateService);
  readonly nodeId = input.required<string>();
  /** The rating when the host already has it (skips the node read); undefined = read it. */
  readonly known = input<number | null | undefined>(undefined);
  /** 1.53.0: the node's server average when the host already has it (with `known`). */
  readonly knownServer = input<ServerRatingDto | null | undefined>(undefined);
  readonly caption = input<string>('Your rating');
  /** What is rated, for the slider's accessible name ("Your rating: Berserk"). */
  readonly name = input<string>('');

  readonly rating = signal<number | null>(null);
  readonly loaded = signal(false);
  /** 1.53.0: the node's server average (null = none to show). */
  readonly server = signal<ServerRatingDto | null>(null);
  readonly serverText = computed(() => serverAverageText(this.server()));
  readonly serverLabel = computed(() => serverAverageLabel(this.server()));

  constructor() {
    effect(() => {
      const id = this.nodeId();
      const known = this.known();
      const knownServer = this.knownServer();
      untracked(() => this.load(id, known, knownServer));
    });
    // The viewer's own change moves the average: read the node again for the line (the stars follow the change themselves).
    this.ratings.changed$.pipe(takeUntilDestroyed(), filter((c) => c.nodeId === this.nodeId())).subscribe(() => this.refreshServer());
  }

  private load(nodeId: string, known: number | null | undefined, knownServer: ServerRatingDto | null | undefined): void {
    if (known !== undefined) {
      this.rating.set(known);
      this.server.set(validServer(knownServer));
      this.loaded.set(true);
      return;
    }
    this.loaded.set(false);
    this.server.set(null);
    this.api.getNode(nodeId).subscribe({
      next: (node) => {
        if (nodeId !== this.nodeId()) return; // a newer node was asked for meanwhile
        this.rating.set(node?.myRating ?? null);
        this.server.set(validServer(node?.serverRating));
        this.loaded.set(true);
      },
      error: () => this.loaded.set(false),
    });
  }

  private refreshServer(): void {
    const nodeId = this.nodeId();
    this.api.getNode(nodeId).subscribe({
      next: (node) => {
        if (nodeId === this.nodeId()) this.server.set(validServer(node?.serverRating));
      },
      error: () => { /* keep the line as it was */ },
    });
  }
}

/** A server average worth a line: at least one rating and a number. */
function validServer(value: ServerRatingDto | null | undefined): ServerRatingDto | null {
  return value && typeof value.average === 'number' && (value.count ?? 0) > 0 ? value : null;
}
