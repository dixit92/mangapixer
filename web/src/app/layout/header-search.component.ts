import { ChangeDetectionStrategy, Component, ElementRef, HostListener, inject, signal, viewChild } from '@angular/core';
import { Router } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';

import { QuickResultsComponent } from '../features/search/quick-results/quick-results.component';
import { QuickEntry, QuickResults, completeToken } from '../features/search/quick-results/quick-results.model';

/**
 * The search box of the app header on desktop and tablet (1.49.0, lane O; owner decision 7). Typing shows grouped quick results (series,
 * folders, archives, creators, tags - or the values of a typed field such as `creator:ura`) about 200 ms after the last keystroke.
 * Keyboard first: Up / Down move through the results, Enter opens the chosen one (a field value completes the token), Enter with none
 * chosen opens the full Search page with the text, Escape closes the list (a second Escape clears the box), and `/` focuses the box from
 * anywhere that is not a text field. ARIA combobox (1.2): the input owns the listbox through `aria-controls` / `aria-activedescendant`.
 * Not mounted on phones (they keep the Search page, which has the same results under its box). Theming through `--mp-*` tokens.
 */
@Component({
  selector: 'app-header-search',
  standalone: true,
  imports: [MatIconModule, QuickResultsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="hs" role="search">
      <mat-icon class="hs-icon" aria-hidden="true">search</mat-icon>
      <input #box class="hs-input" type="text" role="combobox" autocomplete="off" autocapitalize="off" spellcheck="false"
             aria-label="Search libraries" aria-autocomplete="list" aria-haspopup="listbox" aria-controls="header-search-list"
             [attr.aria-expanded]="expanded()" [attr.aria-activedescendant]="activeId()"
             placeholder="Search" [value]="text()" data-testid="header-search-input"
             (input)="onInput($any($event.target).value)" (keydown)="onKey($event)" (focus)="onFocus()" (blur)="onBlur()">
    </div>
    <span class="hs-status" role="status" aria-live="polite">{{ statusText() }}</span>
    @if (expanded()) {
      <div class="hs-panel" [style.top.px]="panelTop()" [style.left.px]="panelLeft()" [style.width.px]="panelWidth()"
           data-testid="header-search-panel">
        <app-quick-results [sections]="quick.sections()" [activeKey]="quick.activeEntry()?.key ?? null" idPrefix="hs-"
                           listId="header-search-list" label="Quick results" (picked)="pick($event)" />
      </div>
    }
  `,
  styles: [`
    :host { display: block; flex: 0 1 340px; min-width: 120px; margin: 0 8px; }
    .hs { position: relative; display: flex; align-items: center; height: 40px; }
    .hs-icon {
      position: absolute; left: 10px; font-size: 20px; width: 20px; height: 20px; pointer-events: none; opacity: 0.8;
    }
    .hs-input {
      width: 100%; height: 100%; box-sizing: border-box; padding: 0 12px 0 38px; border: 1px solid transparent; border-radius: 20px;
      background: rgb(var(--mp-ink-rgb) / 0.14); color: inherit; font: inherit; font-size: 14px; outline: none; min-width: 0;
    }
    .hs-input::placeholder { color: inherit; opacity: 0.7; }
    .hs-input:hover { background: rgb(var(--mp-ink-rgb) / 0.2); }
    .hs-input:focus-visible { border-color: currentColor; background: rgb(var(--mp-ink-rgb) / 0.2); }
    .hs-status { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
    /* Fixed, placed from the box's rectangle (see place()): a toolbar clips nothing, but the panel must stay inside the screen. */
    .hs-panel {
      position: fixed; z-index: 1100; box-sizing: border-box; border-radius: 8px; overflow: hidden;
      background: var(--mp-surface-high); color: var(--mp-text); border: 1px solid rgb(var(--mp-ink-rgb) / 0.16);
      box-shadow: 0 8px 24px rgb(var(--mp-shade-rgb) / 0.35);
    }
  `],
})
export class HeaderSearchComponent {
  private readonly router = inject(Router);
  private readonly box = viewChild.required<ElementRef<HTMLInputElement>>('box');

  readonly quick = new QuickResults();
  readonly text = signal('');
  private readonly focused = signal(false);
  private readonly dismissed = signal(false);
  readonly panelTop = signal(56);
  readonly panelLeft = signal(8);
  readonly panelWidth = signal(420);

  /** The list is open while the box has focus, has text, and there is something to list (not after Escape). */
  readonly expanded = () => this.focused() && !this.dismissed() && this.quick.entries().length > 0 && this.text().trim().length > 0;
  readonly activeId = () => (this.expanded() && this.quick.activeEntry() ? `hs-${this.quick.activeEntry()!.key}` : null);
  readonly statusText = () => {
    if (!this.expanded()) return '';
    const n = this.quick.entries().length;
    return `${n} result${n === 1 ? '' : 's'}, use the up and down arrow keys`;
  };

  private blurTimer: ReturnType<typeof setTimeout> | null = null;

  /** `/` focuses the box from anywhere that is not a text field (the usual search shortcut). */
  @HostListener('document:keydown', ['$event'])
  onDocumentKey(event: KeyboardEvent): void {
    if (event.key !== '/' || event.ctrlKey || event.metaKey || event.altKey || event.defaultPrevented) return;
    const target = event.target as HTMLElement | null;
    if (target && (target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName))) return;
    event.preventDefault();
    this.box().nativeElement.focus();
  }

  @HostListener('window:resize')
  place(): void {
    const rect = this.box().nativeElement.getBoundingClientRect();
    const width = Math.min(460, Math.max(rect.width, 320), window.innerWidth - 16);
    this.panelWidth.set(width);
    this.panelLeft.set(Math.max(8, Math.min(rect.left, window.innerWidth - width - 8)));
    this.panelTop.set(rect.bottom + 4);
  }

  onFocus(): void {
    if (this.blurTimer) clearTimeout(this.blurTimer);
    this.focused.set(true);
    this.dismissed.set(false);
    this.place();
  }

  onBlur(): void {
    // A click on a result is handled before the list goes (the list never takes focus; this is a safety margin for touch).
    this.blurTimer = setTimeout(() => this.focused.set(false), 120);
  }

  onInput(value: string): void {
    this.text.set(value);
    this.dismissed.set(false);
    this.place();
    this.quick.ask(value);
  }

  onKey(event: KeyboardEvent): void {
    switch (event.key) {
      case 'ArrowDown':
      case 'ArrowUp':
        if (this.dismissed()) this.dismissed.set(false);
        event.preventDefault();
        this.quick.move(event.key === 'ArrowDown' ? 1 : -1);
        break;
      case 'Enter': {
        event.preventDefault();
        const entry = this.expanded() ? this.quick.activeEntry() : null;
        if (entry) this.pick(entry);
        else this.search();
        break;
      }
      case 'Escape':
        if (this.expanded()) {
          this.dismissed.set(true);
          this.quick.active.set(-1);
        } else if (this.text()) {
          this.reset();
        }
        break;
      case 'Tab':
        this.dismissed.set(true);
        break;
    }
  }

  /** Choosing a result (Enter on the active one, or a click). */
  pick(entry: QuickEntry): void {
    const action = entry.action;
    if (action.type === 'complete') {
      // A typed field's value completes the token; the list stays for the next word.
      const text = completeToken(this.text(), action.start, action.insert);
      this.text.set(text);
      this.box().nativeElement.value = text;
      this.box().nativeElement.focus();
      this.quick.ask(text);
      return;
    }
    this.reset();
    if (action.type === 'open') void this.router.navigate(action.commands);
    else void this.router.navigate(['/search'], { queryParams: { f: [action.filter] } });
  }

  /** Enter with no result chosen: the full Search page with the text. */
  private search(): void {
    const text = this.text().trim();
    if (!text) return;
    this.reset();
    void this.router.navigate(['/search'], { queryParams: { q: text } });
  }

  private reset(): void {
    this.text.set('');
    this.box().nativeElement.value = '';
    this.dismissed.set(false);
    this.quick.clear();
    this.box().nativeElement.blur();
  }
}
