import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { CatalogNodeDto } from '../../core/api/api-types';
import { CoverImageDirective } from '../cover-image.directive';

/**
 * Pinned "Continue" row (1.7.0). Renders the browsed folder's next-to-read
 * descendant archive (the browse response's additive `nextUnread` field) as a
 * distinct row ABOVE the normal sorted list - the list itself keeps its Sort By /
 * direction order. Renders NOTHING when `nextUnread` is null (every descendant
 * read, or the folder has no readable descendant archive).
 *
 * Purely presentational: no injection, no API calls. The resume affordance links
 * to `/reader/:id` (the same route an archive card opens), so a tap continues
 * reading exactly where the reader would land. Visual tokens (cover aspect ratio,
 * fallback icon) mirror `library-browse.component.ts` so the Continue row reads
 * as a single emphasized card rather than a foreign element.
 *
 * Wiring (integrator applies after the browse lane merges; this lane does NOT edit
 * `library-browse.component.ts`): place `<app-continue-row [node]="page().nextUnread" />`
 * above the `.nodes` grid/list in the browse template, fed by the browse page
 * response. The component hides itself when the input is null.
 */
@Component({
  selector: 'app-continue-row',
  standalone: true,
  imports: [RouterLink, MatIconModule, MatTooltipModule, CoverImageDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (node(); as n) {
      <a class="continue-row" [routerLink]="['/reader', n.id]" [matTooltip]="'Continue reading ' + n.displayName"
         [attr.aria-label]="'Continue reading ' + n.displayName">
        <div class="cover">
          @if (n.coverUrl) {
            <img appCover [src]="n.coverUrl" alt="" loading="lazy">
          }
          <mat-icon class="cover-fallback">menu_book</mat-icon>
        </div>
        <div class="info">
          <span class="label">Continue</span>
          <span class="title" [title]="n.displayName">{{ n.displayName }}</span>
        </div>
        <mat-icon class="resume" aria-hidden="true">play_arrow</mat-icon>
      </a>
    }
  `,
  styles: [`
    :host { display: block; }
    .continue-row {
      display: flex; align-items: center; gap: 14px;
      margin-bottom: 16px; padding: 10px 14px;
      text-decoration: none; color: inherit;
      background: linear-gradient(90deg, rgba(124, 77, 255, 0.18), rgba(124, 77, 255, 0.04));
      border: 1px solid rgba(124, 77, 255, 0.45);
      border-radius: 12px;
      transition: border-color 0.12s, background 0.12s;
    }
    .continue-row:hover {
      border-color: rgba(124, 77, 255, 0.8);
      background: linear-gradient(90deg, rgba(124, 77, 255, 0.26), rgba(124, 77, 255, 0.08));
    }
    .cover {
      position: relative; flex: 0 0 auto;
      width: 44px; height: 64px; border-radius: 6px; overflow: hidden;
      background: rgba(255, 255, 255, 0.06);
      display: flex; align-items: center; justify-content: center;
    }
    .cover img { width: 100%; height: 100%; object-fit: cover; position: relative; z-index: 1; }
    .cover-fallback { font-size: 22px; width: 22px; height: 22px; color: #777; position: absolute; z-index: 0; }
    .info { flex: 1 1 auto; min-width: 0; display: flex; flex-direction: column; gap: 2px; }
    .label {
      font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: 0.6px;
      color: #b39dff;
    }
    .title {
      font-size: 15px; font-weight: 600; color: #e6e6ee;
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .resume { flex: 0 0 auto; color: #b39dff; font-size: 22px; width: 22px; height: 22px; }
  `],
})
export class ContinueRowComponent {
  /**
   * The browse response's `nextUnread` archive, or null/undefined. A falsy value
   * renders nothing so the row simply disappears when there is nothing to continue.
   * Typed to accept `undefined` because the client-side `nextUnread` field is
   * optional (the server always sends it, but older PageResponse fixtures omit it).
   */
  readonly node = input<CatalogNodeDto | null | undefined>(null);

  /** Whether a row is currently shown (null/undefined input hides the host content). */
  readonly present = computed(() => this.node() != null);
}
