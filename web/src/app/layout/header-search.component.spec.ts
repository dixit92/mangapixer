import { vi } from 'vitest';
import { TestBed, ComponentFixture } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { HeaderSearchComponent, isSearchPageUrl } from './header-search.component';
import { ApiService } from '../core/api/api.service';
import { SearchSuggestDto } from '../core/api/api-types';
import { QUICK_DEBOUNCE_MS } from '../features/search/quick-results/quick-results.model';

const ANSWER: SearchSuggestDto = {
  query: 'ber',
  series: [{ kind: 'series', id: 's1', label: 'Monster', detail: 'aka Kaibutsu', libraryId: 'L1', nodeKind: 'Folder' }],
  folders: [
    { kind: 'folder', id: 'f1', label: 'Berserk Deluxe', detail: 'Manga', libraryId: 'L1', nodeKind: 'Folder' },
    { kind: 'folder', id: 'f2', label: 'Berserk Prototype', libraryId: 'L1', nodeKind: 'Folder' },
  ],
  archives: [{ kind: 'archive', id: 'a1', label: 'Berserk Deluxe 01.cbz', detail: 'Berserk Deluxe', libraryId: 'L1', nodeKind: 'Archive' }],
  creators: [{ kind: 'creator', id: 'fv_c', label: 'Naoki Urasawa', filter: 'creator:fv_c', count: 1 }],
  tags: [{ kind: 'tag', id: 'fv_t', label: 'Time Travel', filter: 'tag:fv_t', count: 2 }],
  field: null,
};

