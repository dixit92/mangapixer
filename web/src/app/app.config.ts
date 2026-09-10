import {
  ApplicationConfig,
  provideZoneChangeDetection,
  provideAppInitializer,
  inject,
} from '@angular/core';
import { provideRouter } from '@angular/router';
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

/**
 * App configuration. Provides router, animations, HTTP client with XSRF
 * interceptor, and initializes auth state on startup.
 */
export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes),
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
