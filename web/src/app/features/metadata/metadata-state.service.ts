import { Injectable, inject } from '@angular/core';
import { Subject } from 'rxjs';

import { SeriesInfoDto } from '../../core/api/api-types';
import { MetadataApiService } from './metadata-api.service';

/** A change to a node's series link that other views can react to (1.24.0). */
export interface SeriesInfoChange {
  nodeId: string;
  /** The node's OWN information, as `CatalogNodeDto.hasSeriesInfo` scopes it. */
  hasSeriesInfo: boolean;
}

/**
 * Cross-component channel for series-link changes (1.24.0), the metadata sibling of
 * `FavoritesStateService`. Whatever changes a node's link - Link in the identify dialog,
 * its Undo, Unlink, Don't match / Clear in the admin menu or the selection bar -
 * announces the node here after the server accepted it; the card (i), the list-row (i)
 * and the browse top-bar button observe `changed$` and update in place, so the browse
 * listing (paging, scroll) is never reloaded.
 */
@Injectable({ providedIn: 'root' })
export class MetadataStateService {
  private readonly api = inject(MetadataApiService);
  private readonly changed = new Subject<SeriesInfoChange>();
  readonly changed$ = this.changed.asObservable();

  /** Announces a known outcome (a Link always gives the node its own information). */
  announce(nodeId: string, hasSeriesInfo: boolean): void {
    this.changed.next({ nodeId, hasSeriesInfo });
  }

  /**
   * Announces a change whose outcome depends on the node (Unlink, Don't match, clear,
   * Undo): the node may still have ComicInfo of its own, so the new value comes from
   * one `series-info` GET. A failed GET announces nothing (the next browse load is
   * server truth).
   */
  refresh(nodeId: string): void {
    this.api.getSeriesInfo(nodeId).subscribe({
      next: (info) => this.announce(nodeId, ownSeriesInfo(info)),
      error: () => undefined,
    });
  }
}

/**
 * Whether the resolved info is the node's OWN (the browse `hasSeriesInfo` scope): its own
 * confirmed web link, or ComicInfo of its own (an archive's, or a folder's child /
 * grandchild archives'). Inherited links and "Don't match" never count; hidden series
 * information resolves to `None` and so to false.
 */
export function ownSeriesInfo(info: SeriesInfoDto | null | undefined): boolean {
  if (!info || info.state === 'None' || info.state === 'DontMatch') return false;
  const link = info.link;
  const ownWeb = !!info.web && !!link && !link.inherited && (link.state === 'Confirmed' || link.state === 'Auto');
  return ownWeb || (info.comicInfo?.itemsWithComicInfo ?? 0) > 0;
}
