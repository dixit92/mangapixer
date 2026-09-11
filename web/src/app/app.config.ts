import {
  ApplicationConfig,
  provideZoneChangeDetection,
  provideAppInitializer,
  inject,
} from '@angular/core';
import { provideRouter, withInMemoryScrolling, RouteReuseStrategy } from '@angular/router';
import { provideAnimations } from '@angular/platform-browser/animations';
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
    provideAnimations(),
    provideHttpClient(
      withInterceptorsFromDi(),
      withInterceptors([xsrfInterceptor, incognitoInterceptor]),
    ),
    provideAppInitializer(() => {
      const auth = inject(AuthService);
      return auth.initialize();
    }),
  ],
};
