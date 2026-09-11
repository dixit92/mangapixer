import { vi } from 'vitest';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { LibrarySidebarComponent } from './library-sidebar.component';
import { ApiService } from '../core/api/api.service';
import { LibraryDto } from '../core/api/api-types';

/** Bare routed component so the real Router can resolve navigations in the test. */
@Component({ standalone: true, template: '' })
class BlankComponent {}

/**
 * Tests for the app-shell LibrarySidebarComponent (1.5.0 Task A + C):
 *  - active item is derived from the CURRENT ROUTE (URL parse), not a local
 *    selection signal - so it is correct on deep links and back-button nav;
 *  - a library stays active while you browse a subfolder inside it;
 *  - each library icon carries a reading-direction badge (Task C);
 *  - desktop collapse persists to a shell-scoped localStorage key.
 */
describe('LibrarySidebarComponent', () => {
  const libs: LibraryDto[] = [
    { id: 'L1', name: 'Alpha', isScanning: false, itemCount: 3, lastScanCompleted: null, defaultReaderMode: 'PagedRtl' },
    { id: 'L2', name: 'Beta', isScanning: false, itemCount: 5, lastScanCompleted: null, defaultReaderMode: null },
  ];

  function create() {
    const apiSpy = { getLibraries: vi.fn().mockReturnValue(of(libs)) };
    TestBed.configureTestingModule({
      imports: [LibrarySidebarComponent],
      providers: [
        provideNoopAnimations(),
        provideRouter([
          { path: '', component: BlankComponent },
          { path: 'libraries', component: BlankComponent },
          { path: 'libraries/:libraryId/browse', component: BlankComponent },
          { path: 'libraries/:libraryId/browse/:nodeId', component: BlankComponent },
        ]),
        { provide: ApiService, useValue: apiSpy },
      ],
    });
    const fixture = TestBed.createComponent(LibrarySidebarComponent);
    const router = TestBed.inject(Router);
    return { fixture, router };
  }

  async function navigate(router: Router, url: string, fixture: ReturnType<typeof create>['fixture']) {
    await router.navigateByUrl(url);
    fixture.detectChanges();
  }

  beforeEach(() => localStorage.removeItem('mangaplex-nav-collapsed'));

  it('renders Home plus a navigable item per library with browse-root links', async () => {
    const { fixture, router } = create();
    await navigate(router, '/', fixture);

    const items = fixture.nativeElement.querySelectorAll('.nav-item') as NodeListOf<HTMLAnchorElement>;
    // Home + 2 libraries
    expect(items).toHaveLength(3);
    expect(items[0].getAttribute('href')).toBe('/');
    expect(items[1].getAttribute('href')).toBe('/libraries/L1/browse');
    expect(items[2].getAttribute('href')).toBe('/libraries/L2/browse');
  });

  it('marks Home active at the root and no library', async () => {
    const { fixture, router } = create();
    await navigate(router, '/', fixture);

    const comp = fixture.componentInstance;
    expect(comp.isHome()).toBe(true);
    expect(comp.activeLibraryId()).toBeNull();

    const items = fixture.nativeElement.querySelectorAll('.nav-item');
    expect(items[0].classList.contains('active')).toBe(true);
    expect(items[1].classList.contains('active')).toBe(false);
  });

  it('marks the library active while browsing it, including deep in a subfolder', async () => {
    const { fixture, router } = create();

    await navigate(router, '/libraries/L1/browse', fixture);
    expect(fixture.componentInstance.isHome()).toBe(false);
    expect(fixture.componentInstance.activeLibraryId()).toBe('L1');
    let items = fixture.nativeElement.querySelectorAll('.nav-item');
    expect(items[1].classList.contains('active')).toBe(true); // L1
    expect(items[0].classList.contains('active')).toBe(false); // Home

    // Deep inside a subfolder: the library stays highlighted (prefix match).
    await navigate(router, '/libraries/L1/browse/nodeXYZ', fixture);
    expect(fixture.componentInstance.activeLibraryId()).toBe('L1');
    items = fixture.nativeElement.querySelectorAll('.nav-item');
    expect(items[1].classList.contains('active')).toBe(true);
  });

  it('highlights nothing on the /libraries list route (no id in the URL)', async () => {
    const { fixture, router } = create();
    await navigate(router, '/libraries', fixture);
    expect(fixture.componentInstance.isHome()).toBe(false);
    expect(fixture.componentInstance.activeLibraryId()).toBeNull();
  });

  it('renders a reading-direction badge only for libraries with an explicit mode (Task C)', async () => {
    const { fixture, router } = create();
    await navigate(router, '/', fixture);

    const items = fixture.nativeElement.querySelectorAll('.nav-item');
    // L1 (PagedRtl) has a badge with the RTL aria-label; L2 (null) has none.
    const l1Badge = items[1].querySelector('.nav-dir');
    expect(l1Badge).not.toBeNull();
    expect(l1Badge!.getAttribute('aria-label')).toBe('Reading direction: Right to left');
    expect(items[2].querySelector('.nav-dir')).toBeNull();
  });

  it('renders large item counts without altering the count column markup (overflow fix)', async () => {
    const bigLibs: LibraryDto[] = [
      { id: 'L1', name: 'A Library With A Fairly Long Name', isScanning: false, itemCount: 8715, lastScanCompleted: null, defaultReaderMode: null },
    ];
    const apiSpy = { getLibraries: vi.fn().mockReturnValue(of(bigLibs)) };
    TestBed.configureTestingModule({
      imports: [LibrarySidebarComponent],
      providers: [
        provideNoopAnimations(),
        provideRouter([
          { path: '', component: BlankComponent },
          { path: 'libraries/:libraryId/browse', component: BlankComponent },
        ]),
        { provide: ApiService, useValue: apiSpy },
      ],
    });
    const fixture = TestBed.createComponent(LibrarySidebarComponent);
    const router = TestBed.inject(Router);
    await navigate(router, '/', fixture);

    const items = fixture.nativeElement.querySelectorAll('.nav-item');
    const count = items[1].querySelector('.nav-count') as HTMLElement;
    expect(count.textContent?.trim()).toBe('8715');
  });

  it('toggles and persists the collapsed state under the shell-scoped key', async () => {
    const { fixture, router } = create();
    await navigate(router, '/', fixture);
    const comp = fixture.componentInstance;

    expect(comp.collapsed()).toBe(false);
    comp.toggleCollapsed();
    expect(comp.collapsed()).toBe(true);
    expect(localStorage.getItem('mangaplex-nav-collapsed')).toBe('1');

    comp.toggleCollapsed();
    expect(localStorage.getItem('mangaplex-nav-collapsed')).toBe('0');
  });

  it('restores a persisted collapsed state on init', async () => {
    localStorage.setItem('mangaplex-nav-collapsed', '1');
    const { fixture, router } = create();
    await navigate(router, '/', fixture);
    expect(fixture.componentInstance.collapsed()).toBe(true);
  });
});
