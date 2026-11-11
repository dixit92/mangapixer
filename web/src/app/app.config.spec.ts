import { MAT_CARD_CONFIG } from '@angular/material/card';

import { appConfig } from './app.config';

describe('appConfig', () => {
  it('makes every Material card outlined - flat with a hairline, like the app own cards (1.53.0 audit V9)', () => {
    const provider = appConfig.providers.find(
      (p): p is { provide: unknown; useValue: unknown } => !!p && typeof p === 'object' && 'provide' in p && p.provide === MAT_CARD_CONFIG,
    );
    expect(provider?.useValue).toEqual({ appearance: 'outlined' });
  });
});
