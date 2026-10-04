import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

/**
 * The Volumes | Folders switch of a series header (1.29.0): two segments, the active one highlighted. Shown by the browse
 * view only where a Volumes view exists for the folder. Presentational: the host persists the choice (the per-user
 * `seriesViewMode` preference) and reloads the list. On the phone breakpoint the labels drop and the icons remain.
 * 1.34.0: for a webtoon / manhwa without a volume list (`chapters`) the first segment reads Chapters - the same view, which lists
 * the chapters in order with the series status.
 */
@Component({
  selector: 'app-volume-view-switch',
  standalone: true,
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="switch" role="group" aria-label="Series view" data-testid="volume-view-switch">
      <button type="button" [class.on]="active()" [attr.aria-pressed]="active()" (click)="pick(true)" data-testid="view-volumes"
              [disabled]="disabled()" [title]="disabled() ? disabledHint() : chapters() ? 'Show the chapters in order' : 'Group chapters into volumes'">
        @if (chapters()) {
          <mat-icon>format_list_numbered</mat-icon><span class="lbl">Chapters</span>
        } @else {
          <mat-icon>collections_bookmark</mat-icon><span class="lbl">Volumes</span>
        }
      </button>
      <button type="button" [class.on]="!active()" [attr.aria-pressed]="!active()" (click)="pick(false)" data-testid="view-folders"
              [disabled]="disabled()" [title]="disabled() ? disabledHint() : 'Show the real folders'">
        <mat-icon>folder</mat-icon><span class="lbl">Folders</span>
      </button>
    </div>
  `,
  styles: [`
    :host { display: inline-flex; flex: 0 0 auto; }
    .switch { display: inline-flex; border: 1px solid rgba(255, 255, 255, 0.18); border-radius: 8px; overflow: hidden; }
    button {
      display: inline-flex; align-items: center; gap: 4px; padding: 6px 10px; border: 0; cursor: pointer;
      background: transparent; color: #c9c9d6; font: inherit; font-size: 13px;
    }
    button + button { border-left: 1px solid rgba(255, 255, 255, 0.18); }
    button.on { background: rgba(124, 77, 255, 0.22); color: #d4c7ff; }
    button:disabled { cursor: default; opacity: 0.55; }
    button:focus-visible { outline: 2px solid #b39dff; outline-offset: -2px; }
    mat-icon { font-size: 18px; width: 18px; height: 18px; }
    @media (max-width: 599.98px) { .lbl { display: none; } button { padding: 6px 8px; } }
  `],
})
export class VolumeViewSwitchComponent {
  /** True while the Volumes view is shown. */
  readonly active = input.required<boolean>();

  /** 1.34.0: the series' view is its chapter list (a webtoon / manhwa without a volume list): the first segment reads Chapters. */
  readonly chapters = input(false);

  /** An inert switch (optional). Browse no longer uses it (1.31.0): under another sort, picking Volumes switches to Name. */
  readonly disabled = input(false);
  readonly disabledHint = input('');

  /** The chosen view: true = Volumes, false = Folders. */
  readonly changed = output<boolean>();

  pick(volumes: boolean): void {
    if (volumes !== this.active()) this.changed.emit(volumes);
  }
}
