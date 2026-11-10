import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { CatalogNodeDto } from '../../core/api/api-types';
import { RatingSheetService } from './rating-sheet.service';

/**
 * Browse selection-bar "Rate..." (1.53.0, ratings follow-up 5b; audit N5): opens the Rate sheet for the ONE selected folder or archive -
 * also a folder that has no Info button. For every user (a rating is the user's own). Sits right after Favorites, with the app's one
 * Rate icon (`stars`, audit V8).
 */
@Component({
  selector: 'app-rate-selection-action',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button mat-button type="button" [disabled]="disabled() || !single()" (click)="open()"
            [matTooltip]="single() ? 'Rate this item' : 'Select exactly one item to rate it'"
            data-testid="selection-rate">
      <mat-icon>stars</mat-icon><span class="lbl">Rate…</span>
    </button>
  `,
  styles: [`
    :host { display: contents; }
    mat-icon { margin-right: 4px; }
    @media (max-width: 599.98px) {
      .lbl { display: none; }
      mat-icon { margin-right: 0; }
    }
  `],
})
export class RateSelectionActionComponent {
  private readonly sheet = inject(RatingSheetService);

  /** Every node currently listed. */
  readonly nodes = input.required<CatalogNodeDto[]>();

  /** Ids of the selected nodes (stored nodes only - a volume stack is not one). */
  readonly selected = input.required<ReadonlySet<string>>();

  readonly disabled = input(false);

  /** The one selected folder or archive, or null. */
  readonly single = computed(() => {
    const ids = this.selected();
    if (ids.size !== 1) return null;
    const [id] = ids;
    const node = this.nodes().find((n) => n.id === id);
    return node && (node.kind === 'Folder' || node.kind === 'Archive') ? node : null;
  });

  open(): void {
    const node = this.single();
    if (node) void this.sheet.openNode(node.id);
  }
}
