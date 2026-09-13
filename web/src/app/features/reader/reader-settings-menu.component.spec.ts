import { vi } from 'vitest';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MatBottomSheetRef, MAT_BOTTOM_SHEET_DATA } from '@angular/material/bottom-sheet';

import {
  ReaderSettingsMenuComponent, ReaderOptionsSheetComponent, ReaderOptionsHost,
  ReaderView, ViewPref, FitMode, ReadingDirection,
} from './reader-settings-menu.component';
import { ReaderPreferencesService } from '../../core/reading/reader-preferences.service';

describe('ReaderSettingsMenuComponent', () => {
  function create() {
    TestBed.configureTestingModule({
      imports: [ReaderSettingsMenuComponent],
      providers: [provideNoopAnimations()],
    });
    localStorage.clear();
    const fixture = TestBed.createComponent(ReaderSettingsMenuComponent);
    fixture.detectChanges();
    return { fixture, c: fixture.componentInstance, prefs: TestBed.inject(ReaderPreferencesService) };
  }

  it('renders a page-transition trigger button', () => {
    const { fixture } = create();
    const btn = (fixture.nativeElement as HTMLElement).querySelector('button[aria-label="Page transition"]');
    expect(btn).toBeTruthy();
  });

  it('choose() writes the preference through the shared service', () => {
    const { c, prefs } = create();
    c.choose('reveal');
    expect(prefs.pageAnimation()).toBe('reveal');
    c.choose('none');
    expect(prefs.pageAnimation()).toBe('none');
  });

  it('emits opened/closed for chrome pinning', () => {
    const { c } = create();
    let opened = 0;
    let closed = 0;
    c.opened.subscribe(() => opened++);
    c.closed.subscribe(() => closed++);
    c.opened.emit();
    c.closed.emit();
    expect(opened).toBe(1);
    expect(closed).toBe(1);
  });

  /**
   * 1.10.0 (F4): the active transition is marked with the accent highlight
   * (`selected-option` + aria-checked on a menuitemradio) and keeps its own
   * glyph; the checkmark is gone. The panel renders in a CDK overlay, so open it
   * and query `document`.
   */
  it('marks the active transition with the colour highlight, not a tick', () => {
    const { fixture, c } = create();
    c.choose('reveal');
    fixture.detectChanges();
    ((fixture.nativeElement as HTMLElement).querySelector('button[aria-label="Page transition"]') as HTMLElement).click();
    fixture.detectChanges();
    const panel = document.querySelector('.reader-options-menu') as HTMLElement;
    expect(panel).not.toBeNull();
    const items = Array.from(panel.querySelectorAll<HTMLElement>('button[mat-menu-item]'));
    expect(items.map((i) => i.getAttribute('role'))).toEqual(['menuitemradio', 'menuitemradio', 'menuitemradio']);
    const byLabel = (l: string) => items.find((i) => (i.textContent ?? '').trim().endsWith(l))!;
    expect(byLabel('Reveal').classList.contains('selected-option')).toBe(true);
    expect(byLabel('Reveal').getAttribute('aria-checked')).toBe('true');
    expect(byLabel('Reveal').querySelector('mat-icon')?.textContent?.trim()).toBe('gradient');
    expect(byLabel('Slide').classList.contains('selected-option')).toBe(false);
    expect(byLabel('Slide').getAttribute('aria-checked')).toBe('false');
    for (const i of items) expect(i.querySelector('mat-icon')?.textContent?.trim()).not.toBe('check');
  });
});

/**
 * Phone options sheet (1.10.0, F2). Rendered directly with a fake host (the
 * reader's live state as signals + spied actions) and a fake sheet ref, so the
 * tests cover the sheet's own contract: grouped radio chips with exactly one
 * checked per group, the effective-layout mapping, action dispatch, the webtoon
 * variant, and the close-then-navigate rule for chapter/help.
 */
