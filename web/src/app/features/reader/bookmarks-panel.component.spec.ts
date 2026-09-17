import { vi } from 'vitest';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MatBottomSheetRef, MAT_BOTTOM_SHEET_DATA } from '@angular/material/bottom-sheet';

import { BookmarksPanelComponent, BookmarksPanelHost } from './bookmarks-panel.component';
import { BookmarkDto } from '../../core/api/api-types';

function makeBookmark(overrides: Partial<BookmarkDto> = {}): BookmarkDto {
  return {
    id: 'b1',
    itemId: 'item-1',
    ordinal: 0,
    normalizedAnchor: 0,
    label: null,
    createdAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

describe('BookmarksPanelComponent', () => {
  function makeHost(bookmarks: BookmarkDto[] = []): BookmarksPanelHost {
    return {
      bookmarks: signal(bookmarks),
      jumpToBookmark: vi.fn(),
      deleteBookmark: vi.fn(),
    };
  }

  function create(host: BookmarksPanelHost = makeHost()) {
    const ref = { dismiss: vi.fn() };
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      imports: [BookmarksPanelComponent],
      providers: [
        provideNoopAnimations(),
        { provide: MAT_BOTTOM_SHEET_DATA, useValue: host },
        { provide: MatBottomSheetRef, useValue: ref },
      ],
    });
    const fixture = TestBed.createComponent(BookmarksPanelComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return { fixture, c: fixture.componentInstance, el, host, ref };
  }

  it('shows an empty state with no bookmarks', () => {
    const { el } = create(makeHost([]));
    expect(el.querySelector('.empty')).toBeTruthy();
    expect(el.querySelectorAll('.bookmark-row').length).toBe(0);
  });

  it('lists every bookmark, labelled or falling back to its page number', () => {
    const { el } = create(makeHost([
      makeBookmark({ id: 'b1', ordinal: 0, label: 'Cliffhanger' }),
      makeBookmark({ id: 'b2', ordinal: 4, label: null }),
    ]));
    const rows = Array.from(el.querySelectorAll('.bookmark-row'));
    expect(rows.length).toBe(2);
    expect(rows[0].querySelector('.label')?.textContent).toBe('Cliffhanger');
    expect(rows[1].querySelector('.label')?.textContent).toBe('Page 5'); // ordinal 4 → 1-based
  });

  it('tapping a row jumps to that bookmark and dismisses the sheet', () => {
    const bookmark = makeBookmark({ id: 'b1', ordinal: 3 });
    const { el, host, ref } = create(makeHost([bookmark]));
    (el.querySelector('.jump') as HTMLButtonElement).click();
    expect(ref.dismiss).toHaveBeenCalled();
    expect(host.jumpToBookmark).toHaveBeenCalledWith(bookmark);
  });

  it('the delete button removes a bookmark WITHOUT dismissing the sheet', () => {
    const bookmark = makeBookmark({ id: 'b1', ordinal: 3 });
    const { el, host, ref } = create(makeHost([bookmark]));
    (el.querySelector('.delete') as HTMLButtonElement).click();
    expect(host.deleteBookmark).toHaveBeenCalledWith(bookmark);
    expect(ref.dismiss).not.toHaveBeenCalled();
  });

  it('the close button dismisses the sheet', () => {
    const { el, ref } = create();
    (el.querySelector('.close') as HTMLButtonElement).click();
    expect(ref.dismiss).toHaveBeenCalled();
  });
});