describe('HeaderSearchComponent (quick results)', () => {
  let fixture: ComponentFixture<HeaderSearchComponent>;
  let router: Router;
  let searchSuggest: ReturnType<typeof vi.fn>;
  let navigate: ReturnType<typeof vi.spyOn>;

  function setup(response: SearchSuggestDto = ANSWER): void {
    searchSuggest = vi.fn().mockReturnValue(of(response));
    TestBed.configureTestingModule({
      imports: [HeaderSearchComponent],
      providers: [provideRouter([]), { provide: ApiService, useValue: { searchSuggest } }],
    });
    router = TestBed.inject(Router);
    navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    fixture = TestBed.createComponent(HeaderSearchComponent);
    fixture.detectChanges();
  }

  const input = () => fixture.nativeElement.querySelector('input') as HTMLInputElement;
  const options = () => [...fixture.nativeElement.querySelectorAll('[role="option"]')] as HTMLElement[];
  const panel = () => fixture.nativeElement.querySelector('[data-testid="header-search-panel"]') as HTMLElement | null;

  /** Focus, type, and let the 200 ms debounce fire. */
  function type(text: string): void {
    input().focus();
    input().dispatchEvent(new Event('focus'));
    input().value = text;
    input().dispatchEvent(new Event('input'));
    vi.advanceTimersByTime(QUICK_DEBOUNCE_MS);
    fixture.detectChanges();
  }

  function key(name: string): KeyboardEvent {
    const event = new KeyboardEvent('keydown', { key: name, bubbles: true, cancelable: true });
    input().dispatchEvent(event);
    fixture.detectChanges();
    return event;
  }

  beforeEach(() => vi.useFakeTimers());
  afterEach(() => { vi.runOnlyPendingTimers(); vi.useRealTimers(); });

  it('is an ARIA combobox that owns a listbox of grouped options', () => {
    setup();
    expect(input().getAttribute('role')).toBe('combobox');
    expect(input().getAttribute('aria-expanded')).toBe('false');
    expect(panel()).toBeNull();

    type('ber');
    expect(input().getAttribute('aria-expanded')).toBe('true');
    expect(input().getAttribute('aria-controls')).toBe('header-search-list');
    const list = fixture.nativeElement.querySelector('[role="listbox"]') as HTMLElement;
    expect(list.id).toBe('header-search-list');
    expect([...list.querySelectorAll('[role="group"]')].map((g) => g.getAttribute('data-group'))).toEqual(['series', 'folders', 'archives', 'creators', 'tags']);
    expect(options().map((o) => o.querySelector('.qr-label')!.textContent)).toEqual(
      ['Monster', 'Berserk Deluxe', 'Berserk Prototype', 'Berserk Deluxe 01.cbz', 'Naoki Urasawa', 'Time Travel']);
  });

  it('asks the server only after the typing pauses (about 200 ms)', () => {
    setup();
    input().dispatchEvent(new Event('focus'));
    for (const text of ['b', 'be', 'ber']) {
      input().value = text;
      input().dispatchEvent(new Event('input'));
      vi.advanceTimersByTime(50);
    }
    expect(searchSuggest).not.toHaveBeenCalled();
    vi.advanceTimersByTime(QUICK_DEBOUNCE_MS);
    expect(searchSuggest).toHaveBeenCalledTimes(1);
    expect(searchSuggest).toHaveBeenCalledWith('ber');
  });

  it('moves with Down / Up (aria-activedescendant follows) and opens the chosen result with Enter', () => {
    setup();
    type('ber');
    key('ArrowDown');
    key('ArrowDown');
    expect(options()[1].getAttribute('aria-selected')).toBe('true');
    expect(input().getAttribute('aria-activedescendant')).toBe(options()[1].id);
    key('ArrowUp');
    expect(options()[0].getAttribute('aria-selected')).toBe('true');

    key('ArrowDown');
    const enter = key('Enter');
    expect(enter.defaultPrevented).toBe(true);
    expect(navigate).toHaveBeenCalledWith(['/libraries', 'L1', 'browse', 'f1']);
    expect(input().value).toBe('');
    expect(panel()).toBeNull();
  });

  it('opens an archive in the reader, and a creator or tag as a tag view', () => {
    setup();
    type('ber');
    options()[3].click();
    expect(navigate).toHaveBeenLastCalledWith(['/reader', 'a1']);

    type('ber2');
    options()[4].click();
    expect(navigate).toHaveBeenLastCalledWith(['/search'], { queryParams: { f: ['creator:fv_c'] } });
  });

  it('opens the full Search page with the text on Enter when no result is chosen', () => {
    setup();
    type('berserk deluxe');
    key('Enter');
    expect(navigate).toHaveBeenCalledWith(['/search'], { queryParams: { q: 'berserk deluxe' } });
  });

  it('opens the full Search page even before any answer arrived', () => {
    setup();
    input().dispatchEvent(new Event('focus'));
    input().value = 'ber';
    input().dispatchEvent(new Event('input'));
    key('Enter');
    expect(navigate).toHaveBeenCalledWith(['/search'], { queryParams: { q: 'ber' } });
  });

  it('Escape closes the list and keeps the text; a second Escape clears the box', () => {
    setup();
    type('ber');
    expect(panel()).not.toBeNull();
    key('Escape');
    expect(panel()).toBeNull();
    expect(input().value).toBe('ber');
    key('Escape');
    expect(input().value).toBe('');
    // Down opens it again.
    type('ber');
    key('Escape');
    key('ArrowDown');
    expect(panel()).not.toBeNull();
  });

  it('completes a typed field\'s token when a value is chosen, and keeps listing', () => {
    setup({
      ...ANSWER,
      series: [], folders: [], archives: [], creators: [], tags: [],
      field: { name: 'creator', prefix: 'ura', start: 8, items: [{ label: 'Naoki Urasawa', insert: 'creator:"Naoki Urasawa"', count: 1 }] },
    });
    type('berserk creator:ura');
    expect(fixture.nativeElement.querySelector('.qr-heading')!.textContent).toBe('Creators');
    key('ArrowDown');
    key('Enter');
    expect(input().value).toBe('berserk creator:"Naoki Urasawa" ');
    expect(navigate).not.toHaveBeenCalled();
    vi.advanceTimersByTime(QUICK_DEBOUNCE_MS);
    expect(searchSuggest).toHaveBeenLastCalledWith('berserk creator:"Naoki Urasawa" ');
  });

  it('closes when the box loses focus, and \'/\' focuses it from the page', () => {
    setup();
    type('ber');
    input().dispatchEvent(new Event('blur'));
    vi.advanceTimersByTime(200);
    fixture.detectChanges();
    expect(panel()).toBeNull();

    const event = new KeyboardEvent('keydown', { key: '/', bubbles: true, cancelable: true });
    document.body.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(input());
  });

  it('1.50.0: ends the list with a keyboard hint line (arrows / Enter / Esc), hidden from screen readers', () => {
    setup();
    type('ber');
    const hint = panel()!.querySelector('[data-testid="quick-hint"]')!;
    expect(hint.textContent).toContain('to move');
    expect(hint.textContent).toContain('Enter');
    expect(hint.textContent).toContain('Esc');
    expect(hint.getAttribute('aria-hidden')).toBe('true');
  });

  it('1.50.0: knows the Search page by its path (query and fragment aside, a trailing slash allowed)', () => {
    expect(isSearchPageUrl('/search')).toBe(true);
    expect(isSearchPageUrl('/search?q=berserk&f=tag:fv_1')).toBe(true);
    expect(isSearchPageUrl('/search/')).toBe(true);
    expect(isSearchPageUrl('/search#top')).toBe(true);
    expect(isSearchPageUrl('/libraries/L1/browse/f1?q=search')).toBe(false);
    expect(isSearchPageUrl('/searching')).toBe(false);
    expect(isSearchPageUrl('/')).toBe(false);
  });

  it('1.50.0: steps aside on the Search page (that page has its own field with the quick results) and gives it the \'/\' key', async () => {
    vi.useRealTimers(); // router navigation is awaited here
    searchSuggest = vi.fn().mockReturnValue(of(ANSWER));
    TestBed.configureTestingModule({
      imports: [HeaderSearchComponent],
      providers: [provideRouter([{ path: 'search', children: [] }, { path: 'libraries', children: [] }]), { provide: ApiService, useValue: { searchSuggest } }],
    });
    router = TestBed.inject(Router);
    fixture = TestBed.createComponent(HeaderSearchComponent);
    const host = fixture.nativeElement as HTMLElement;
    const pageField = document.createElement('input');
    pageField.setAttribute('data-testid', 'search-input');
    document.body.appendChild(pageField);
    try {
      await router.navigateByUrl('/libraries');
      fixture.detectChanges();
      expect(host.classList).not.toContain('on-search-page');

      await router.navigateByUrl('/search?q=ber');
      fixture.detectChanges();
      expect(host.classList).toContain('on-search-page');
      const slash = new KeyboardEvent('keydown', { key: '/', bubbles: true, cancelable: true });
      document.body.dispatchEvent(slash);
      expect(slash.defaultPrevented).toBe(true);
      expect(document.activeElement).toBe(pageField);

      await router.navigateByUrl('/libraries');
      fixture.detectChanges();
      expect(host.classList).not.toContain('on-search-page');
    } finally {
      pageField.remove();
      vi.useFakeTimers(); // the suite's afterEach expects the fake clock
    }
  });

  it('leaves \'/\' alone while typing in another text field', () => {
    setup();
    const other = document.createElement('input');
    document.body.appendChild(other);
    other.focus();
    const event = new KeyboardEvent('keydown', { key: '/', bubbles: true, cancelable: true });
    other.dispatchEvent(event);
    expect(event.defaultPrevented).toBe(false);
    other.remove();
  });
});