describe('ReaderOptionsSheetComponent', () => {
  function makeHost(overrides: Partial<{
    view: ReaderView; viewPref: ViewPref | null; cover: boolean; fit: FitMode; dir: ReadingDirection;
    prev: boolean; next: boolean;
  }> = {}) {
    const o = { view: 'paged' as ReaderView, viewPref: null as ViewPref | null, cover: true, fit: 'screen' as FitMode,
      dir: 'ltr' as ReadingDirection, prev: false, next: true, ...overrides };
    const host: ReaderOptionsHost = {
      view: signal(o.view),
      viewPref: signal(o.viewPref),
      coverIsStandalone: signal(o.cover),
      fitMode: signal(o.fit),
      direction: signal(o.dir),
      webtoonWidthPct: signal(70),
      hasPrevChapter: signal(o.prev),
      hasNextChapter: signal(o.next),
      prevNeighbor: signal(o.prev ? { displayName: 'Ch 0' } : null),
      nextNeighbor: signal(o.next ? { displayName: 'Ch 2' } : null),
      chooseView: vi.fn(),
      chooseSpread: vi.fn(),
      setFitMode: vi.fn(),
      setDirection: vi.fn(),
      setWebtoonWidth: vi.fn(),
      prevChapter: vi.fn(),
      nextChapter: vi.fn(),
      toggleHelp: vi.fn(),
    };
    return host;
  }

  function create(host: ReaderOptionsHost = makeHost()) {
    const ref = { dismiss: vi.fn() };
    // Reset first so a single test may call create() several times (the
    // activeLayout test creates the sheet once per layout scenario).
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [ReaderOptionsSheetComponent],
      providers: [
        provideNoopAnimations(),
        { provide: MAT_BOTTOM_SHEET_DATA, useValue: host },
        { provide: MatBottomSheetRef, useValue: ref },
      ],
    });
    localStorage.clear();
    const fixture = TestBed.createComponent(ReaderOptionsSheetComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const group = (id: string) => el.querySelector(`[aria-labelledby="${id}"]`) as HTMLElement;
    const chips = (id: string) => Array.from(group(id).querySelectorAll<HTMLElement>('button[role="radio"]'));
    const checked = (id: string) => chips(id).filter((c) => c.getAttribute('aria-checked') === 'true');
    const chip = (id: string, label: string) => chips(id).find((c) => (c.textContent ?? '').trim().endsWith(label))!;
    return { fixture, c: fixture.componentInstance, el, host, ref, group, chips, checked, chip };
  }

  it('paged: four labelled radio groups, each with exactly one checked + highlighted chip', () => {
    const { checked, chips } = create();
    for (const id of ['reader-options-layout', 'reader-options-fit', 'reader-options-direction', 'reader-options-transition']) {
      expect(chips(id).length, id).toBeGreaterThan(1);
      const on = checked(id);
      expect(on.length, `${id} has one checked chip`).toBe(1);
      expect(on[0].classList.contains('selected'), `${id} checked chip is highlighted`).toBe(true);
      // Every chip keeps its own glyph; the selected-state is colour, never a tick.
      for (const c of chips(id)) expect(c.querySelector('mat-icon')?.textContent?.trim()).not.toBe('check');
    }
    expect(checked('reader-options-layout')[0].textContent).toContain('Single page');
    expect(checked('reader-options-fit')[0].textContent).toContain('Fit screen');
    expect(checked('reader-options-direction')[0].textContent).toContain('Left to right');
    expect(checked('reader-options-transition')[0].textContent).toContain('Slide');
  });

  it('activeLayout is the EFFECTIVE layout: auto pref wins, spread splits by cover offset, webtoon by view', () => {
    expect(create(makeHost({ view: 'paged', viewPref: 'auto' })).c.activeLayout()).toBe('auto');
    expect(create(makeHost({ view: 'spread', viewPref: 'auto' })).c.activeLayout()).toBe('auto');
    expect(create(makeHost({ view: 'spread', viewPref: 'spread', cover: true })).c.activeLayout()).toBe('spread-cover');
    expect(create(makeHost({ view: 'spread', viewPref: null, cover: false })).c.activeLayout()).toBe('spread');
    expect(create(makeHost({ view: 'paged', viewPref: null })).c.activeLayout()).toBe('paged');
    expect(create(makeHost({ view: 'webtoon', viewPref: 'spread' })).c.activeLayout()).toBe('webtoon');
  });

  it('layout chips dispatch to the reader exactly like the desktop menu items', () => {
    const { host, chip } = create();
    chip('reader-options-layout', 'Auto').click();
    expect(host.chooseView).toHaveBeenLastCalledWith('auto');
    chip('reader-options-layout', 'Single page').click();
    expect(host.chooseView).toHaveBeenLastCalledWith('paged');
    chip('reader-options-layout', 'Double page').click();
    expect(host.chooseSpread).toHaveBeenLastCalledWith(false);
    chip('reader-options-layout', 'Double, cover alone').click();
    expect(host.chooseSpread).toHaveBeenLastCalledWith(true);
    chip('reader-options-layout', 'Vertical').click();
    expect(host.chooseView).toHaveBeenLastCalledWith('webtoon');
  });

  it('fit / direction / transition chips apply immediately and keep the sheet open', () => {
    const { host, ref, chip } = create();
    chip('reader-options-fit', 'Fit width').click();
    expect(host.setFitMode).toHaveBeenCalledWith('width');
    chip('reader-options-direction', 'Right to left').click();
    expect(host.setDirection).toHaveBeenCalledWith('rtl');
    chip('reader-options-transition', 'Reveal').click();
    expect(TestBed.inject(ReaderPreferencesService).pageAnimation()).toBe('reveal');
    expect(ref.dismiss).not.toHaveBeenCalled();
  });

  it('the highlight follows the live reader state (signals), e.g. after a fit change', () => {
    const host = makeHost();
    const { fixture, checked } = create(host);
    (host.fitMode as ReturnType<typeof signal<FitMode>>).set('original');
    fixture.detectChanges();
    expect(checked('reader-options-fit')[0].textContent).toContain('Original size');
  });

  it('webtoon: paged-only groups are replaced by the page-width slider', () => {
    const { el, group } = create(makeHost({ view: 'webtoon' }));
    expect(group('reader-options-layout')).toBeTruthy();
    expect(el.querySelector('[aria-labelledby="reader-options-fit"]')).toBeNull();
    expect(el.querySelector('[aria-labelledby="reader-options-direction"]')).toBeNull();
    expect(el.querySelector('[aria-labelledby="reader-options-transition"]')).toBeNull();
    expect(el.querySelector('mat-slider.width-slider input')).toBeTruthy();
    expect(el.querySelector('#reader-options-width')?.textContent).toContain('70%');
  });

  it('chapter buttons reflect neighbour availability and close the sheet BEFORE navigating', () => {
    const { el, host, ref } = create(makeHost({ prev: false, next: true }));
    const buttons = Array.from(el.querySelectorAll<HTMLButtonElement>('.chapter-row button'));
    expect(buttons.length).toBe(2);
    const [prev, next] = buttons;
    expect(prev.disabled).toBe(true);
    expect(next.disabled).toBe(false);
    expect(next.getAttribute('aria-label')).toBe('Next chapter: Ch 2');

    const order: string[] = [];
    ref.dismiss.mockImplementation(() => order.push('dismiss'));
    (host.nextChapter as ReturnType<typeof vi.fn>).mockImplementation(() => order.push('next'));
    next.click();
    expect(order).toEqual(['dismiss', 'next']);
  });

  it('Reading help and Close dismiss the sheet (help then opens the overlay)', () => {
    const { el, host, ref } = create();
    (el.querySelector('.help-row') as HTMLElement).click();
    expect(ref.dismiss).toHaveBeenCalledTimes(1);
    expect(host.toggleHelp).toHaveBeenCalledTimes(1);
    (el.querySelector('button[aria-label="Close reader options"]') as HTMLElement).click();
    expect(ref.dismiss).toHaveBeenCalledTimes(2);
  });
});
