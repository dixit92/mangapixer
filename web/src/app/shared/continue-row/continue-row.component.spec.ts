import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';

import { ContinueRowComponent } from './continue-row.component';
import { CatalogNodeDto } from '../../core/api/api-types';

/** Host that drives the Continue row through its signal input, as the browse view will. */
@Component({
  standalone: true,
  imports: [ContinueRowComponent],
  template: `<app-continue-row [node]="nextUnread()" />`,
})
class HostComponent {
  readonly nextUnread = signal<CatalogNodeDto | null>(null);
}

function makeNode(overrides: Partial<CatalogNodeDto> = {}): CatalogNodeDto {
  return {
    id: 'item1',
    parentId: 'folder1',
    libraryId: 'lib1',
    kind: 'Archive',
    displayName: 'Chapter 1.cbz',
    availability: 'Available',
    coverUrl: '/api/v1/items/item1/cover',
    childFolderCount: null,
    childArchiveCount: null,
    pageCount: 20,
    readingState: 'InProgress',
    lastReadPage: 3,
    readerDefault: null,
    isRead: false,
    readRollup: null,
    ...overrides,
  };
}

/**
 * Continue row (1.7.0): a standalone presentational component that renders the
 * browse response's additive `nextUnread` archive as a pinned "Continue" row.
 *  - non-null -> a row with cover, title, and a /reader/:id resume link
 *  - null     -> renders nothing (the folder has nothing to continue)
 */
describe('ContinueRowComponent', () => {
  function create(nextUnread: CatalogNodeDto | null) {
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [provideNoopAnimations(), provideRouter([])],
    });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.nextUnread.set(nextUnread);
    fixture.detectChanges();
    return fixture;
  }

  function row(fixture: ReturnType<typeof create>): HTMLElement | null {
    return fixture.nativeElement.querySelector('app-continue-row .continue-row');
  }

  it('renders nothing when nextUnread is null', () => {
    expect(row(create(null))).toBeNull();
  });

  it('renders the title and a resume link to /reader/:id when nextUnread is set', () => {
    const fixture = create(makeNode({ id: 'abc', displayName: 'Chapter 1.cbz' }));
    const el = row(fixture);
    expect(el).not.toBeNull();
    expect(el!.getAttribute('href')).toContain('/reader/abc');
    expect(el!.textContent).toContain('Continue');
    expect(el!.textContent).toContain('Chapter 1.cbz');
  });

  it('labels the row "Continue" when the target is in progress (1.9.0)', () => {
    const fixture = create(makeNode({ readingState: 'InProgress' }));
    const el = row(fixture)!;
    expect(el.textContent).toContain('Continue');
    expect(el.textContent).not.toContain('Start reading');
  });

  it('labels the row "Start reading" when the target is unread (1.9.0)', () => {
    const fixture = create(makeNode({ readingState: 'Unread', lastReadPage: null }));
    const el = row(fixture)!;
    expect(el.textContent).toContain('Start reading');
  });

  it('falls back to "Start reading" when the target has no reading state (1.9.0)', () => {
    const fixture = create(makeNode({ readingState: null, lastReadPage: null }));
    const el = row(fixture)!;
    expect(el.textContent).toContain('Start reading');
  });

  it('shows the cover image when coverUrl is present', () => {
    const fixture = create(makeNode({ coverUrl: '/api/v1/items/abc/cover' }));
    const img = fixture.nativeElement.querySelector('app-continue-row .cover img') as HTMLImageElement | null;
    expect(img).not.toBeNull();
    expect(img!.getAttribute('src')).toBe('/api/v1/items/abc/cover');
  });

  it('omits the cover image but keeps the fallback icon when coverUrl is null', () => {
    const fixture = create(makeNode({ coverUrl: null }));
    expect(fixture.nativeElement.querySelector('app-continue-row .cover img')).toBeNull();
    expect(fixture.nativeElement.querySelector('app-continue-row .cover-fallback')).not.toBeNull();
  });

  it('updates in place when the input changes (e.g. after marking the last item read)', () => {
    const fixture = create(makeNode());
    expect(row(fixture)).not.toBeNull();

    fixture.componentInstance.nextUnread.set(null);
    fixture.detectChanges();
    expect(row(fixture)).toBeNull();

    fixture.componentInstance.nextUnread.set(makeNode({ id: 'next', displayName: 'Chapter 2.cbz' }));
    fixture.detectChanges();
    const el = row(fixture);
    expect(el).not.toBeNull();
    expect(el!.getAttribute('href')).toContain('/reader/next');
    expect(el!.textContent).toContain('Chapter 2.cbz');
  });

  it('exposes a present signal that mirrors whether a row is shown', () => {
    const fixture = create(null);
    const cmp = fixture.debugElement.children[0].componentInstance as ContinueRowComponent;
    expect(cmp.present()).toBe(false);
    fixture.componentInstance.nextUnread.set(makeNode());
    fixture.detectChanges();
    expect(cmp.present()).toBe(true);
  });
});
