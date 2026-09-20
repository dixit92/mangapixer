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
 * Tests for the app-shell LibrarySidebarComponent:
 *  - active item is derived from the CURRENT ROUTE (URL parse), not a local
 *    selection signal - so it is correct on deep links and back-button nav;
 *  - a library stays active while you browse a subfolder inside it;
 *  - each library icon carries a reading-direction badge;
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
          { path: 'favorites', component: BlankComponent },
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

  beforeEach(() => localStorage.removeItem('mangapixer-nav-collapsed'));

  it('renders Home plus a navigable item per library with browse-root links', async () => {
    const { fixture, router } = create();
    await navigate(router, '/', fixture);

    const items = fixture.nativeElement.querySelectorAll('.nav-item') as NodeListOf<HTMLAnchorElement>;
    // Home + Favorites (1.21.0) + 2 libraries
    expect(items).toHaveLength(4);
    expect(items[0].getAttribute('href')).toBe('/');
    expect(items[1].getAttribute('href')).toBe('/favorites');
    expect(items[2].getAttribute('href')).toBe('/libraries/L1/browse');
    expect(items[3].getAttribute('href')).toBe('/libraries/L2/browse');
  });

  it('renders the Favorites entry above the library list (1.21.0)', async () => {
    const { fixture, router } = create();
    await navigate(router, '/favorites', fixture);

    const items = fixture.nativeElement.querySelectorAll('.nav-item') as NodeListOf<HTMLAnchorElement>;
    expect(items[1].getAttribute('href')).toBe('/favorites');
    expect(items[1].classList.contains('active')).toBe(true);
    expect(fixture.componentInstance.isFavorites()).toBe(true);
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
    expect(items[2].classList.contains('active')).toBe(true); // L1 (index 2: Home, Favorites, L1)
    expect(items[0].classList.contains('active')).toBe(false); // Home

    // Deep inside a subfolder: the library stays highlighted (prefix match).
    await navigate(router, '/libraries/L1/browse/nodeXYZ', fixture);
    expect(fixture.componentInstance.activeLibraryId()).toBe('L1');
    items = fixture.nativeElement.querySelectorAll('.nav-item');
    expect(items[2].classList.contains('active')).toBe(true);
  });

  it('highlights nothing on the /libraries list route (no id in the URL)', async () => {
    const { fixture, router } = create();
    await navigate(router, '/libraries', fixture);
    expect(fixture.componentInstance.isHome()).toBe(false);
    expect(fixture.componentInstance.activeLibraryId()).toBeNull();
  });

  it('renders a reading-direction badge only for libraries with an explicit mode', async () => {
    const { fixture, router } = create();
    await navigate(router, '/', fixture);

    const items = fixture.nativeElement.querySelectorAll('.nav-item');
    // Indices: 0 Home, 1 Favorites, 2 L1, 3 L2. L1 (PagedRtl) has an RTL badge; L2 none.
    const l1Badge = items[2].querySelector('.nav-dir');
    expect(l1Badge).not.toBeNull();
    expect(l1Badge!.getAttribute('aria-label')).toBe('Reading direction: Right to left');
    expect(items[3].querySelector('.nav-dir')).toBeNull();
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
    // Indices: 0 Home, 1 Favorites, 2 the single library.
    const count = items[2].querySelector('.nav-count') as HTMLElement;
    expect(count.textContent?.trim()).toBe('8715');
  });

  it('toggles and persists the collapsed state under the shell-scoped key', async () => {
    const { fixture, router } = create();
    await navigate(router, '/', fixture);
    const comp = fixture.componentInstance;

    // 1.10.3: the shell sidebar now defaults to COLLAPSED when no preference is stored.
    expect(comp.collapsed()).toBe(true);
    comp.toggleCollapsed();
    expect(comp.collapsed()).toBe(false);
    expect(localStorage.getItem('mangapixer-nav-collapsed')).toBe('0');

    comp.toggleCollapsed();
    expect(localStorage.getItem('mangapixer-nav-collapsed')).toBe('1');
  });

  it('restores a persisted collapsed state on init', async () => {
    localStorage.setItem('mangapixer-nav-collapsed', '1');
    const { fixture, router } = create();
    await navigate(router, '/', fixture);
    expect(fixture.componentInstance.collapsed()).toBe(true);
  });

  describe('page mode (1.10.0, F3)', () => {
    it('defaults to shell mode: no page-mode class, collapse toggle present', async () => {
      const { fixture, router } = create();
      await navigate(router, '/', fixture);

      const nav = fixture.nativeElement.querySelector('nav.sidebar');
      expect(nav.classList.contains('page-mode')).toBe(false);
      expect(fixture.nativeElement.querySelector('.nav-collapse')).not.toBeNull();
    });

    it('applies the page-mode class and omits the collapse toggle when pageMode is set', async () => {
      const { fixture, router } = create();
      fixture.componentRef.setInput('pageMode', true);
      await navigate(router, '/', fixture);

      const nav = fixture.nativeElement.querySelector('nav.sidebar');
      expect(nav.classList.contains('page-mode')).toBe(true);
      expect(nav.classList.contains('collapsed')).toBe(false);
      expect(fixture.nativeElement.querySelector('.nav-head')).toBeNull();
      expect(fixture.nativeElement.querySelector('.nav-collapse')).toBeNull();
    });

    it('still renders Home plus a navigable item per library in page mode', async () => {
      const { fixture, router } = create();
      fixture.componentRef.setInput('pageMode', true);
      await navigate(router, '/libraries/L1/browse', fixture);

      const items = fixture.nativeElement.querySelectorAll('.nav-item');
      expect(items).toHaveLength(4); // Home + Favorites + L1 + L2
      expect(items[2].classList.contains('active')).toBe(true); // L1
    });

    it('never shows as collapsed in page mode even if the shell collapse flag is persisted', async () => {
      localStorage.setItem('mangapixer-nav-collapsed', '1');
      const { fixture, router } = create();
      fixture.componentRef.setInput('pageMode', true);
      await navigate(router, '/', fixture);

      expect(fixture.componentInstance.collapsed()).toBe(true); // underlying state is still loaded
      const nav = fixture.nativeElement.querySelector('nav.sidebar');
      expect(nav.classList.contains('collapsed')).toBe(false); // but never applied visually in page mode
    });
  });
});
