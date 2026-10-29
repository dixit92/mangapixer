import { Injectable, computed, signal } from '@angular/core';

import { LibraryViewMode } from '../api/api-types';
import {
  DeviceDisplayKeys,
  DeviceDisplayLayer,
  clampCardSize,
  clampListColumns,
  validCardSize,
  validListColumns,
  writeStored,
} from './device-display-preferences.service';

/** The Favorites view's own localStorage keys (1.50.0 lane F): never browse's, so the two views stay independent on a device. */
export const FAVORITES_CARD_SIZE_STORAGE_KEY = 'mangapixer-favorites-card-size';
export const FAVORITES_LIST_COLUMNS_STORAGE_KEY = 'mangapixer-favorites-list-columns';
export const FAVORITES_VIEW_MODE_STORAGE_KEY = 'mangapixer-favorites-view-mode';
export const FAVORITES_SORT_STORAGE_KEY = 'mangapixer-favorites-sort';

export const FAVORITES_DISPLAY_KEYS: DeviceDisplayKeys = {
  cardSize: FAVORITES_CARD_SIZE_STORAGE_KEY,
  listColumns: FAVORITES_LIST_COLUMNS_STORAGE_KEY,
};

/** The Favorites sorts (owner decision 6). The wire value of `GET favorites?sort=`. */
export type FavoritesSort = 'favorited' | 'name' | 'recentlyAdded' | 'recentlyUpdated';
export type FavoritesSortDirection = 'asc' | 'desc';

/** A sort with its direction (only Name has one; the date sorts and Recently favorited are newest first). */
export interface FavoritesSortChoice {
  readonly sort: FavoritesSort;
  readonly direction: FavoritesSortDirection;
}

export const DEFAULT_FAVORITES_SORT: FavoritesSortChoice = { sort: 'favorited', direction: 'desc' };

/** What a device starts the Favorites view with: browse's values on that device (owner decision 10). */
export interface FavoritesDisplaySeed {
  readonly cardSize: number;
  readonly listColumns: number;
  readonly viewMode: LibraryViewMode;
}

/**
 * The Favorites view's per-DEVICE display settings (1.50.0 lane F, owner decisions 9 and 10): its own view mode (card / list), card
 * size, list-view column count and sort, in this browser's localStorage under the `mangapixer-favorites-*` keys - per device, never sent
 * to the server and never shared with browse. A device without them is seeded ONCE from browse's values on that device (`seedOnce`):
 * each missing value is stored, so from then on the two views change independently. The sort is not seeded (it starts on Recently
 * favorited, as the page always did) and persists per device; the filters are transient (the page's own state).
 */
@Injectable({ providedIn: 'root' })
export class FavoritesDisplayPreferencesService extends DeviceDisplayLayer {
  private readonly deviceViewMode = signal<LibraryViewMode | null>(readViewMode());
  private readonly deviceSort = signal<FavoritesSortChoice | null>(readSort());

  /** The view mode shown on this device (card until seeded or chosen). */
  readonly viewMode = computed<LibraryViewMode>(() => this.deviceViewMode() ?? 'card');
  /** The sort shown on this device. */
  readonly sort = computed<FavoritesSortChoice>(() => this.deviceSort() ?? DEFAULT_FAVORITES_SORT);

  constructor() {
    super(FAVORITES_DISPLAY_KEYS);
  }

  /** True while any display value is still missing on this device (the page then asks for browse's values once). */
  needsSeed(): boolean {
    return this.deviceCardSize() === null || this.deviceListColumns() === null || this.deviceViewMode() === null;
  }

  /** Stores each display value this device does not have yet from `seed` (browse's values on this device). Never overwrites. */
  seedOnce(seed: FavoritesDisplaySeed): void {
    if (this.deviceCardSize() === null && validCardSize(seed.cardSize)) this.storeCardSize(clampCardSize(seed.cardSize));
    if (this.deviceListColumns() === null && validListColumns(seed.listColumns)) this.storeListColumns(clampListColumns(seed.listColumns));
    if (this.deviceViewMode() === null) this.storeViewMode(seed.viewMode === 'list' ? 'list' : 'card');
  }

  setViewMode(mode: LibraryViewMode): void {
    this.storeViewMode(mode);
  }

  /** Store a sort. Name keeps the given direction; every other sort is newest first. */
  setSort(sort: FavoritesSort, direction: FavoritesSortDirection = sort === 'name' ? 'asc' : 'desc'): FavoritesSortChoice {
    const choice: FavoritesSortChoice = { sort, direction: sort === 'name' ? direction : 'desc' };
    this.deviceSort.set(choice);
    writeStored(FAVORITES_SORT_STORAGE_KEY, encodeSort(choice));
    return choice;
  }

  protected override onStorage(key: string | null): void {
    super.onStorage(key);
    if (key === FAVORITES_VIEW_MODE_STORAGE_KEY || key === null) this.deviceViewMode.set(readViewMode());
    if (key === FAVORITES_SORT_STORAGE_KEY || key === null) this.deviceSort.set(readSort());
  }

  private storeViewMode(mode: LibraryViewMode): void {
    this.deviceViewMode.set(mode);
    writeStored(FAVORITES_VIEW_MODE_STORAGE_KEY, mode);
  }
}

/** `name-desc` for Name Z-A, else the sort's own name. */
export function encodeSort(choice: FavoritesSortChoice): string {
  return choice.sort === 'name' && choice.direction === 'desc' ? 'name-desc' : choice.sort;
}

export function decodeSort(raw: string | null): FavoritesSortChoice | null {
  switch (raw) {
    case 'favorited':
    case 'recentlyAdded':
    case 'recentlyUpdated':
      return { sort: raw, direction: 'desc' };
    case 'name':
      return { sort: 'name', direction: 'asc' };
    case 'name-desc':
      return { sort: 'name', direction: 'desc' };
    default:
      return null;
  }
}

function readRaw(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

function readViewMode(): LibraryViewMode | null {
  const raw = readRaw(FAVORITES_VIEW_MODE_STORAGE_KEY);
  return raw === 'card' || raw === 'list' ? raw : null;
}

function readSort(): FavoritesSortChoice | null {
  return decodeSort(readRaw(FAVORITES_SORT_STORAGE_KEY));
}

