import { vi } from 'vitest';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { BreakpointObserver } from '@angular/cdk/layout';
import { BehaviorSubject, of } from 'rxjs';

import { LayoutComponent } from './layout.component';
import { AuthService } from '../core/auth/auth.service';
import { IncognitoService } from '../core/incognito/incognito.service';
import { ApiService } from '../core/api/api.service';

/** Bare routed component so the outlet has something to activate. */
@Component({ standalone: true, template: 'routed' })
class BlankComponent {}

/**
 * Fake `BreakpointObserver` (1.10.0, F3) so tests can deterministically drive
 * `LayoutComponent.isPhone` without depending on jsdom's `matchMedia` support.
 * `observe()` mirrors the real service's synchronous-on-subscribe emission via
 * a `BehaviorSubject`, which is what lets `toSignal` in the component pick up
 * the right value before first render.
 */
class FakeBreakpointObserver {
  readonly state$ = new BehaviorSubject({ matches: false, breakpoints: {} });
  observe() {
    return this.state$.asObservable();
  }
}

/**
 * Layout shell tests (1.5.0 Task A): the app-shell library sidebar is rendered on
 * content routes but EXCLUDED from the immersive reader (`/reader/:id`), which
 * instead gets a full-bleed content column. Auth routes live outside this
 * component entirely, so there is nothing to assert for them here.
 *
 * 1.10.0 (F3) adds phone-breakpoint coverage: on phone widths the shell stops
 * mounting the sidebar at all (even on content routes) and instead shows a
 * toolbar nav control routing to the dedicated mobile library-nav page.
 * Desktop/tablet (the default, non-phone fake state) must be unaffected -
 * the pre-existing tests below assert exactly that with no changes needed.
 */
