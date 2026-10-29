import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

import { QuickEntry, QuickSection } from './quick-results.model';

/**
 * The grouped quick results (1.49.0, lane O) as an ARIA listbox: the owner of the text field (the header box, or the phone Search page)
 * points its combobox at this list with `aria-controls` / `aria-activedescendant` (`idPrefix` + the entry's key) and keeps the keyboard.
 * A press on the list does not take focus from the field (mousedown is cancelled), so the field's blur never races a click.
 * Theming: `--mp-*` tokens only.
 */
@Component({
  selector: 'app-quick-results',
  standalone: true,
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="qr" role="listbox" [attr.id]="listId()" [attr.aria-label]="label()" (mousedown)="$event.preventDefault()">
      @for (section of sections(); track section.id) {
        <div class="qr-group" role="group" [attr.aria-labelledby]="idPrefix() + 'h-' + section.id" [attr.data-group]="section.id">
          <div class="qr-heading" [attr.id]="idPrefix() + 'h-' + section.id" role="presentation">{{ section.title }}</div>
          @for (entry of section.entries; track entry.key) {
            <div class="qr-option" role="option" [attr.id]="idPrefix() + entry.key" [class.active]="entry.key === activeKey()"
                 [attr.aria-selected]="entry.key === activeKey()" tabindex="-1" (click)="picked.emit(entry)"
                 (keydown.enter)="picked.emit(entry)" data-testid="quick-result">
              <mat-icon class="qr-icon" aria-hidden="true">{{ entry.icon }}</mat-icon>
              <span class="qr-text">
                <span class="qr-label">{{ entry.label }}</span>
                @if (entry.detail) {
                  <span class="qr-detail">{{ entry.detail }}</span>
                }
              </span>
              @if (entry.count !== null) {
                <span class="qr-count" [attr.aria-label]="entry.count + ' works'">{{ entry.count }}</span>
              }
            </div>
          }
        </div>
      }
    </div>
    @if (keyboardHint()) {
      <!-- 1.50.0 (lane U): the keys, for the boxes that have a keyboard focus on them (the screen-reader line says the same in words). -->
      <div class="qr-hint" aria-hidden="true" data-testid="quick-hint">
        <kbd>&uarr;</kbd><kbd>&darr;</kbd> to move<span class="qr-hint-dot">&middot;</span><kbd>Enter</kbd> to open<span class="qr-hint-dot">&middot;</span><kbd>Esc</kbd> to close
      </div>
    }
  `,
  styles: [`
    :host { display: block; min-width: 0; }
    .qr { max-height: min(70vh, 520px); overflow-y: auto; overscroll-behavior: contain; padding: 4px 0; color: var(--mp-text); }
    .qr-heading {
      padding: 8px 12px 2px; font-size: 11px; font-weight: 600; letter-spacing: 0.6px; text-transform: uppercase; color: var(--mp-text-dim);
    }
    .qr-option {
      display: flex; align-items: center; gap: 10px; min-height: 40px; padding: 4px 12px; cursor: pointer; min-width: 0;
    }
    .qr-option:hover { background: rgb(var(--mp-ink-rgb) / 0.06); }
    .qr-option.active { background: rgb(var(--mp-accent-rgb) / 0.18); box-shadow: inset 3px 0 0 var(--mp-accent); }
    .qr-icon { flex: none; font-size: 20px; width: 20px; height: 20px; color: var(--mp-text-muted); }
    .qr-text { display: flex; flex-direction: column; min-width: 0; flex: 1 1 auto; }
    .qr-label, .qr-detail { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .qr-label { font-size: 14px; }
    .qr-detail { font-size: 12px; color: var(--mp-text-muted); }
    .qr-count { flex: none; font-size: 12px; color: var(--mp-text-muted); }
    .qr-hint {
      display: flex; flex-wrap: wrap; align-items: center; gap: 4px; padding: 6px 12px 4px; margin-top: 2px; font-size: 11px;
      color: var(--mp-text-dim); border-top: 1px solid rgb(var(--mp-ink-rgb) / 0.1);
    }
    .qr-hint kbd {
      font: inherit; font-size: 10px; padding: 0 4px; border-radius: 3px; border: 1px solid rgb(var(--mp-ink-rgb) / 0.25);
      background: rgb(var(--mp-ink-rgb) / 0.06); color: var(--mp-text-muted);
    }
    .qr-hint-dot { margin: 0 4px; }
  `],
})
export class QuickResultsComponent {
  readonly sections = input.required<readonly QuickSection[]>();
  /** The key of the keyboard's entry, or null. */
  readonly activeKey = input<string | null>(null);
  /** Prefix of every option / heading element id (unique per field on the page). */
  readonly idPrefix = input('qr-');
  readonly listId = input<string | null>(null);
  readonly label = input('Quick results');
  /** 1.50.0 (lane U): show the keyboard line (arrows / Enter / Esc) under the list - off on a phone, which has no keyboard in play. */
  readonly keyboardHint = input(false);
  readonly picked = output<QuickEntry>();
}
