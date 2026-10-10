import { TestBed } from '@angular/core/testing';
import { WritableSignal, signal } from '@angular/core';
import { Observable, Subject, of, throwError } from 'rxjs';

import { ApiService } from '../api/api.service';
import { AuthService } from '../auth/auth.service';
import { AppearancePreferencesDto, AuthUserDto, UserPreferencesDto } from '../api/api-types';
import { ACCENT_STORAGE_KEY, THEME_STORAGE_KEY, ThemeService, isOpaque } from './theme.service';

/** A controllable `prefers-color-scheme: light` media query. */
class FakeMediaQuery {
  matches: boolean;
  private listeners: ((e: MediaQueryListEvent) => void)[] = [];
  constructor(matches: boolean) {
    this.matches = matches;
  }
  addEventListener(_: string, l: (e: MediaQueryListEvent) => void): void {
    this.listeners.push(l);
  }
  removeEventListener(_: string, l: (e: MediaQueryListEvent) => void): void {
    this.listeners = this.listeners.filter((x) => x !== l);
  }
  change(matches: boolean): void {
    this.matches = matches;
    for (const l of this.listeners) l({ matches } as MediaQueryListEvent);
  }
}

describe('ThemeService', () => {
  const root = document.documentElement;
  const originalMatchMedia = globalThis.matchMedia;
  let media: FakeMediaQuery;
  let currentUser: WritableSignal<AuthUserDto | null>;
  let api: {
    getPreferences: ReturnType<typeof vi.fn>;
    setAppearance: ReturnType<typeof vi.fn>;
  };
  let meta: HTMLMetaElement;

  const user = (id: string): AuthUserDto => ({ id, username: id, role: 'User', isAdmin: false });
  const prefs = (theme?: string, accent?: string): UserPreferencesDto =>
    ({ defaultReaderMode: 'PagedLtr', preferDoubleSpread: false, reducedMotion: false, preferredBackground: null, theme, accent });

  function create(): ThemeService {
    TestBed.configureTestingModule({
      providers: [
        { provide: ApiService, useValue: api },
        {
          provide: AuthService,
          useValue: { currentUser, isAuthenticated: () => currentUser() !== null },
        },
      ],
    });
    const service = TestBed.inject(ThemeService);
    TestBed.tick();
    return service;
  }

  beforeEach(() => {
    localStorage.clear();
    root.removeAttribute('data-theme');
    root.removeAttribute('data-accent');
    media = new FakeMediaQuery(false);
    globalThis.matchMedia = ((q: string) => {
      expect(q).toBe('(prefers-color-scheme: light)');
      return media;
    }) as unknown as typeof matchMedia;
    currentUser = signal<AuthUserDto | null>(null);
    api = {
      getPreferences: vi.fn(() => of(prefs())),
      setAppearance: vi.fn((_: AppearancePreferencesDto): Observable<void> => of(undefined)),
    };
    meta = document.createElement('meta');
    meta.name = 'theme-color';
    meta.content = '#333333';
    document.head.appendChild(meta);
  });

  afterEach(() => {
    globalThis.matchMedia = originalMatchMedia;
    meta.remove();
    document.body.style.backgroundColor = '';
  });

  it('defaults to dark / violet and paints them on <html>', () => {
    const service = create();
    expect(service.base()).toBe('dark');
    expect(service.accent()).toBe('violet');
    expect(root.getAttribute('data-theme')).toBe('dark');
    expect(root.getAttribute('data-accent')).toBe('violet');
    expect(root.style.colorScheme).toBe(''); // color-scheme comes from styles.scss, keyed on data-theme
  });

  it('restores the device copy on construction', () => {
    localStorage.setItem(THEME_STORAGE_KEY, 'sepia');
    localStorage.setItem(ACCENT_STORAGE_KEY, 'teal');
    const service = create();
    expect(service.base()).toBe('sepia');
    expect(root.getAttribute('data-theme')).toBe('sepia');
    expect(root.getAttribute('data-accent')).toBe('teal');
  });

  it('falls back to the defaults for bad stored values', () => {
    localStorage.setItem(THEME_STORAGE_KEY, 'neon');
    localStorage.setItem(ACCENT_STORAGE_KEY, 'Violet');
    const service = create();
    expect(service.base()).toBe('dark');
    expect(service.accent()).toBe('violet');
    expect(root.getAttribute('data-theme')).toBe('dark');
  });

  it('persists a choice to localStorage and paints it at once (signed out: no server call)', () => {
    const service = create();
    service.setBase('light');
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('light');
    expect(root.getAttribute('data-theme')).toBe('light');
    service.setBase('black');
    expect(root.getAttribute('data-theme')).toBe('black');
    expect(api.setAppearance).not.toHaveBeenCalled();
  });

  it('ignores a value outside the vocabulary', () => {
    const service = create();
    service.setBase('neon' as never);
    service.setAccent('purple' as never);
    expect(service.base()).toBe('dark');
    expect(service.accent()).toBe('violet');
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBeNull();
  });

  it('sets data-accent for an accent', () => {
    const service = create();
    service.setAccent('amber');
    expect(root.getAttribute('data-accent')).toBe('amber');
    expect(localStorage.getItem(ACCENT_STORAGE_KEY)).toBe('amber');
    expect(root.getAttribute('data-theme')).toBe('dark'); // the base is untouched
  });

  it('system follows prefers-color-scheme, live', () => {
    media = new FakeMediaQuery(true);
    const service = create();
    service.setBase('system');
    expect(service.base()).toBe('system');
    expect(service.resolved()).toBe('light');
    expect(root.getAttribute('data-theme')).toBe('light');

    media.change(false);
    expect(root.getAttribute('data-theme')).toBe('dark');
    media.change(true);
    expect(root.getAttribute('data-theme')).toBe('light');
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('system'); // the CHOICE is stored, not the resolved base
  });

  it('a device change does not repaint a fixed base', () => {
    const service = create();
    service.setBase('sepia');
    media.change(true);
    media.change(false);
    expect(root.getAttribute('data-theme')).toBe('sepia');
  });

  it('loads the server copy once a user is signed in, and it wins over the device copy', () => {
    localStorage.setItem(THEME_STORAGE_KEY, 'light');
    api.getPreferences.mockReturnValue(of(prefs('black', 'rose')));
    const service = create();
    expect(root.getAttribute('data-theme')).toBe('light'); // device copy first
    expect(api.getPreferences).not.toHaveBeenCalled();

    currentUser.set(user('u1'));
    TestBed.tick();

    expect(api.getPreferences).toHaveBeenCalledTimes(1);
    expect(service.base()).toBe('black');
    expect(root.getAttribute('data-theme')).toBe('black');
    expect(root.getAttribute('data-accent')).toBe('rose');
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('black'); // the device copy follows the server
  });

  it('a user who never chose gets the defaults from the server', () => {
    localStorage.setItem(THEME_STORAGE_KEY, 'light'); // the previous user's look on this device
    api.getPreferences.mockReturnValue(of(prefs('dark', 'violet')));
    create();
    currentUser.set(user('u2'));
    TestBed.tick();
    expect(root.getAttribute('data-theme')).toBe('dark');
  });

  it('bad server values fall back to the defaults', () => {
    api.getPreferences.mockReturnValue(of(prefs('neon', undefined)));
    const service = create();
    currentUser.set(user('u1'));
    TestBed.tick();
    expect(service.base()).toBe('dark');
    expect(service.accent()).toBe('violet');
  });

  it('saves a choice to the server for the signed-in user (only the changed field)', () => {
    currentUser.set(user('u1'));
    const service = create();
    service.setBase('light');
    expect(api.setAppearance).toHaveBeenCalledWith({ theme: 'light' });
    service.setAccent('blue');
    expect(api.setAppearance).toHaveBeenLastCalledWith({ accent: 'blue' });
    expect(service.saving()).toBe(false);
    expect(service.saveError()).toBeNull();
  });

  it('keeps a failed save applied on this device and reports it', () => {
    currentUser.set(user('u1'));
    api.setAppearance.mockReturnValue(throwError(() => ({ message: 'offline' })));
    const service = create();
    service.setBase('light');
    expect(root.getAttribute('data-theme')).toBe('light');
    expect(service.saveError()).toBe('offline');
  });

  it('a server answer requested before a local choice does not undo it', () => {
    const answer = new Subject<UserPreferencesDto>();
    api.getPreferences.mockReturnValue(answer);
    const service = create();
    currentUser.set(user('u1'));
    TestBed.tick();

    service.setBase('sepia');
    answer.next(prefs('dark', 'violet'));
    answer.complete();

    expect(service.base()).toBe('sepia');
    expect(root.getAttribute('data-theme')).toBe('sepia');
  });

  it('sign-out keeps the device copy; the next user loads their own', () => {
    api.getPreferences.mockReturnValue(of(prefs('light', 'teal')));
    create();
    currentUser.set(user('u1'));
    TestBed.tick();
    expect(root.getAttribute('data-theme')).toBe('light');

    currentUser.set(null);
    TestBed.tick();
    expect(root.getAttribute('data-theme')).toBe('light');
    expect(localStorage.getItem(THEME_STORAGE_KEY)).toBe('light');

    api.getPreferences.mockReturnValue(of(prefs('dark', 'violet')));
    currentUser.set(user('u2'));
    TestBed.tick();
    expect(api.getPreferences).toHaveBeenCalledTimes(2);
    expect(root.getAttribute('data-theme')).toBe('dark');
  });

  it('updates <meta name="theme-color"> from the painted background', () => {
    document.body.style.backgroundColor = 'rgb(1, 2, 3)';
    const service = create();
    expect(meta.content).toBe('rgb(1, 2, 3)');
    document.body.style.backgroundColor = 'rgb(250, 250, 250)';
    service.setBase('light');
    expect(meta.content).toBe('rgb(250, 250, 250)');
  });

  it('samplePalettes reads one sample per request and restores <html> as it was', () => {
    localStorage.setItem(THEME_STORAGE_KEY, 'sepia');
    localStorage.setItem(ACCENT_STORAGE_KEY, 'teal');
    const service = create();
    const samples = service.samplePalettes([
      { base: 'light', accent: 'rose' },
      { base: 'black', accent: 'blue' },
    ]);
    expect(samples.length).toBe(2);
    expect(Object.keys(samples[0]).sort()).toEqual(['accent', 'accentStrong', 'surface', 'text']);
    expect(root.getAttribute('data-theme')).toBe('sepia');
    expect(root.getAttribute('data-accent')).toBe('teal');
  });

  it('isOpaque tells a painted colour from a transparent one (black is opaque)', () => {
    expect(isOpaque('rgb(0, 0, 0)')).toBe(true);
    expect(isOpaque('rgb(0 0 0)')).toBe(true);
    expect(isOpaque('#000')).toBe(true);
    expect(isOpaque('rgba(0, 0, 0, 0)')).toBe(false);
    expect(isOpaque('rgb(0 0 0 / 0%)')).toBe(false);
    expect(isOpaque('transparent')).toBe(false);
    expect(isOpaque('')).toBe(false);
  });
});