describe('LayoutComponent sidebar visibility', () => {
  function create(options: { authenticated?: boolean; phone?: boolean } = {}) {
    const { authenticated = false, phone = false } = options;
    const authSpy = {
      isAuthenticated: () => authenticated,
      isAdmin: () => false,
      currentUser: () => null,
      logout: vi.fn().mockReturnValue(of(void 0)),
    };
    const incognitoSpy = { isIncognito: () => false, toggle: vi.fn() };
    const apiSpy = { getLibraries: vi.fn().mockReturnValue(of([])) };
    const breakpointObserver = new FakeBreakpointObserver();
    breakpointObserver.state$.next({ matches: phone, breakpoints: {} });

    TestBed.configureTestingModule({
      imports: [LayoutComponent],
      providers: [
        provideNoopAnimations(),
        provideRouter([
          { path: '', component: BlankComponent },
          { path: 'libraries/:libraryId/browse', component: BlankComponent },
          { path: 'reader/:itemId', component: BlankComponent },
          { path: 'library-nav', component: BlankComponent },
        ]),
        { provide: AuthService, useValue: authSpy },
        { provide: IncognitoService, useValue: incognitoSpy },
        { provide: ApiService, useValue: apiSpy },
        { provide: BreakpointObserver, useValue: breakpointObserver },
      ],
    });
    const fixture = TestBed.createComponent(LayoutComponent);
    const router = TestBed.inject(Router);
    return { fixture, router, breakpointObserver };
  }

  async function go(router: Router, url: string, fixture: ReturnType<typeof create>['fixture']) {
    await router.navigateByUrl(url);
    fixture.detectChanges();
  }

  it('shows the sidebar and a gutter-padded content column on the home route', async () => {
    const { fixture, router } = create();
    await go(router, '/', fixture);

    expect(fixture.componentInstance.showSidebar()).toBe(true);
    expect(fixture.nativeElement.querySelector('app-library-sidebar')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('.content.full-bleed')).toBeNull();
  });

  it('shows the sidebar while browsing a library', async () => {
    const { fixture, router } = create();
    await go(router, '/libraries/L1/browse', fixture);

    expect(fixture.componentInstance.showSidebar()).toBe(true);
    expect(fixture.nativeElement.querySelector('app-library-sidebar')).not.toBeNull();
  });

  it('hides the sidebar and full-bleeds the content in the reader', async () => {
    const { fixture, router } = create();
    await go(router, '/reader/item42', fixture);

    expect(fixture.componentInstance.showSidebar()).toBe(false);
    expect(fixture.nativeElement.querySelector('app-library-sidebar')).toBeNull();
    expect(fixture.nativeElement.querySelector('.content.full-bleed')).not.toBeNull();
  });

  describe('phone breakpoint (1.10.0, F3)', () => {
    it('desktop/tablet (non-phone) is unchanged: shell sidebar shown, no mobile nav control', async () => {
      const { fixture, router } = create({ authenticated: true, phone: false });
      await go(router, '/', fixture);

      expect(fixture.componentInstance.isPhone()).toBe(false);
      expect(fixture.componentInstance.showShellSidebar()).toBe(true);
      expect(fixture.nativeElement.querySelector('app-library-sidebar')).not.toBeNull();
      expect(fixture.nativeElement.querySelector('.mobile-nav-btn')).toBeNull();
    });

    it('on phone, hides the shell sidebar on a content route and shows the nav control instead', async () => {
      const { fixture, router } = create({ authenticated: true, phone: true });
      await go(router, '/', fixture);

      expect(fixture.componentInstance.isPhone()).toBe(true);
      expect(fixture.componentInstance.showSidebar()).toBe(true); // not the reader
      expect(fixture.componentInstance.showShellSidebar()).toBe(false);
      expect(fixture.nativeElement.querySelector('app-library-sidebar')).toBeNull();
      // Content keeps its normal gutter padding - only the reader is full-bleed.
      expect(fixture.nativeElement.querySelector('.content.full-bleed')).toBeNull();

      const navBtn: HTMLButtonElement = fixture.nativeElement.querySelector('.mobile-nav-btn');
      expect(navBtn).not.toBeNull();
      expect(navBtn.getAttribute('aria-label')).toBe('Open library navigation');

      navBtn.click();
      await fixture.whenStable();
      expect(router.url).toBe('/library-nav');
    });

    it('on phone, the nav control TOGGLES: a second tap closes and returns to origin', async () => {
      const { fixture, router } = create({ authenticated: true, phone: true });
      await go(router, '/', fixture);
      const navBtn = () => fixture.nativeElement.querySelector('.mobile-nav-btn') as HTMLButtonElement;

      // First tap opens the dedicated nav page; the control flips to a close affordance.
      navBtn().click();
      await fixture.whenStable();
      fixture.detectChanges();
      expect(router.url).toBe('/library-nav');
      expect(navBtn().getAttribute('aria-label')).toBe('Close library navigation');

      // Second tap closes, returning to wherever it was opened from.
      navBtn().click();
      await fixture.whenStable();
      fixture.detectChanges();
      expect(router.url).toBe('/');
      expect(navBtn().getAttribute('aria-label')).toBe('Open library navigation');
    });

    it('does not show the mobile nav control when unauthenticated, even on phone', async () => {
      const { fixture, router } = create({ authenticated: false, phone: true });
      await go(router, '/', fixture);

      expect(fixture.nativeElement.querySelector('.mobile-nav-btn')).toBeNull();
    });

    it('does not show the mobile nav control in the reader, even on phone', async () => {
      const { fixture, router } = create({ authenticated: true, phone: true });
      await go(router, '/reader/item42', fixture);

      expect(fixture.componentInstance.showSidebar()).toBe(false);
      expect(fixture.nativeElement.querySelector('.mobile-nav-btn')).toBeNull();
      expect(fixture.nativeElement.querySelector('app-library-sidebar')).toBeNull();
    });

    it('reacts live to a breakpoint change (e.g. rotation) without a route change', async () => {
      const { fixture, router, breakpointObserver } = create({ authenticated: true, phone: false });
      await go(router, '/', fixture);
      expect(fixture.nativeElement.querySelector('app-library-sidebar')).not.toBeNull();

      breakpointObserver.state$.next({ matches: true, breakpoints: {} });
      fixture.detectChanges();

      expect(fixture.componentInstance.isPhone()).toBe(true);
      expect(fixture.nativeElement.querySelector('app-library-sidebar')).toBeNull();
      expect(fixture.nativeElement.querySelector('.mobile-nav-btn')).not.toBeNull();
    });
  });
});
