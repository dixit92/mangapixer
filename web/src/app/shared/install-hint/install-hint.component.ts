import { ChangeDetectionStrategy, Component, ElementRef, effect, inject, viewChild } from '@angular/core';
import { NavigationStart, Router } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { filter } from 'rxjs';

import { InstallHintService } from './install-hint.service';

/**
 * Renders the iPhone/iPad "Add to Home Screen" hint (see `InstallHintService`). Mounted once in
 * the app shell so no reader file needs template changes.
 *
 * A compact, non-modal card pinned to the bottom, above the reader's bars (z-index 1001) but
 * under the reader's help overlay (1003): on the very first reader open the help overlay wins
 * and the hint is fully usable once help is closed. Dark like the reader, padded by the
 * safe-area insets, and clear of the reader's 48px bottom bar.
 */
@Component({
  selector: 'app-install-hint',
  standalone: true,
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (hint.visible()) {
      <section #card class="hint" role="region" aria-labelledby="install-hint-title" tabindex="-1"
               (keydown.escape)="hint.dismissForSession()">
        <h2 id="install-hint-title">Read full screen</h2>
        <p>Add MangaPixer to your Home Screen to get rid of the browser bars:</p>
        <ol>
          <li><mat-icon aria-hidden="true">ios_share</mat-icon>
            <span>Tap <strong>Share</strong> (behind the <mat-icon class="inline" aria-hidden="true">more_horiz</mat-icon> menu in some Safari layouts)</span></li>
          <li><mat-icon aria-hidden="true">add_box</mat-icon>
            <span>Choose <strong>Add to Home Screen</strong></span></li>
          <li><mat-icon aria-hidden="true">check_circle</mat-icon>
            <span>Tap <strong>Add</strong>, then open MangaPixer from your Home Screen</span></li>
        </ol>
        <div class="actions">
          <button type="button" (click)="hint.dismissForSession()">Not now</button>
          <button type="button" (click)="hint.dismissForever()">Don't show again</button>
        </div>
      </section>
    }
  `,
  styles: [`
    .hint {
      position: fixed; z-index: 1002; left: 0; right: 0; margin: 0 auto;
      bottom: calc(56px + env(safe-area-inset-bottom, 0px));
      width: min(calc(100vw - 24px - env(safe-area-inset-left, 0px) - env(safe-area-inset-right, 0px)), 420px);
      box-sizing: border-box; padding: 12px 16px 4px; border-radius: 12px;
      background: #1c1c1f; color: #eee; border: 1px solid rgba(255, 255, 255, 0.16);
      box-shadow: 0 6px 24px rgba(0, 0, 0, 0.5); font-size: 14px; line-height: 1.35;
    }
    .hint:focus { outline: none; }
    .hint:focus-visible { outline: 2px solid #7c4dff; outline-offset: 2px; }
    h2 { margin: 0 0 4px; font-size: 16px; font-weight: 600; }
    p { margin: 0 0 8px; color: #bbb; }
    ol { list-style: none; margin: 0; padding: 0; }
    li { display: flex; align-items: center; gap: 10px; padding: 4px 0; }
    li > mat-icon { flex: none; color: #b39ddb; }
    mat-icon.inline { font-size: 16px; width: 16px; height: 16px; vertical-align: text-bottom; }
    .actions { display: flex; justify-content: flex-end; gap: 4px; margin-top: 4px; }
    button {
      min-height: 44px; padding: 0 12px; background: transparent; border: 0; border-radius: 8px;
      color: #b39ddb; font: inherit; font-weight: 600; cursor: pointer;
    }
    button:focus-visible { outline: 2px solid #7c4dff; }
  `],
})
export class InstallHintComponent {
  protected readonly hint = inject(InstallHintService);
  private readonly card = viewChild<ElementRef<HTMLElement>>('card');

  constructor() {
    // Leaving the page (or moving to another chapter) hides the hint without recording a choice.
    inject(Router).events
      .pipe(filter((e) => e instanceof NavigationStart), takeUntilDestroyed())
      .subscribe(() => this.hint.hide());

    // A Fullscreen tap is a user gesture: bring keyboard/screen-reader focus to the card. The
    // unprompted first-open appearance never steals focus.
    effect(() => {
      const el = this.card()?.nativeElement;
      if (el && this.hint.reason() === 'fullscreen') el.focus();
    });
  }
}
