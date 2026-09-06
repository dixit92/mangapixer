import { TestBed } from '@angular/core/testing';
import { ThemeService } from './theme.service';

describe('ThemeService', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({});
  });

  it('defaults to dark theme', () => {
    const service = TestBed.inject(ThemeService);
    expect(service.preference).toBe('dark');
    expect(document.documentElement.style.colorScheme).toBe('dark');
  });

  it('persists theme preference to localStorage', () => {
    const service = TestBed.inject(ThemeService);
    service.setPreference('light');
    expect(service.preference).toBe('light');
    expect(localStorage.getItem('mangaplex-theme')).toBe('light');
    expect(document.documentElement.style.colorScheme).toBe('light');
  });

  it('restores saved preference on construction', () => {
    localStorage.setItem('mangaplex-theme', 'system');
    const service = TestBed.inject(ThemeService);
    expect(service.preference).toBe('system');
    expect(document.documentElement.style.colorScheme).toBe('light dark');
  });
});
