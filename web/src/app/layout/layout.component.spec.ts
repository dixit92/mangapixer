import { vi } from 'vitest';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { LayoutComponent } from './layout.component';
import { AuthService } from '../core/auth/auth.service';
import { IncognitoService } from '../core/incognito/incognito.service';
import { ApiService } from '../core/api/api.service';

/** Bare routed component so the outlet has something to activate. */
@Component({ standalone: true, template: 'routed' })
class BlankComponent {}

/**
 * Layout shell tests (1.5.0 Task A): the app-shell library sidebar is rendered on
 * content routes but EXCLUDED from the immersive reader (`/reader/:id`), which
 * instead gets a full-bleed content column. Auth routes live outside this
 * component entirely, so there is nothing to assert for them here.
 */
describe('LayoutComponent sidebar visibility', () => {
  function create() {
    const authSpy = {
      isAuthenticated: () => false,
      isAdmin: () => false,
      currentUser: () => null,
      logout: vi.fn().mockReturnValue(of(void 0)),
    };
    const incognitoSpy = { isIncognito: () => false, toggle: vi.fn() };
    const apiSpy = { getLibraries: vi.fn().mockReturnValue(of([])) };

    TestBed.configureTestingModule({
      imports: [LayoutComponent],
      providers: [
        provideNoopAnimations(),
        provideRouter([
          { path: '', component: BlankComponent },
          { path: 'libraries/:libraryId/browse', component: BlankComponent },
          { path: 'reader/:itemId', component: BlankComponent },
        ]),
        { provide: AuthService, useValue: authSpy },
        { provide: IncognitoService, useValue: incognitoSpy },
        { provide: ApiService, useValue: apiSpy },
      ],
    });
    const fixture = TestBed.createComponent(LayoutComponent);
    const router = TestBed.inject(Router);
    return { fixture, router };
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
});
