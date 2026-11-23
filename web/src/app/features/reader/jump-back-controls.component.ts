import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { JumpState } from './jump-back';

/** Which line the chip shows: right after a jump, after Back (offering Return), or back in the app after leaving right after a jump. */
export type JumpChipKind = 'jumped' | 'returned' | 'left';

/** "Back to page 42" / "Return to page 187": what the button does (the origin is where it leads). */
export function jumpActionLabel(state: JumpState): string {
  return (state.mode === 'back' ? 'Back to page ' : 'Return to page ') + (state.origin + 1);
}

/**
 * The reader bar's jump back button (1.54.0): "undo 42" while the jump can be undone, "redo 187" after Back. The persistent way back
 * after the chip is gone, and the only one in fullscreen once the bar is shown.
 */
@Component({
  selector: 'app-jump-back-button',
  standalone: true,
  imports: [MatIconModule, MatTooltipModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button type="button" class="jump-btn" (click)="back.emit()" [attr.aria-label]="label()" [matTooltip]="label() + ' (Backspace)'">
      <mat-icon aria-hidden="true">{{ state().mode === 'back' ? 'undo' : 'redo' }}</mat-icon>{{ state().origin + 1 }}
    </button>
  `,
  styles: [`
    :host { flex: none; display: inline-flex; }
    .jump-btn {
      display: inline-flex; align-items: center; gap: 2px; height: 40px; min-width: 40px; padding: 0 10px 0 6px;
      border: 1px solid rgb(var(--mp-ink-rgb) / 0.22); border-radius: 20px; background: rgb(var(--mp-ink-rgb) / 0.08);
      color: var(--mp-reader-text); font: inherit; font-size: 13px; font-weight: 600; font-variant-numeric: tabular-nums;
      cursor: pointer; -webkit-tap-highlight-color: transparent;
    }
    .jump-btn mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .jump-btn:focus-visible { outline: 2px solid var(--mp-accent-strong); outline-offset: 1px; }
  `],
})
export class JumpBackButtonComponent {
  readonly state = input.required<JumpState>();
  readonly back = output<void>();
  readonly label = computed(() => jumpActionLabel(this.state()));
}

/**
 * The jump back chip (1.54.0): about 10 s after a jump, above the bar ("Jumped from page 42 [Back to page 42] [x]"), low on the
 * screen while the controls are hidden; shorter on a phone ("From p. 42 [Back]"). The text is a status region, so a screen reader
 * hears it.
 */
@Component({
  selector: 'app-jump-back-chip',
  standalone: true,
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[class.low]': 'low()' },
  template: `
    <span class="text" role="status">
      <span class="long">{{ longText() }}</span><span class="short">{{ shortText() }}</span>
    </span>
    <button type="button" class="go" (click)="back.emit()" [attr.aria-label]="label()">
      <mat-icon aria-hidden="true">{{ state().mode === 'back' ? 'undo' : 'redo' }}</mat-icon>
      <span class="long">{{ label() }}</span><span class="short">{{ state().mode === 'back' ? 'Back' : 'p. ' + (state().origin + 1) }}</span>
    </button>
    <button type="button" class="x" (click)="dismissed.emit()" aria-label="Dismiss">
      <mat-icon aria-hidden="true">close</mat-icon>
    </button>
  `,
  styles: [`
    :host {
      position: absolute; left: 50%; transform: translateX(-50%); z-index: 1002;
      bottom: calc(60px + env(safe-area-inset-bottom, 0px));
      display: flex; align-items: center; gap: 6px; padding: 4px 4px 4px 14px; max-width: calc(100% - 32px);
      border-radius: 24px; background: var(--mp-reader-chrome); color: var(--mp-reader-text-secondary);
      border: 1px solid rgb(var(--mp-ink-rgb) / 0.2); box-shadow: 0 4px 14px rgb(var(--mp-shade-rgb) / 0.4);
      font-size: 13px; white-space: nowrap;
    }
    :host(.low) { bottom: calc(16px + env(safe-area-inset-bottom, 0px)); }
    .go {
      display: inline-flex; align-items: center; gap: 4px; min-height: 40px; padding: 0 14px 0 10px; border: 0;
      border-radius: 20px; background: var(--mp-accent-strong); color: var(--mp-on-accent); font: inherit;
      font-weight: 600; cursor: pointer;
    }
    mat-icon { font-size: 18px; width: 18px; height: 18px; }
    .x {
      display: inline-flex; align-items: center; justify-content: center; width: 40px; height: 40px; border: 0;
      border-radius: 50%; background: transparent; color: var(--mp-reader-text-secondary); cursor: pointer;
    }
    button:focus-visible { outline: 2px solid var(--mp-accent-strong); outline-offset: 1px; }
    .short { display: none; }
    @media (max-width: 599.98px) {
      .long { display: none; }
      .short { display: inline; }
    }
  `],
})
export class JumpBackChipComponent {
  readonly state = input.required<JumpState>();
  readonly kind = input.required<JumpChipKind>();
  readonly low = input(false);
  readonly back = output<void>();
  readonly dismissed = output<void>();
  readonly label = computed(() => jumpActionLabel(this.state()));
  readonly longText = computed(() => {
    const s = this.state();
    switch (this.kind()) {
      case 'left': return 'You jumped just before leaving';
      case 'returned': return 'Back on page ' + (s.landing + 1);
      default: return 'Jumped from page ' + (s.origin + 1);
    }
  });
  readonly shortText = computed(() =>
    this.kind() === 'returned' ? 'Back on p. ' + (this.state().landing + 1) : 'From p. ' + (this.state().origin + 1));
}
