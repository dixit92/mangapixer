import { ChangeDetectionStrategy, Component } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

/**
 * The favourite mark of a volume stack (1.29.0 RC, owner): a filled star in the cover's bottom-right corner when ANY archive in
 * the stack is starred. Display only - a stack is not a node and cannot be starred itself; open it to star its chapters.
 */
@Component({
  selector: 'app-volume-stack-star',
  standalone: true,
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="star" role="img" aria-label="Has starred chapters" title="Has starred chapters" data-testid="stack-star">
      <mat-icon>star</mat-icon>
    </span>
  `,
  styles: [`
    :host { display: contents; }
    .star {
      position: absolute; right: 6px; bottom: 6px; z-index: 2; line-height: 0; padding: 4px; border-radius: 50%;
      background: rgb(var(--mp-shade-rgb) / 0.55); color: #ffca28; /* theme-exempt: amber star on the always-dark disc over cover art */
    }
    mat-icon { font-size: 18px; width: 18px; height: 18px; }
  `],
})
export class VolumeStackStarComponent {}
