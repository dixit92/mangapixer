import { vi } from 'vitest';
import { WritableSignal, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { ReaderBookmarkMenuComponent, ReaderDisplayMenuComponent } from './reader-bar-menus.component';
import { ReaderOptionsHost, ReaderView, ViewPref, FitMode, ReadingDirection } from './reader-settings-menu.component';
import { ReaderPreferencesService } from '../../core/reading/reader-preferences.service';
import { WebtoonNavPreferencesService } from './webtoon-nav.service';

/** The reader as the menus see it: live signals in, spied actions out. */
function makeHost(view: ReaderView = 'paged') {
  return {
    view: signal<ReaderView>(view),
    viewPref: signal<ViewPref | null>(null),
    spreadShifted: signal(false),
    narrowPortrait: signal(false),
    fitMode: signal<FitMode>('screen'),
    direction: signal<ReadingDirection>('ltr'),
    webtoonWidthPct: signal(70),
    hasPrevChapter: signal(false),
    hasNextChapter: signal(false),
    prevNeighbor: signal(null),
    nextNeighbor: signal(null),
    isCurrentPageBookmarked: signal(false),
    currentFavorite: signal(false),
    seriesInfoAnchor: signal<string | null>(null),
    chooseView: vi.fn(),
    chooseSpread: vi.fn(),
    setFitMode: vi.fn(),
    setDirection: vi.fn(),
    setWebtoonWidth: vi.fn(),
    prevChapter: vi.fn(),
    nextChapter: vi.fn(),
    toggleHelp: vi.fn(),
    toggleBookmark: vi.fn(),
    openBookmarks: vi.fn(),
    toggleFavorite: vi.fn(),
    openSeriesInfo: vi.fn(),
  } satisfies ReaderOptionsHost;
}

function panelItems(panel: HTMLElement, group?: string): HTMLButtonElement[] {
  const scope = group ? panel.querySelector(`[role="group"][aria-label="${group}"]`) as HTMLElement : panel;
  return Array.from(scope.querySelectorAll<HTMLButtonElement>('button[mat-menu-item]'));
}

describe('ReaderDisplayMenuComponent (1.41.0)', () => {
  function create(view: ReaderView = 'paged') {
    TestBed.configureTestingModule({ imports: [ReaderDisplayMenuComponent], providers: [provideNoopAnimations()] });
    localStorage.clear();
    const host = makeHost(view);
    const fixture = TestBed.createComponent(ReaderDisplayMenuComponent);
    fixture.componentRef.setInput('host', host);
    fixture.componentRef.setInput('icon', 'import_contacts');
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const open = () => {
      (el.querySelector('button[aria-label="Display"]') as HTMLElement).click();
      fixture.detectChanges();
      return document.querySelector('.reader-options-menu') as HTMLElement;
    };
    return { fixture, c: fixture.componentInstance, host, el, open, prefs: TestBed.inject(ReaderPreferencesService) };
  }
  afterEach(() => localStorage.clear());

  it('one trigger, "Display", showing the glyph the reader passes (its reading mode on screen)', () => {
    const { el } = create();
    const buttons = Array.from(el.querySelectorAll('button'));
    expect(buttons.map((b) => b.getAttribute('aria-label'))).toEqual(['Display']);
    expect(buttons[0].textContent?.trim()).toBe('import_contacts');
  });

  it('marks the active page transition with the colour highlight, not a tick (moved from the 1.9.0 menu)', () => {
    const { fixture, prefs, open } = create();
    prefs.setPageAnimation('reveal');
    fixture.detectChanges();
    const items = panelItems(open(), 'Page transition');
    expect(items.map((i) => i.getAttribute('aria-label')))
      .toEqual(['Page transition: Slide', 'Page transition: Reveal', 'Page transition: None']);
    expect(items.map((i) => i.getAttribute('role'))).toEqual(Array(3).fill('menuitemradio'));
    expect(items[1].classList.contains('selected-option')).toBe(true);
    expect(items[1].getAttribute('aria-checked')).toBe('true');
    expect(items[1].querySelector('mat-icon')?.textContent?.trim()).toBe('gradient');
    expect(items[0].getAttribute('aria-checked')).toBe('false');
    for (const i of items) expect(i.querySelector('mat-icon')?.textContent?.trim()).not.toBe('check');
    items[2].click();
    expect(prefs.pageAnimation()).toBe('none');
  });

  it('layout picks go through the reader exactly like the sheet chips (both double-page pairings)', () => {
    const { host, open } = create();
    panelItems(open(), 'Layout')[3].click(); // Double page (shifted)
    expect(host.chooseSpread).toHaveBeenCalledWith(true);
  });

  it('the layout highlight is the sheet\'s rule: the effective layout, exactly one checked', () => {
    const { fixture, host, open } = create();
    host.view.set('spread');
    host.spreadShifted.set(true);
    fixture.detectChanges();
    const checked = panelItems(open(), 'Layout').filter((i) => i.getAttribute('aria-checked') === 'true');
    expect(checked.map((i) => i.getAttribute('aria-label'))).toEqual(['Layout: Double page (shifted)']);
  });

  it('webtoon: Tap to scroll (Off / 80 / 90 / 100) takes the transition\'s slot, highlighted the same way', () => {
    const { fixture, open } = create('webtoon');
    const panel = open();
    expect(panel.querySelector('[role="group"][aria-label="Page transition"]')).toBeNull();
    expect(panel.querySelector('[role="group"][aria-label="Reading direction"]')).toBeNull();
    const items = panelItems(panel, 'Tap to scroll');
    expect(items.map((i) => i.getAttribute('aria-checked'))).toEqual(['false', 'false', 'true', 'false']); // default 90
    expect(items[2].classList.contains('selected-option')).toBe(true);
    items[3].click();
    fixture.detectChanges();
    expect(TestBed.inject(WebtoonNavPreferencesService).tapStep()).toBe(100);
  });

  it('webtoon: the Page width slider shows the width and sets it through the reader, without closing the menu', () => {
    const { fixture, host, open } = create('webtoon');
    const panel = open();
    const group = panel.querySelector('[role="group"][aria-label="Page width"]') as HTMLElement;
    expect(group.querySelector('.menu-group-label')?.textContent).toContain('70%');
    const input = group.querySelector('input[type="range"]') as HTMLInputElement;
    expect(input.getAttribute('aria-label')).toBe('Webtoon page width');
    input.value = '45';
    input.dispatchEvent(new Event('input'));
    input.dispatchEvent(new Event('change'));
    expect(host.setWebtoonWidth).toHaveBeenCalledWith(45);
    (group.querySelector('mat-slider') as HTMLElement).click();
    fixture.detectChanges();
    expect(document.querySelector('.reader-options-menu')).toBeTruthy(); // still open
  });
});

describe('ReaderBookmarkMenuComponent (1.41.0)', () => {
  function create() {
    TestBed.configureTestingModule({ imports: [ReaderBookmarkMenuComponent], providers: [provideNoopAnimations()] });
    const host = makeHost();
    const fixture = TestBed.createComponent(ReaderBookmarkMenuComponent);
    fixture.componentRef.setInput('host', host);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const trigger = () => el.querySelector('button') as HTMLButtonElement;
    const open = () => {
      trigger().click();
      fixture.detectChanges();
      return document.querySelector('.reader-options-menu') as HTMLElement;
    };
    return { fixture, c: fixture.componentInstance, host, trigger, open };
  }

  it('the trigger shows the page\'s bookmark state: glyph, accent, label', () => {
    const { fixture, host, trigger } = create();
    expect(trigger().textContent?.trim()).toBe('bookmark_border');
    expect(trigger().classList.contains('marked')).toBe(false);
    (host.isCurrentPageBookmarked as WritableSignal<boolean>).set(true);
    fixture.detectChanges();
    expect(trigger().textContent?.trim()).toBe('bookmark');
    expect(trigger().classList.contains('marked')).toBe(true);
    expect(trigger().getAttribute('aria-label')).toBe('Bookmark menu, this page is bookmarked');
  });

  it('"Bookmark this page" toggles through the reader', () => {
    const { host, open } = create();
    const items = panelItems(open());
    expect(items.map((i) => (i.textContent ?? '').replace(/\s+/g, ' ').trim())).toEqual(['bookmark_add Bookmark this page', 'bookmarks Bookmarks…']);
    items[0].click();
    expect(host.toggleBookmark).toHaveBeenCalledTimes(1);
    expect(host.openBookmarks).not.toHaveBeenCalled();
  });

  it('"Bookmarks..." opens the list only after the menu has closed (opened / closed bracket it)', () => {
    const { c, host, open } = create();
    const events: string[] = [];
    c.opened.subscribe(() => events.push('opened'));
    c.closed.subscribe(() => events.push('closed'));
    host.openBookmarks.mockImplementation(() => events.push('list'));
    const panel = open();
    panelItems(panel)[1].click();
    expect(events).toEqual(['opened', 'closed', 'list']);
  });
});
