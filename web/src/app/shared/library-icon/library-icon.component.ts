import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';

/**
 * Shared library icon renderer (1.22.0). Every surface that shows a library
 * glyph - the app-shell sidebar (also reused by the mobile `/library-nav`
 * page), the Home "Libraries" section, `/libraries`, and the admin Libraries
 * card - renders through this ONE component instead of a hardcoded `folder`
 * `mat-icon`, so an admin-picked icon (or its absence) looks identical
 * everywhere.
 *
 * `icon` null means "no admin pick": renders a deterministic name-derived
 * default instead of a generic folder, so libraries stay visually
 * distinguishable at a glance even before anyone opens the icon picker. The
 * derivation is a pure function of the name (see `deriveDefaultLibraryIcon`),
 * so the same library always gets the same badge - it does not depend on ID,
 * insertion order, or any mutable state.
 *
 * Decorative in both modes (the library name is already rendered as text next
 * to the icon in every surface), so the glyph/badge itself is `aria-hidden`.
 */
@Component({
  selector: 'app-library-icon',
  standalone: true,
  imports: [MatIconModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (icon(); as name) {
      <mat-icon class="lib-icon" [style.font-size.px]="size()" [style.width.px]="size()"
                [style.height.px]="size()" [style.line-height.px]="size()" aria-hidden="true">{{ name }}</mat-icon>
    } @else {
      <span class="lib-badge" aria-hidden="true"
            [style.width.px]="size()" [style.height.px]="size()"
            [style.background]="badge().bg" [style.color]="badge().fg"
            [style.font-size.px]="size() * 0.5">{{ badge().initial }}</span>
    }
  `,
  styles: [`
    :host { display: contents; }
    .lib-icon { flex: 0 0 auto; }
    .lib-badge {
      flex: 0 0 auto;
      display: inline-flex; align-items: center; justify-content: center;
      border-radius: 50%;
      font-weight: 600;
      line-height: 1;
      user-select: none;
    }
  `],
})
export class LibraryIconComponent {
  /** Library display name; drives the default-badge derivation. */
  readonly name = input.required<string>();

  /** Admin-picked icon ligature, or null for the derived default. */
  readonly icon = input<string | null>(null);

  /** Rendered glyph/badge size in px. Matches the surface's existing icon size. */
  readonly size = input<number>(24);

  readonly badge = computed(() => deriveDefaultLibraryIcon(this.name()));
}

/** Deterministic default badge for a library with no admin-picked icon. */
export interface DefaultLibraryBadge {
  /** Uppercase first letter/digit of the name, or "?" for an empty/symbols-only name. */
  initial: string;
  bg: string;
  fg: string;
}

/**
 * Small fixed palette of dark-theme-legible tints (readable text-on-fill
 * contrast against the app's dark surfaces), matching the violet/cyan accents
 * already used elsewhere (see `--mp-accent` in styles.scss) plus a few
 * distinguishable companions so libraries don't all collapse to one color.
 */
const PALETTE: readonly { bg: string; fg: string }[] = [
  { bg: '#7c4dff', fg: '#ffffff' }, // violet (app accent)
  { bg: '#00acc1', fg: '#ffffff' }, // cyan (app tertiary)
  { bg: '#e53935', fg: '#ffffff' }, // red
  { bg: '#43a047', fg: '#ffffff' }, // green
  { bg: '#fb8c00', fg: '#1a1a1a' }, // amber
  { bg: '#3949ab', fg: '#ffffff' }, // indigo
  { bg: '#d81b60', fg: '#ffffff' }, // pink
  { bg: '#00897b', fg: '#ffffff' }, // teal
];

/**
 * Pure, deterministic hash of `name` -> a palette entry + initial. Same
 * algorithm class as `NaturalOrderComparer`'s use elsewhere in the app: no
 * `Math.random`, no Date, no ID - just the name, so it is stable across
 * renders, reloads, and server round-trips (the name is the only thing both
 * the picker preview and every render site can agree on without a network
 * round trip).
 */
export function deriveDefaultLibraryIcon(name: string): DefaultLibraryBadge {
  const trimmed = name.trim();
  const initial = (trimmed.match(/[\p{L}\p{N}]/u)?.[0] ?? '?').toUpperCase();

  let hash = 0;
  for (let i = 0; i < trimmed.length; i++) {
    hash = (hash * 31 + trimmed.charCodeAt(i)) | 0;
  }
  const entry = PALETTE[Math.abs(hash) % PALETTE.length];

  return { initial, bg: entry.bg, fg: entry.fg };
}
