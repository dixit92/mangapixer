import { vi } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { MobileLibraryNavComponent } from './mobile-library-nav.component';
import { ApiService } from '../core/api/api.service';
import { LibraryDto } from '../core/api/api-types';

/**
 * Tests for the phone-only library nav page (1.10.0, F3): it should render a
 * page title plus the shared `LibrarySidebarComponent` in page mode - i.e. no
 * collapse toggle and the `page-mode` class applied, rather than the shell's
 * narrow/collapsible rendering.
 */
describe('MobileLibraryNavComponent', () => {
  function create() {
    const libs: LibraryDto[] = [
      { id: 'L1', name: 'Alpha', isScanning: false, itemCount: 3, lastScanCompleted: null, defaultReaderMode: null },
    ];
    const apiSpy = { getLibraries: vi.fn().mockReturnValue(of(libs)) };

    TestBed.configureTestingModule({
      imports: [MobileLibraryNavComponent],
      providers: [
        provideRouter([{ path: '', component: MobileLibraryNavComponent }]),
        { provide: ApiService, useValue: apiSpy },
      ],
    });

    const fixture = TestBed.createComponent(MobileLibraryNavComponent);
    fixture.detectChanges();
    return { fixture };
  }

  it('renders a page title', () => {
    const { fixture } = create();
    const title = fixture.nativeElement.querySelector('.page-title');
    expect(title?.textContent).toContain('Libraries');
  });

  it('renders the library sidebar in page mode (no collapse toggle, page-mode class)', () => {
    const { fixture } = create();
    const nav = fixture.nativeElement.querySelector('app-library-sidebar nav.sidebar');
    expect(nav).not.toBeNull();
    expect(nav!.classList.contains('page-mode')).toBe(true);
    expect(fixture.nativeElement.querySelector('.nav-collapse')).toBeNull();
  });

  it('renders a nav item per library plus Home, same as the shell sidebar', () => {
    const { fixture } = create();
    const items = fixture.nativeElement.querySelectorAll('.nav-item');
    expect(items).toHaveLength(2); // Home + Alpha
    expect(items[1].textContent).toContain('Alpha');
  });
});
