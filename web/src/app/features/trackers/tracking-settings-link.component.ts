import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';

import { TRACKING_PATH } from './tracker-texts';

/**
 * Settings' pointer to the Tracking page (1.43.0 owner redesign): the Connected accounts card moved there - it could grow to
 * hundreds of series and did not match the other Settings cards' width. One line, a card like its neighbours.
 */
@Component({
  selector: 'app-tracking-settings-link',
  standalone: true,
  imports: [RouterLink, MatCardModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-card class="tracking-link-card" data-testid="settings-tracking-link-card">
      <mat-card-content>
        <p class="line">
          <mat-icon aria-hidden="true">track_changes</mat-icon>
          <span>Your MyAnimeList and MangaUpdates accounts and the series synced to them are on the
            <a [routerLink]="trackingPath" data-testid="settings-tracking-link">Tracking page</a>.</span>
        </p>
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    /* The same width and spacing as the other Settings cards. */
    mat-card { max-width: 600px; margin: 24px auto 0; }
    .line { display: flex; align-items: center; gap: 10px; margin: 0; overflow-wrap: anywhere; color: var(--mp-text-secondary); }
    .line mat-icon { flex: none; color: var(--mp-warn); }
    .line a { color: var(--mp-accent); }
  `],
})
export class TrackingSettingsLinkComponent {
  readonly trackingPath = TRACKING_PATH;
}
