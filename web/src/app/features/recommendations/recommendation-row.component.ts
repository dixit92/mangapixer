import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';

import { CatalogNodeDto, RecommendationDto } from '../../core/api/api-types';
import { CoverImageDirective } from '../../shared/cover-image.directive';
import { InfoToggleComponent } from '../../shared/info-toggle/info-toggle.component';
import { StarToggleComponent } from '../../shared/star-toggle/star-toggle.component';
import { FolderRollupBadgeComponent } from '../../shared/folder-rollup-badge/folder-rollup-badge.component';
import { SeriesInfoHoverDirective } from '../../shared/hover-info/series-info-hover.directive';
import { RatingStarsComponent } from '../ratings/rating-stars.component';
import { favoriteLink } from '../favorites/favorite-stack';

/**
 * One row of recommendations (1.53.0), shared by Home's "Recommended for you" (`variant="home"`, after Continue reading) and the series
 * page's "More like this" (`variant="page"`, after About) - visual audit N2 / N3. A heading and Home's strip: `.cont-card` cards (2:3 cover,
 * title on one line, the reason as one muted 12 px line with a `title`), the usual overlays (read state top right, (i) bottom left, the
 * star bottom right, the own-rating badge above it). Home cards follow `--card-size`; the series page has no size slider, so its cards
 * are `clamp(112px, 15vw, 150px)` - a half card at the end says the row scrolls. Nothing is rendered without items (no heading, no
 * space). Anything projected into the row goes at the end of the heading line (Home's card-size slider, audit V10).
 */
@Component({
  selector: 'app-recommendation-row',
  standalone: true,
  imports: [RouterLink, MatIconModule, MatTooltipModule, CoverImageDirective, InfoToggleComponent, StarToggleComponent,
    FolderRollupBadgeComponent, SeriesInfoHoverDirective, RatingStarsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (items().length > 0) {
      <section class="rec-section" [class.page]="variant() === 'page'" [attr.data-testid]="testId()" [attr.aria-label]="heading()">
        <div class="rec-head">
          <h3 class="rec-heading">{{ heading() }}</h3>
          <ng-content />
        </div>
        <div class="strip">
          @for (item of items(); track item.node.id) {
            <a #cardEl class="cont-card" [routerLink]="link(item.node)" data-testid="recommendation-card">
              <div class="cover" [appSeriesInfoHover]="item.node.hasSeriesInfo ? item.node.id : null" [hoverAnchor]="cardEl">
                @if (cover(item.node); as src) { <img appCover [src]="src" alt="" loading="lazy"> }
                <mat-icon class="cover-fallback">{{ item.node.kind === 'Folder' ? 'folder' : 'menu_book' }}</mat-icon>
                @if (item.node.isRead) {
                  <span class="badge read" matTooltip="Read">✓ Read</span>
                } @else if (item.node.readingState === 'InProgress') {
                  <span class="badge reading">Reading</span>
                }
                <app-folder-rollup-badge [rollup]="item.node.readRollup" />
                <app-info-toggle [nodeId]="item.node.id" [hasSeriesInfo]="!!item.node.hasSeriesInfo"
                                 [anchored]="item.node.kind === 'Archive'" [overlay]="true" />
                <app-star-toggle [nodeId]="item.node.id" [favorite]="!!item.node.isFavorite" [overlay]="true" [compact]="true" />
                <app-rating-stars mode="badge" [overlay]="true" [nodeId]="item.node.id" [rating]="item.node.myRating" />
              </div>
              <div class="cont-title" [title]="item.node.displayName" data-testid="recommendation-title"
                   [appSeriesInfoHover]="item.node.hasSeriesInfo ? item.node.id : null" [hoverAnchor]="cardEl">{{ item.node.displayName }}</div>
              <div class="cont-page reason" [title]="item.reason.display" data-testid="recommendation-reason">{{ item.reason.display }}</div>
            </a>
          }
        </div>
      </section>
    }
  `,
  styles: [`
    :host { display: block; }
    .rec-section { margin-bottom: 28px; }
    .rec-head { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
    .rec-heading { margin: 8px 0 12px; }
    /* Series page (audit N2): the heading in the page's summary style, no surface behind the row. */
    .page { margin: 10px 0; }
    .page .rec-heading { margin: 0; padding: 4px 0; font-size: 16px; font-weight: 600; color: var(--mp-text); }
    .strip { display: flex; gap: 16px; overflow-x: auto; padding: 8px 8px 8px 0; scroll-snap-type: x proximity; }
    .cont-card {
      display: block; flex: 0 0 auto; width: var(--card-size, 150px);
      text-decoration: none; color: inherit; scroll-snap-align: start;
    }
    .page .cont-card { width: clamp(112px, 15vw, 150px); }
    .cover {
      position: relative; width: 100%; aspect-ratio: 2 / 3; border-radius: 8px;
      overflow: hidden; background: rgb(var(--mp-ink-rgb) / 0.06);
      display: flex; align-items: center; justify-content: center;
    }
    .cover img { width: 100%; height: 100%; object-fit: cover; position: relative; z-index: 1; }
    .cover-fallback { position: absolute; font-size: 48px; width: 48px; height: 48px; color: var(--mp-text-faint); z-index: 0; }
    .cont-title {
      margin-top: 6px; font-size: 13px; font-weight: 500;
      white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
    }
    .cont-page { font-size: 12px; color: var(--mp-text-muted); }
    .reason { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .badge {
      position: absolute; top: 6px; right: 6px; z-index: 2;
      font-size: 11px; font-weight: 700; padding: 2px 7px; border-radius: 10px;
      background: var(--mp-accent-strong); color: var(--mp-on-accent); letter-spacing: 0.2px;
    }
    .badge.read { background: var(--mp-success-fill); color: var(--mp-on-scrim); }
  `],
})
export class RecommendationRowComponent {
  readonly items = input<RecommendationDto[]>([]);
  readonly heading = input.required<string>();
  /** `home`: `--card-size` cards, Home's h3; `page`: the series page's fixed card width and summary-style heading. */
  readonly variant = input<'home' | 'page'>('home');
  readonly testId = input<string | null>(null);

  /** Folders open browse, archives the reader (the Favorites row's rule). */
  link(node: CatalogNodeDto): string[] {
    return favoriteLink(node);
  }

  /** The server's layered cover, else (older servers) the archive's own cover. */
  cover(node: CatalogNodeDto): string | null {
    return node.coverUrl ?? (node.kind === 'Archive' ? `/api/v1/items/${node.id}/cover` : null);
  }
}
