import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';

import { MoveConflictsApiService } from './move-conflicts-api.service';

/**
 * "Move conflicts (n)" on the admin Libraries card (1.31.0): a link to the Move conflicts page, shown only while at least one
 * conflict is open (an item moved to another library while both copies had their own reading state or settings).
 * Self-contained so the admin page wires it with one line.
 */
@Component({
  selector: 'app-move-conflicts-link',
  standalone: true,
  imports: [RouterLink, MatButtonModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (open() > 0) {
      <a mat-stroked-button routerLink="/admin/move-conflicts" data-testid="move-conflicts-link"
         title="Items moved to another library while both copies had their own state - choose which to keep">
        <mat-icon>merge_type</mat-icon> Move conflicts ({{ open() }})
      </a>
    }
  `,
})
export class MoveConflictsLinkComponent implements OnInit {
  private readonly api = inject(MoveConflictsApiService);
  readonly open = signal(0);

  ngOnInit(): void {
    this.api.count().subscribe({ next: (c) => this.open.set(c.open), error: () => this.open.set(0) });
  }
}
