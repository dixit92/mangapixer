import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { CatalogNodeDto } from '../../core/api/api-types';
import { CoverPickerDialogService } from './cover-picker-dialog.service';

/**
 * Browse selection-bar "Cover..." for admins (1.29.0): opens the cover picker for the ONE selected item. Unlike the
 * series menu it shows everywhere - also under "Don't match", in unlinked libraries and with series information hidden -
 * because a wrong cover is fixed where it is seen. The card updates through `CoverStateService`.
 */
@Component({
  selector: 'app-cover-selection-action',
  standalone: true,
  imports: [MatButtonModule, MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button mat-button type="button" [disabled]="disabled() || !single()" (click)="open()"
            [matTooltip]="single() ? 'Choose the cover of this item' : 'Select exactly one item to choose its cover'"
            data-testid="selection-cover">
      <mat-icon>image</mat-icon><span class="lbl">Cover</span>
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
export class CoverSelectionActionComponent {
  private readonly picker = inject(CoverPickerDialogService);

  /** Every node currently listed. */
  readonly nodes = input.required<CatalogNodeDto[]>();

  /** Ids of the selected nodes. */
  readonly selected = input.required<ReadonlySet<string>>();

  readonly disabled = input(false);

  /** The one selected node, or null. */
  readonly single = computed(() => {
    const ids = this.selected();
    if (ids.size !== 1) return null;
    const [id] = ids;
    return this.nodes().find((n) => n.id === id) ?? null;
  });

  open(): void {
    const node = this.single();
    if (node) void this.picker.open(node.id, node.displayName);
  }
}
