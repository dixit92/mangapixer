import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';

import { AppearanceCardComponent } from './appearance-card.component';
import { ThemeService } from '../../core/theme/theme.service';
import { THEME_ACCENTS, THEME_BASES, ThemeAccent, ThemeBase } from '../../core/theme/theme-vocabulary';

/** Appearance card (1.40.0): five base choices with previews, six accents, applied through ThemeService. */
describe('AppearanceCardComponent', () => {
  /** A distinct (valid) surface colour per sampled base, so a preview can be traced back to its base. */
  const SURFACE: Record<string, number> = { dark: 10, light: 20, black: 30, sepia: 40 };
  function create(start: { base?: ThemeBase; accent?: ThemeAccent; error?: string | null } = {}) {
    const base = signal<ThemeBase>(start.base ?? 'dark');
    const accent = signal<ThemeAccent>(start.accent ?? 'violet');
    const theme = {
      base,
      accent,
      resolved: () => (base() === 'system' ? 'dark' : base()),
      saveError: signal(start.error ?? null),
      setBase: vi.fn((b: ThemeBase) => base.set(b)),
      setAccent: vi.fn((a: ThemeAccent) => accent.set(a)),
      samplePalettes: vi.fn((reqs: { base: string; accent: string }[]) =>
        reqs.map((r) => ({
          surface: `rgb(${SURFACE[r.base]}, 0, 0)`,
          text: 'rgb(0, 0, 1)',
          accent: 'rgb(0, 0, 2)',
          accentStrong: `rgb(1, 2, ${THEME_ACCENTS.indexOf(r.accent as ThemeAccent)})`,
        })),
      ),
    };
    TestBed.configureTestingModule({
      imports: [AppearanceCardComponent],
      providers: [{ provide: ThemeService, useValue: theme }],
    });
    const fixture = TestBed.createComponent(AppearanceCardComponent);
    fixture.detectChanges();
    return { fixture, theme, el: fixture.nativeElement as HTMLElement };
  }

  it('lists every base and every accent of the vocabulary, as radio groups', () => {
    const { el } = create();
    const bases = [...el.querySelectorAll<HTMLButtonElement>('.base')].map((b) => b.dataset['base']);
    const accents = [...el.querySelectorAll<HTMLButtonElement>('.accent')].map((b) => b.dataset['accentChoice']);
    expect(bases).toEqual([...THEME_BASES]);
    expect(accents).toEqual([...THEME_ACCENTS]);
    expect(el.querySelectorAll('[role="radiogroup"]').length).toBe(2);
    expect(el.querySelector('.base[data-base="dark"]')!.getAttribute('aria-checked')).toBe('true');
    expect(el.querySelector('.accent[data-accent-choice="violet"]')!.getAttribute('aria-checked')).toBe('true');
  });

  it('previews each base with its sampled tokens; System shows light and dark halves', () => {
    const { el } = create();
    const halves = (b: string) =>
      [...el.querySelectorAll<HTMLElement>(`.base[data-base="${b}"] .half`)].map((h) => h.style.backgroundColor);
    expect(halves('sepia')).toEqual(['rgb(40, 0, 0)']);
    expect(halves('dark')).toEqual(['rgb(10, 0, 0)']);
    expect(halves('system')).toEqual(['rgb(20, 0, 0)', 'rgb(10, 0, 0)']); // light | dark
  });

  it('paints the accent swatches from the tokens of the current base', () => {
    const { el, theme } = create({ base: 'light' });
    const lastCall = theme.samplePalettes.mock.calls.at(-1)![0] as { base: string; accent: string }[];
    expect(lastCall.every((r) => r.base === 'light')).toBe(true);
    expect(el.querySelector<HTMLElement>('.accent[data-accent-choice="teal"] .fill')!.style.backgroundColor).toBe('rgb(1, 2, 2)');
  });

  it('a click chooses the base / accent through the theme service', () => {
    const { el, theme, fixture } = create();
    el.querySelector<HTMLButtonElement>('.base[data-base="light"]')!.click();
    el.querySelector<HTMLButtonElement>('.accent[data-accent-choice="rose"]')!.click();
    fixture.detectChanges();
    expect(theme.setBase).toHaveBeenCalledWith('light');
    expect(theme.setAccent).toHaveBeenCalledWith('rose');
    expect(el.querySelector('.base[data-base="light"]')!.getAttribute('aria-checked')).toBe('true');
    expect(el.querySelector('.accent[data-accent-choice="rose"] mat-icon')).not.toBeNull();
  });

  it('shows a save error', () => {
    const { el } = create({ error: 'offline' });
    expect(el.querySelector('.error')!.textContent).toContain('offline');
  });
});
