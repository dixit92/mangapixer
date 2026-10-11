import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

/**
 * "Reading mode suggestions" on the admin Libraries card (1.41.0): a link to the suggestions page. No count here - the list is
 * computed when the page opens, not on every visit to Administration. Self-contained so the admin page wires it with one line.
 */
@Component({
  selector: 'app-reading-mode-suggestions-link',
  standalone: true,
  imports: [RouterLink, MatButtonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <a mat-stroked-button routerLink="/admin/reading-modes" data-testid="reading-modes-link"
       title="Folders whose pages, ComicInfo or linked series suggest another reading mode">
      <mat-icon>auto_stories</mat-icon> Reading mode suggestions
    </a>
  `,
})
export class ReadingModeSuggestionsLinkComponent {}
