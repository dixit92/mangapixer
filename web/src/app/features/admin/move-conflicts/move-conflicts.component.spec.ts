import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

import { MoveConflictDto, MoveConflictPageDto } from '../../../core/api/api-types';
import { MoveConflictsComponent } from './move-conflicts.component';
import { fromText, resolvedSentence, sideText, whoText } from './move-conflict-labels';

const URL = '/api/v1/admin/move-conflicts';

function conflict(overrides: Partial<MoveConflictDto> = {}): MoveConflictDto {
  return {
    id: 'c1',
    kind: 'Progress',
    state: 'Open',
    userId: 'u1',
    userName: 'reader',
    nodeId: 'n1',
    title: 'Vol 03',
    isFolder: false,
    parentTitle: 'Synthetic Saga',
    libraryId: 'lib2',
    libraryName: 'Concluded',
    fromTitle: 'Vol 03',
    fromLibraryName: 'Ongoing',
    old: { present: true, progress: 'InProgress', page: 12, pageCount: 40 },
    new: { present: true, progress: 'InProgress', page: 3, pageCount: 40 },
    createdAt: '2026-10-01T10:00:00Z',
    ...overrides,
  };
}

function page(items: MoveConflictDto[], openCount = items.length, nextCursor: string | null = null): MoveConflictPageDto {
  return { items, openCount, nextCursor };
}

/**
 * Move conflicts page (1.31.0): lists the open conflicts with the state before the move and now, resolves one, a selection
 * or all (after a confirmation), switches to the resolved ones, and shows an empty state. Mocked HTTP backend.
 */
