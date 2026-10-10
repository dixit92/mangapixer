import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';

import { ThemeService } from '../../core/theme/theme.service';
import {
  ResolvedThemeBase,
  THEME_ACCENTS,
  THEME_BASES,
  ThemeAccent,
  ThemeBase,
} from '../../core/theme/theme-vocabulary';

const BASE_LABELS: Record<ThemeBase, string> = {
  dark: 'Dark',
  light: 'Light',
  black: 'Black',
  sepia: 'Sepia',
  system: 'System',
};

const BASE_HINTS: Record<ThemeBase, string> = {
  dark: 'The default',
  light: 'Bright surfaces',
  black: 'True black, for OLED screens',
  sepia: 'Warm paper',
  system: 'Follows this device: light or dark',
};

const ACCENT_LABELS: Record<ThemeAccent, string> = {
  violet: 'Violet',
  blue: 'Blue',
  teal: 'Teal',
  green: 'Green',
  amber: 'Amber',
  rose: 'Rose',
};

interface Swatch {
  surface: string;
  text: string;
  accent: string;
}

/**
 * Appearance card (1.40.0) on the Settings page: the base theme (Dark / Light / Black / Sepia / System) and the accent.
 * A choice applies at once and is saved for the signed-in user ({@link ThemeService}). The previews show each theme's
 * CURRENT token values (sampled from the stylesheet), so the values in `src/themes/` appear here as they are.
 */
@Component({
  selector: 'app-appearance-card',
  standalone: true,
  imports: [MatCardModule, MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <mat-card class="appearance-card">
      <mat-card-header>
        <mat-card-title>Appearance</mat-card-title>
      </mat-card-header>
      <mat-card-content>
        <h4 id="appearance-theme-label">Theme</h4>
        <div class="bases" role="radiogroup" aria-labelledby="appearance-theme-label">
          @for (b of bases(); track b.id) {
            <button type="button" class="base" role="radio" [attr.data-base]="b.id"
                    [class.selected]="theme.base() === b.id" [attr.aria-checked]="theme.base() === b.id"
                    [attr.aria-label]="b.label + ' - ' + b.hint" (click)="theme.setBase(b.id)">
              <span class="preview" aria-hidden="true">
                @for (s of b.swatches; track $index) {
                  <span class="half" [style.background]="s.surface">
                    <span class="line" [style.background]="s.text"></span>
                    <span class="line short" [style.background]="s.text"></span>
                    <span class="dot" [style.background]="s.accent"></span>
                  </span>
                }
              </span>
              <span class="label">{{ b.label }}</span>
            </button>
          }
        </div>
        <p class="hint">{{ hintFor(theme.base()) }}</p>

        <h4 id="appearance-accent-label">Accent</h4>
        <div class="accents" role="radiogroup" aria-labelledby="appearance-accent-label">
          @for (a of accents(); track a.id) {
            <button type="button" class="accent" role="radio" [attr.data-accent-choice]="a.id"
                    [class.selected]="theme.accent() === a.id" [attr.aria-checked]="theme.accent() === a.id"
                    [attr.aria-label]="a.label" [title]="a.label" (click)="theme.setAccent(a.id)">
              <span class="fill" [style.background]="a.color" aria-hidden="true">
                @if (theme.accent() === a.id) { <mat-icon>check</mat-icon> }
              </span>
            </button>
          }
        </div>

        @if (theme.saveError()) {
          <div class="error" role="alert">{{ theme.saveError() }}</div>
        }
      </mat-card-content>
    </mat-card>
  `,
  styles: [`
    mat-card { max-width: 600px; margin: 24px auto 0; }
    h4 { margin: 4px 0 10px; font-size: 14px; font-weight: 500; }
    .bases { display: grid; grid-template-columns: repeat(auto-fill, minmax(88px, 1fr)); gap: 10px; }
    .base, .accent {
      font: inherit; color: inherit; cursor: pointer; background: transparent;
      -webkit-tap-highlight-color: transparent;
    }
    .base {
      display: flex; flex-direction: column; align-items: stretch; gap: 6px; padding: 6px;
      border: 1px solid rgb(var(--mp-ink-rgb) / 0.16); border-radius: 12px;
    }
    .base.selected, .accent.selected { border-color: var(--mp-accent-strong); box-shadow: 0 0 0 1px var(--mp-accent-strong); }
    .base:focus-visible, .accent:focus-visible { outline: 2px solid var(--mp-accent); outline-offset: 2px; }
    .preview {
      display: flex; height: 52px; border-radius: 8px; overflow: hidden;
      border: 1px solid rgb(var(--mp-ink-rgb) / 0.12);
    }
    .half { flex: 1; position: relative; padding: 9px 8px; display: flex; flex-direction: column; gap: 5px; }
    .line { display: block; height: 5px; width: 70%; border-radius: 3px; opacity: 0.85; }
    .line.short { width: 45%; }
    .dot { position: absolute; right: 7px; bottom: 7px; width: 12px; height: 12px; border-radius: 50%; }
    .label { font-size: 13px; text-align: center; }
    .hint { color: var(--mp-text-muted); font-size: 13px; margin: 8px 0 16px; }
    .accents { display: flex; flex-wrap: wrap; gap: 10px; }
    .accent {
      width: 48px; height: 48px; padding: 4px; border-radius: 50%;
      border: 1px solid transparent;
    }
    .fill {
      display: flex; align-items: center; justify-content: center; width: 100%; height: 100%;
      border-radius: 50%; color: var(--mp-on-accent);
    }
    .fill mat-icon { font-size: 22px; width: 22px; height: 22px; }
    .error { color: var(--mp-error); font-size: 14px; margin-top: 12px; }
  `],
})
export class AppearanceCardComponent {
  readonly theme = inject(ThemeService);

  /** Each base with its preview: two halves for System (light | dark), one for the others. */
  readonly bases = computed(() => {
    const accent = this.theme.accent();
    const painted: ResolvedThemeBase[] = ['dark', 'light', 'black', 'sepia'];
    const samples = this.theme.samplePalettes(painted.map((base) => ({ base, accent })));
    const swatch = (base: ResolvedThemeBase): Swatch => {
      const s = samples[painted.indexOf(base)];
      return { surface: s.surface, text: s.text, accent: s.accentStrong };
    };
    return THEME_BASES.map((id) => ({
      id,
      label: BASE_LABELS[id],
      hint: BASE_HINTS[id],
      swatches: id === 'system' ? [swatch('light'), swatch('dark')] : [swatch(id)],
    }));
  });

  /** Each accent as it paints on the CURRENT base. */
  readonly accents = computed(() => {
    const base = this.theme.resolved();
    const samples = this.theme.samplePalettes(THEME_ACCENTS.map((accent) => ({ base, accent })));
    return THEME_ACCENTS.map((id, i) => ({ id, label: ACCENT_LABELS[id], color: samples[i].accentStrong }));
  });

  hintFor(base: ThemeBase): string {
    return BASE_HINTS[base];
  }
}
