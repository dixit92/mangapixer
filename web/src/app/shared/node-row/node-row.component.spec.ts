import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';

import { CatalogNodeDto } from '../../core/api/api-types';
import { NodeRowComponent, directionShort } from './node-row.component';

/** The shared list row (1.30.0): what the folder browse and the volume stack page both render in List view. */
describe('NodeRowComponent', () => {
  function node(over: Partial<CatalogNodeDto> = {}): CatalogNodeDto {
    return {
      id: 'n1', parentId: 'p', libraryId: 'lib1', kind: 'Archive', displayName: 'Chapter One', availability: 'Available',
      coverUrl: '/api/v1/items/n1/cover', childFolderCount: null, childArchiveCount: null, pageCount: 20, readingState: null,
      lastReadPage: null, readerDefault: null, isRead: false, readRollup: null, hasSeriesInfo: false, ...over,
    } as CatalogNodeDto;
  }

  function create(inputs: Record<string, unknown>) {
    TestBed.configureTestingModule({
      imports: [NodeRowComponent],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()],
    });
    const fixture = TestBed.createComponent(NodeRowComponent);
    const events: string[] = [];
    const c = fixture.componentInstance;
    c.activate.subscribe(() => events.push('activate'));
    c.rowSelect.subscribe(() => events.push('rowSelect'));
    c.pressStart.subscribe(() => events.push('pressStart'));
    c.pressEnd.subscribe(() => events.push('pressEnd'));
    // As the host's NodeSelection does: the prompt buttons sit inside the link, so their click must not reach it.
    c.selectToHere.subscribe((e) => { e.stopPropagation(); events.push('selectToHere'); });
    c.dismissPrompt.subscribe((e) => { e.stopPropagation(); events.push('dismissPrompt'); });
    for (const [key, value] of Object.entries({ node: node(), ...inputs })) fixture.componentRef.setInput(key, value);
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, events };
  }

  it('renders the cover, title, subtitle and a link, with the star and the leading checkbox', () => {
    const { el } = create({ link: ['/reader', 'n1'], subtitle: 'Ch. 1 · 20 pages' });

    expect(el.querySelector('img')!.getAttribute('src')).toBe('/api/v1/items/n1/cover');
    expect(el.querySelector('.node-title')!.textContent).toBe('Chapter One');
    expect(el.querySelector('.node-sub')!.textContent).toBe('Ch. 1 · 20 pages');
    expect(el.querySelector('a.node-card')!.getAttribute('href')).toBe('/reader/n1');
    expect(el.querySelector('.row-select')!.getAttribute('aria-label')).toBe('Select Chapter One');
    expect(el.querySelector('app-star-toggle')).not.toBeNull();
    expect(el.querySelector('app-info-toggle')).not.toBeNull();
  });

  it('has no link and no (i) in select mode, and shows the check', () => {
    const { el } = create({ link: null, selectMode: true, selected: true });

    expect(el.querySelector('a.node-card')!.getAttribute('href')).toBeNull();
    expect(el.querySelector('app-info-toggle')).toBeNull();
    expect(el.querySelector('.check.on')).not.toBeNull();
    expect(el.querySelector('.node-wrap')!.classList).toContain('selected');
    expect(el.querySelector('.row-select')!.getAttribute('aria-checked')).toBe('true');
    expect(el.querySelector('.row-select')!.getAttribute('aria-label')).toBe('Deselect Chapter One');
  });

  it('shows the read and reading badges, a folder rollup and an admin folder\'s direction chip', () => {
    expect(create({ node: node({ isRead: true }) }).el.querySelector('.badge.read')).not.toBeNull();
    TestBed.resetTestingModule();
    expect(create({ node: node({ readingState: 'InProgress' }) }).el.querySelector('.badge.reading')).not.toBeNull();
    TestBed.resetTestingModule();
    const folder = create({ node: node({ kind: 'Folder', readRollup: 'Read', readerDefault: 'PagedRtl' }), showDirection: true });
    expect(folder.el.querySelector('app-folder-rollup-badge .badge.read')).not.toBeNull();
    expect(folder.el.querySelector('.badge.dir')!.textContent).toBe('RTL');
    TestBed.resetTestingModule();
    expect(create({ node: node({ kind: 'Folder', readerDefault: 'PagedRtl' }) }).el.querySelector('.badge.dir')).toBeNull();
  });

  it('draws a volume stack as a row of its own: the incomplete mark, no star, no (i)', () => {
    const { el } = create({
      node: node({
        kind: 'VolumeStack', displayName: 'Volume 3',
        volumeStack: { key: '3', label: 'Volume 3', presentCount: 8, chapterCount: 9, missingCount: 1, extraCount: 0, hasVolumeArchive: false, confidence: 'Exact' },
      }),
    });

    expect(el.querySelector('[data-testid="stack-incomplete"]')!.textContent!.trim()).toBe('8/9');
    expect(el.querySelector('app-star-toggle')).toBeNull();
    expect(el.querySelector('app-info-toggle')).toBeNull();
    expect(el.querySelector('.row-select')).not.toBeNull();
    expect(el.querySelector('mat-icon.cover-fallback')!.textContent).toBe('collections_bookmark');
  });

  it('can hide the checkbox (a row that cannot be selected)', () => {
    const { el } = create({ selectable: false, selectMode: true });

    expect(el.querySelector('.row-select')).toBeNull();
    expect(el.querySelector('.check')).toBeNull();
  });

  it('hands the raw events to its host: click, checkbox, touch press and the range prompt', () => {
    const { fixture, el, events } = create({ rangePrompt: true });
    const link = el.querySelector('a.node-card') as HTMLElement;

    link.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
    (el.querySelector('.row-select') as HTMLElement).click();
    link.dispatchEvent(new PointerEvent('pointerdown', { pointerType: 'touch', bubbles: true }));
    link.dispatchEvent(new PointerEvent('pointerup', { bubbles: true }));
    (el.querySelector('.range-prompt button:not(.cancel)') as HTMLElement).click();
    (el.querySelector('.range-prompt button.cancel') as HTMLElement).click();
    fixture.detectChanges();

    expect(events).toEqual(['activate', 'rowSelect', 'pressStart', 'pressEnd', 'selectToHere', 'dismissPrompt']);
  });

  it('labels the direction override', () => {
    expect(directionShort('PagedLtr')).toBe('LTR');
    expect(directionShort('PagedRtl')).toBe('RTL');
    expect(directionShort('VerticalWebtoon')).toBe('Vertical');
    expect(directionShort('Spread' as never)).toBe('Spread');
  });
});
