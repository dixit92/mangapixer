import {
  ApplicationConfig,
  provideZoneChangeDetection,
  provideAppInitializer,
  inject,
} from '@angular/core';
import { provideRouter, withInMemoryScrolling, RouteReuseStrategy } from '@angular/router';
import { provideAnimations } from '@angular/platform-browser/animations';
import { MAT_CARD_CONFIG } from '@angular/material/card';
import {
  provideHttpClient,
  withInterceptorsFromDi,
  withInterceptors,
} from '@angular/common/http';

import { routes } from './app.routes';
import { xsrfInterceptor } from './core/auth/xsrf.interceptor';
import { incognitoInterceptor } from './core/incognito/incognito.interceptor';
import { AuthService } from './core/auth/auth.service';
import { LibraryBrowseReuseStrategy } from './core/routing/library-browse-reuse.strategy';
import { ThemeService } from './core/theme/theme.service';
import { provideOverlayBack } from './shared/overlay-back/overlay-back.providers';

/**
 * App configuration. Provides router, animations, HTTP client with XSRF
 * interceptor, and initializes auth state on startup.
 *
 * Scroll restoration + `LibraryBrowseReuseStrategy` (1.6.2 hotfix) together
 * fix the reader-exit regression: restoration alone can't help when the
 * browse component gets destroyed and re-created empty by the reader
 * round-trip, so the strategy retains that instance across the round trip
 * and restoration then puts the retained view back at its prior scroll.
 */
export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(
      routes,
      withInMemoryScrolling({ scrollPositionRestoration: 'enabled', anchorScrolling: 'enabled' }),
    ),
    { provide: RouteReuseStrategy, useClass: LibraryBrowseReuseStrategy },
    // 1.52.0 (m1): a phone's back gesture / the browser's Back closes an open menu, sheet, dialog or list and stays on the page.
    provideOverlayBack(),
    provideAnimations(),
    // 1.53.0 (audit V9): every Material card is flat with a hairline, like the app's own cards (the two outlined-card tokens are in
    // styles.scss).
    { provide: MAT_CARD_CONFIG, useValue: { appearance: 'outlined' } },
    provideHttpClient(
      withInterceptorsFromDi(),
      withInterceptors([xsrfInterceptor, incognitoInterceptor]),
    ),
    provideAppInitializer(() => {
      const auth = inject(AuthService);
      return auth.initialize();
    }),
    provideAppInitializer(() => inject(ThemeService).start()),
  ],
};
