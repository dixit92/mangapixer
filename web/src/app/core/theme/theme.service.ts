import { Injectable } from '@angular/core';

export type ThemePreference = 'dark' | 'light' | 'system';

@Injectable({ providedIn: 'root' })
export class ThemeService {
  private static readonly STORAGE_KEY = 'mangaplex-theme';
  private current: ThemePreference = 'dark';

  constructor() {
    const stored = localStorage.getItem(ThemeService.STORAGE_KEY) as ThemePreference | null;
    if (stored === 'dark' || stored === 'light' || stored === 'system') {
      this.current = stored;
    }
    this.apply();
  }

  get preference(): ThemePreference {
    return this.current;
  }

  setPreference(pref: ThemePreference): void {
    this.current = pref;
    localStorage.setItem(ThemeService.STORAGE_KEY, pref);
    this.apply();
  }

  private apply(): void {
    const doc = document.documentElement;
    switch (this.current) {
      case 'dark':
        doc.style.colorScheme = 'dark';
        break;
      case 'light':
        doc.style.colorScheme = 'light';
        break;
      case 'system':
        doc.style.colorScheme = 'light dark';
        break;
    }
  }
}
