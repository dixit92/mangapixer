import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { routes } from './app.routes';

@Component({ standalone: true, template: 'home' })
class StubHomeComponent {}

/** 1.53.0 (audit V2): an unknown address goes Home instead of a blank page with a router error. */
describe('app routes', () => {
  it('ends with a catch-all that redirects to the app root', () => {
    expect(routes[routes.length - 1]).toEqual({ path: '**', redirectTo: '' });
    // Only the last entry is a catch-all: nothing after it would ever match.
    expect(routes.filter((r) => r.path === '**').length).toBe(1);
  });

  it('sends an unknown address to "/"', async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: '', component: StubHomeComponent }, routes[routes.length - 1]])],
    });
    const router = TestBed.inject(Router);
    expect(await router.navigateByUrl('/no-such-page')).toBe(true);
    expect(router.url).toBe('/');
    expect(await router.navigateByUrl('/libraries/x/browse/y/no/such/page')).toBe(true);
    expect(router.url).toBe('/');
  });
});
