import { vi } from 'vitest';
import { Component, viewChild } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MatMenuModule, MatMenuTrigger } from '@angular/material/menu';
import { MatBottomSheet } from '@angular/material/bottom-sheet';
import { of } from 'rxjs';

import { ReaderComponent } from './reader.component';
import { ManifestPageEntry } from '../../core/api/api-types';

/**
 * 1.24.0 Escape fix. Pressing Escape to close a reader menu (Reading mode, Image
 * fit, the settings MatMenu, the phone options bottom sheet) used to ALSO reach the
 * reader's `window:keydown` listener and run its Escape branch (`goBack()`), so one
 * key both closed the menu and left the reader. These tests drive REAL Material
 * overlays (not mocks): MatMenu and MatBottomSheet call `preventDefault()` on the
 * Escape they consume, the event then bubbles to window where the reader's
 * HostListener (registered at component creation) must ignore it. Keys MatMenu does
 * not prevent (Left/Right, typeahead letters) must not turn pages either.
 */

function makePages(n: number): ManifestPageEntry[] {
  return Array.from({ length: n }, (_, i) => ({
    entryKey: `p${i}`, pageIndex: i, mediaType: 'image/png', width: 800, height: 1200,
    animationState: 'None', byteSize: 1000,
  }));
}

@Component({
  selector: 'app-escape-host',
  imports: [MatMenuModule],
  template: `
    <button type="button" id="trigger" [matMenuTriggerFor]="menu">Mode</button>
    <mat-menu #menu="matMenu">
      <button mat-menu-item type="button">Single page</button>
      <button mat-menu-item type="button">Double page</button>
    </mat-menu>
  `,
})
class MenuHostComponent {
  readonly trigger = viewChild.required(MatMenuTrigger);
}

@Component({ selector: 'app-escape-sheet', template: '<button type="button" id="sheet-btn">Option</button>' })
class SheetComponent {}

/** A bubbling, cancelable keydown with the legacy `keyCode` MatMenu/the overlay dispatcher read. */
function keydown(key: string, keyCode: number): KeyboardEvent {
  const ev = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true });
  Object.defineProperty(ev, 'keyCode', { get: () => keyCode });
  return ev;
}

describe('ReaderComponent Escape / keys consumed by an open menu (1.24.0)', () => {
  function setup() {
    TestBed.configureTestingModule({
      imports: [ReaderComponent, MenuHostComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideNoopAnimations(),
        { provide: ActivatedRoute, useValue: { paramMap: of({ get: () => 'item-1' }) } },
      ],
    });
    // No detectChanges on the reader: ngOnInit (HTTP) never runs, but its host
    // listeners are live from creation, which is all these tests need.
    const reader = TestBed.createComponent(ReaderComponent).componentInstance;
    reader.pages.set(makePages(5));
    reader.view.set('paged');
    reader.direction.set('ltr');
    reader.currentPage.set(1);
    reader.phase.set('ready');
    const goBack = vi.spyOn(reader, 'goBack').mockImplementation(() => { /* stay */ });
    const host = TestBed.createComponent(MenuHostComponent);
    host.detectChanges();
    return { reader, goBack, host };
  }

  function openMenu(host: ReturnType<typeof setup>['host']): HTMLElement {
    host.componentInstance.trigger().openMenu();
    host.detectChanges();
    const panel = document.querySelector('.mat-mdc-menu-panel') as HTMLElement | null;
    expect(panel).not.toBeNull();
    return panel!;
  }

  afterEach(() => {
    vi.restoreAllMocks();
    document.querySelectorAll('.cdk-overlay-container').forEach(el => el.remove());
  });

  it('Escape inside an open MatMenu closes only the menu; a second Escape leaves the reader', async () => {
    const { goBack, host } = setup();
    const panel = openMenu(host);

    const first = keydown('Escape', 27);
    panel.dispatchEvent(first);
    host.detectChanges();

    expect(first.defaultPrevented).toBe(true); // MatMenu consumed it (the fix's premise)
    expect(host.componentInstance.trigger().menuOpen).toBe(false);
    expect(goBack).not.toHaveBeenCalled();

    await host.whenStable(); // the closed panel detaches (as it has long since, for a human's 2nd key)
    expect(document.querySelector('.mat-mdc-menu-panel')).toBeNull();
    document.body.dispatchEvent(keydown('Escape', 27));
    expect(goBack).toHaveBeenCalledTimes(1);
  });

  it('Escape that closes a menu does not exit fullscreen either', () => {
    const { reader, goBack, host } = setup();
    reader.isFullscreen.set(true);
    const toggle = vi.spyOn(reader, 'toggleFullscreen').mockImplementation(() => { /* noop */ });
    openMenu(host).dispatchEvent(keydown('Escape', 27));
    expect(toggle).not.toHaveBeenCalled();
    expect(goBack).not.toHaveBeenCalled();
  });

  it('Left/Right arrows inside an open menu do not turn the page (MatMenu does not preventDefault them)', () => {
    const { reader, host } = setup();
    const panel = openMenu(host);
    const right = keydown('ArrowRight', 39);
    panel.dispatchEvent(right);
    expect(right.defaultPrevented).toBe(false); // why the overlay-target guard exists
    expect(reader.currentPage()).toBe(1);
    panel.dispatchEvent(keydown('ArrowLeft', 37));
    expect(reader.currentPage()).toBe(1);
  });

  it('Home/End and shortcut letters inside an open menu are ignored by the reader', () => {
    const { reader, host } = setup();
    const panel = openMenu(host);
    const item = panel.querySelector('button') as HTMLElement;
    item.dispatchEvent(keydown('End', 35));
    item.dispatchEvent(keydown('d', 68)); // typeahead letter; would toggle double page
    expect(reader.currentPage()).toBe(1);
    expect(reader.view()).toBe('paged');
  });

  it('Escape in the phone options bottom sheet dismisses it without leaving the reader', async () => {
    const { goBack } = setup();
    const ref = TestBed.inject(MatBottomSheet).open(SheetComponent);
    const dismissed = vi.fn();
    ref.afterDismissed().subscribe(dismissed);
    await ref.afterOpened().toPromise();
    const btn = document.getElementById('sheet-btn') as HTMLElement;
    expect(btn).not.toBeNull();

    const ev = keydown('Escape', 27);
    btn.dispatchEvent(ev);

    expect(ev.defaultPrevented).toBe(true);
    expect(goBack).not.toHaveBeenCalled();
  });

  it('without an overlay, Escape and arrows still work as before', () => {
    const { reader, goBack } = setup();
    document.body.dispatchEvent(keydown('ArrowRight', 39));
    expect(reader.currentPage()).toBe(2);
    document.body.dispatchEvent(keydown('Escape', 27));
    expect(goBack).toHaveBeenCalledTimes(1);
  });
});