describe('MoveConflictsComponent', () => {
  function create() {
    TestBed.configureTestingModule({
      imports: [MoveConflictsComponent],
      providers: [provideNoopAnimations(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(MoveConflictsComponent);
    fixture.detectChanges();
    return { fixture, c: fixture.componentInstance, http: TestBed.inject(HttpTestingController), el: fixture.nativeElement as HTMLElement };
  }

  const click = (el: HTMLElement, testId: string) => (el.querySelector(`[data-testid="${testId}"]`) as HTMLButtonElement).click();

  it('lists open conflicts with both sides and resolves one with Use old', () => {
    const { fixture, http, el } = create();
    const req = http.expectOne((r) => r.url === URL);
    expect(req.request.params.get('state')).toBe('open');
    req.flush(page([
      conflict(),
      conflict({
        id: 'c2', kind: 'SeriesLink', userId: null, userName: null, isFolder: true, title: 'Synthetic Saga', nodeId: 'f1', parentTitle: null,
        old: { present: true, linkState: 'Confirmed', recordTitle: 'Right Saga' },
        new: { present: true, linkState: 'Auto', recordTitle: 'Wrong Saga' },
      }),
    ]));
    fixture.detectChanges();

    const rows = el.querySelectorAll('[data-testid="move-conflict-row"]');
    expect(rows).toHaveLength(2);
    expect(rows[0].querySelector('[data-testid="move-conflict-old"]')!.textContent).toContain('Page 12 of 40');
    expect(rows[0].querySelector('[data-testid="move-conflict-new"]')!.textContent).toContain('Page 3 of 40');
    expect(rows[0].textContent).toContain('Moved from Ongoing');
    expect(rows[0].textContent).toContain('reader');
    expect(rows[1].querySelector('[data-testid="move-conflict-old"]')!.textContent).toContain('Right Saga (confirmed)');
    expect(rows[1].querySelector('[data-testid="move-conflict-item"]')!.getAttribute('href')).toBe('/libraries/lib2/browse/f1');
    expect(el.querySelector('[data-testid="move-conflicts-open-count"]')!.textContent).toContain('2 open');

    (rows[0].querySelector('[data-testid="move-conflict-use-old"]') as HTMLButtonElement).click();
    const resolve = http.expectOne((r) => r.url === `${URL}/resolve`);
    expect(resolve.request.body).toEqual({ ids: ['c1'], resolution: 'Overwrite' });
    resolve.flush({ resolved: 1, skipped: 0 });
    http.expectOne((r) => r.url === URL).flush(page([conflict({ id: 'c2' })], 1));
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="move-conflicts-message"]')!.textContent).toContain('1 conflict resolved.');
    http.verify();
  });

  it('resolves a selection, and all open conflicts after a confirmation', () => {
    const { fixture, c, http, el } = create();
    http.expectOne((r) => r.url === URL).flush(page([conflict(), conflict({ id: 'c2' }), conflict({ id: 'c3' })]));
    fixture.detectChanges();

    c.toggle('c1', true);
    c.toggle('c3', true);
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="move-conflicts-keep-new-selected"]')!.textContent).toContain('(2)');
    click(el, 'move-conflicts-keep-new-selected');
    const selected = http.expectOne((r) => r.url === `${URL}/resolve`);
    expect(selected.request.body).toEqual({ ids: ['c1', 'c3'], resolution: 'Keep' });
    selected.flush({ resolved: 2, skipped: 0 });
    http.expectOne((r) => r.url === URL).flush(page([conflict({ id: 'c2' })], 1));
    fixture.detectChanges();
    expect(c.selected().size).toBe(0);

    click(el, 'move-conflicts-use-old-all');
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="move-conflicts-confirm"]')!.textContent).toContain('Use the old state for all 1 open conflicts?');
    http.expectNone((r) => r.url === `${URL}/resolve`);
    click(el, 'move-conflicts-confirm-yes');
    const all = http.expectOne((r) => r.url === `${URL}/resolve`);
    expect(all.request.body).toEqual({ all: true, resolution: 'Overwrite' });
    all.flush({ resolved: 1, skipped: 0 });
    http.expectOne((r) => r.url === URL).flush(page([], 0));
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="move-conflicts-confirm"]')).toBeNull();
    expect(el.querySelector('[data-testid="move-conflicts-empty"]')!.textContent).toContain('No open move conflicts.');
    http.verify();
  });

  it('shows resolved conflicts without actions and pages with Load more', () => {
    const { fixture, c, http, el } = create();
    http.expectOne((r) => r.url === URL).flush(page([], 0));
    fixture.detectChanges();

    c.setView('resolved');
    const resolved = http.expectOne((r) => r.url === URL);
    expect(resolved.request.params.get('state')).toBe('resolved');
    resolved.flush(page([conflict({ state: 'Kept', resolvedAt: '2026-10-01T11:00:00Z' })], 0, 'c1'));
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="move-conflict-state"]')!.textContent).toContain('New state kept');
    expect(el.querySelector('[data-testid="move-conflict-use-old"]')).toBeNull();
    expect(el.querySelector('[data-testid="move-conflicts-bulk"]')).toBeNull();

    click(el, 'move-conflicts-more');
    const more = http.expectOne((r) => r.url === URL);
    expect(more.request.params.get('cursor')).toBe('c1');
    more.flush(page([conflict({ id: 'c0', state: 'Overwritten' })], 0));
    fixture.detectChanges();
    expect(el.querySelectorAll('[data-testid="move-conflict-row"]')).toHaveLength(2);
    http.verify();
  });

  it('says so when the list cannot be loaded', () => {
    const { fixture, http, el } = create();
    http.expectOne((r) => r.url === URL).flush('boom', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();
    expect(el.querySelector('[role="alert"]')!.textContent).toContain('could not be loaded');
  });
});

describe('move conflict labels', () => {
  it('describes each side in words', () => {
    expect(sideText('Progress', { present: false })).toBe('Not started');
    expect(sideText('Progress', { present: true, progress: 'Completed', page: 40, pageCount: 40 })).toBe('Finished');
    expect(sideText('Progress', { present: true, progress: 'InProgress', page: 5 })).toBe('Page 5');
    expect(sideText('ReaderSettings', { present: true, readerMode: 'PagedRtl', otherReaderSettings: true })).toBe('Right to left + other settings');
    expect(sideText('ReaderSettings', { present: false })).toBe('Default settings');
    expect(sideText('SeriesLink', { present: true, linkState: 'DontMatch' })).toBe("Don't match");
    expect(sideText('SeriesLink', { present: false })).toBe('Not linked');
  });

  it('names who and where from, and the result', () => {
    expect(whoText(conflict({ kind: 'SeriesLink' }))).toBe('Admin (series link)');
    expect(whoText(conflict({ userName: null }))).toBe('A removed user');
    expect(fromText(conflict({ fromTitle: 'Old Name' }))).toBe('Moved from Ongoing (was Old Name)');
    expect(resolvedSentence(3, 1)).toBe('3 conflicts resolved. 1 already resolved or gone.');
  });
});
