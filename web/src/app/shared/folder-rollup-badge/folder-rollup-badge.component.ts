import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatTooltipModule } from '@angular/material/tooltip';

import { FolderReadRollup } from '../../core/api/api-types';

/**
 * Presentational badge for a folder's derived read rollup (1.6.0). Renders
 * "Read" when every descendant archive is read, "Reading" when the folder is
 * partially read (some read or in progress), and NOTHING for Unread / null -
 * matching the archive-card convention where an unread item shows no badge, so
 * a folder never looks busier than the cards inside it.
 *
 * Purely presentational: no injection, no API calls. Drop it inside a card's
 * `.cover` box; the host is `display: contents` so the badge positions itself
 * against the cover exactly like the existing `.badge` spans in the browse list.
 * Visual tokens (colors, radius, size) mirror `library-browse.component.ts` so the
 * folder badge is indistinguishable from the archive one at a glance.
 */
@Component({
  selector: 'app-folder-rollup-badge',
  standalone: true,
  imports: [MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (view(); as v) {
      <span class="badge" [class.read]="v.kind === 'read'" [class.reading]="v.kind === 'reading'"
            [matTooltip]="v.tooltip" [attr.aria-label]="v.tooltip" role="img">{{ v.text }}</span>
    }
  `,
  styles: [`
    :host { display: contents; }
    .badge {
      position: absolute; top: 6px; right: 6px; z-index: 2;
      font-size: 11px; font-weight: 600; padding: 2px 6px; border-radius: 10px;
      background: rgba(124, 77, 255, 0.9); color: #fff;
      pointer-events: auto;
    }
    .badge.read { background: rgba(76, 175, 80, 0.95); }
    /* List view parity with the browse list's compact badge sizing. */
    :host-context(.nodes.list) .badge { font-size: 9px; padding: 1px 4px; top: 2px; right: 2px; }
  `],
})
export class FolderRollupBadgeComponent {
  /** The folder's `readRollup` from the browse response; null renders nothing. */
  readonly rollup = input<FolderReadRollup | null>(null);

  /** Display model for the current rollup, or null when no badge should render. */
  readonly view = computed(() => folderRollupView(this.rollup()));
}

/** What the badge shows for a rollup value; exported so the mapping is unit-testable. */
export interface FolderRollupView {
  kind: 'read' | 'reading';
  text: string;
  tooltip: string;
}

/**
 * Map a rollup to its badge, or `null` for no badge. `Unread` deliberately maps to
 * null (an unread archive card shows no badge either); `null` means the folder has
 * nothing to roll up (no readable descendant archives).
 */
export function folderRollupView(rollup: FolderReadRollup | null): FolderRollupView | null {
  switch (rollup) {
    case 'Read':
      return { kind: 'read', text: '✓ Read', tooltip: 'All items read' };
    case 'Reading':
      return { kind: 'reading', text: 'Reading', tooltip: 'Partially read' };
    default:
      return null;
  }
}
