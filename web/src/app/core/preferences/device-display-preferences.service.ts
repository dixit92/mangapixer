import { Injectable, computed, signal } from '@angular/core';

import { LibraryViewPreferencesDto } from '../api/api-types';

/** The localStorage keys of the device copies. */
export const CARD_SIZE_STORAGE_KEY = 'mangapixer-card-size';
export const LIST_COLUMNS_STORAGE_KEY = 'mangapixer-list-columns';

export const CARD_SIZE_MIN = 110;
export const CARD_SIZE_MAX = 260;
export const CARD_SIZE_STEP = 10;
export const DEFAULT_CARD_SIZE = 150;

export const LIST_COLUMNS_MIN = 1;
export const LIST_COLUMNS_MAX = 3;
export const LIST_COLUMNS_STEP = 1;
export const DEFAULT_LIST_COLUMNS = 2;

/**
 * The two per-DEVICE browse display settings (1.48.0): the card size and the list-view column count. They follow the theme's
 * pattern - a copy in this browser's localStorage - but the rule is device-first, not server-first:
 *
 *  - a device that has a copy always uses it; the per-user server value (`LibraryViewPreferencesDto.cardSize` / `listColumns`)
 *    is never read over it;
 *  - a device without a copy is seeded once from the server value (`seedFrom`, called with every preferences load), and the
 *    seed is stored, so the device keeps it from then on;
 *  - a change on a device is stored here and sent to the server by the caller (so a new device starts from the last choice);
 *  - every PUT of the whole preferences blob passes through `stamp`, so a blob loaded earlier can never write a stale size or
 *    column count over what this device shows.
 *
 * Every storage access is wrapped in try / catch (private mode, storage disabled): the in-memory value then lasts the session.
 */
@Injectable({ providedIn: 'root' })
export class DeviceDisplayPreferencesService {
  /** This device's own values; null while the device has none (the server value or the default shows). */
  private readonly deviceCardSize = signal<number | null>(readNumber(CARD_SIZE_STORAGE_KEY, validCardSize));
  private readonly deviceListColumns = signal<number | null>(readNumber(LIST_COLUMNS_STORAGE_KEY, validListColumns));
  /** The size a pre-1.6.0 view mode implied (grid / poster): shown while the device has no copy, but never stored as a choice. */
  private readonly legacyCardSize = signal<number | null>(null);
  /** A live preview while a slider is dragged: shown but neither stored nor sent. */
  private readonly previewCardSize = signal<number | null>(null);
  private readonly previewListColumns = signal<number | null>(null);

  /** The card size (px, min column width) shown on this device. */
  readonly cardSize = computed(() => this.previewCardSize() ?? this.deviceCardSize() ?? this.legacyCardSize() ?? DEFAULT_CARD_SIZE);
  /** The list-view column count (1-3) shown on this device. */
  readonly listColumns = computed(() => this.previewListColumns() ?? this.deviceListColumns() ?? DEFAULT_LIST_COLUMNS);

  constructor() {
    if (typeof window !== 'undefined') {
      // Another tab of this browser changed a value: follow it (the root service lives as long as the tab).
      window.addEventListener('storage', (e: StorageEvent) => {
        if (e.key === CARD_SIZE_STORAGE_KEY || e.key === null) {
          this.deviceCardSize.set(readNumber(CARD_SIZE_STORAGE_KEY, validCardSize));
        }
        if (e.key === LIST_COLUMNS_STORAGE_KEY || e.key === null) {
          this.deviceListColumns.set(readNumber(LIST_COLUMNS_STORAGE_KEY, validListColumns));
        }
      });
    }
  }

  /**
   * Called with every load of the per-user preferences. A value this device already has is left alone; a missing one is
   * seeded from the server (the stored `cardSize`, `listColumns` when 1-3) and stored. When the server has nothing usable the
   * device stays unset, so the default shows and a later server value can still seed it. The size an account that never chose
   * one implies through its legacy view mode (a server row's default is grid / comfortable = 150 px) is only SHOWN, not stored:
   * storing it would pin a device that never chose anything before another device's choice could reach it.
   */
  seedFrom(prefs: LibraryViewPreferencesDto | null | undefined): void {
    if (!prefs) return;
    if (this.deviceCardSize() === null) {
      const explicit = explicitCardSizeOf(prefs);
      if (explicit !== null) this.storeCardSize(explicit);
      this.legacyCardSize.set(explicit === null ? legacyCardSizeOf(prefs) : null);
    }
    if (this.deviceListColumns() === null) {
      const seed = listColumnsOf(prefs);
      if (seed !== null) this.storeListColumns(seed);
    }
  }

  /** A slider is being dragged: show the size without storing it (one commit on release). */
  previewCardSizeOnly(px: number): void {
    this.previewCardSize.set(clampCardSize(px));
  }

  /** Commit a card size: clamp, show, store on this device. The caller sends the preferences blob (through `stamp`). */
  setCardSize(px: number): number {
    const size = clampCardSize(px);
    this.previewCardSize.set(null);
    this.storeCardSize(size);
    return size;
  }

  previewListColumnsOnly(count: number): void {
    this.previewListColumns.set(clampListColumns(count));
  }

  setListColumns(count: number): number {
    const columns = clampListColumns(count);
    this.previewListColumns.set(null);
    this.storeListColumns(columns);
    return columns;
  }

  /**
   * The preferences blob to PUT: the same blob, with this device's card size and list columns in place of whatever the blob
   * carries (it was loaded earlier and may be stale). A device with no copy leaves the blob's own values.
   */
  stamp(prefs: LibraryViewPreferencesDto): LibraryViewPreferencesDto {
    const card = this.deviceCardSize();
    const columns = this.deviceListColumns();
    if (card === null && columns === null) return prefs;
    return {
      ...prefs,
      ...(card !== null ? { cardSize: String(card) } : {}),
      ...(columns !== null ? { listColumns: columns } : {}),
    };
  }

  private storeCardSize(size: number): void {
    this.deviceCardSize.set(size);
    writeStored(CARD_SIZE_STORAGE_KEY, String(size));
  }

  private storeListColumns(columns: number): void {
    this.deviceListColumns.set(columns);
    writeStored(LIST_COLUMNS_STORAGE_KEY, String(columns));
  }
}

function validCardSize(n: number): boolean {
  return Number.isFinite(n) && n >= CARD_SIZE_MIN && n <= CARD_SIZE_MAX;
}

function validListColumns(n: number): boolean {
  return Number.isInteger(n) && n >= LIST_COLUMNS_MIN && n <= LIST_COLUMNS_MAX;
}

export function clampCardSize(px: number): number {
  if (!Number.isFinite(px)) return DEFAULT_CARD_SIZE;
  return Math.min(CARD_SIZE_MAX, Math.max(CARD_SIZE_MIN, Math.round(px)));
}

export function clampListColumns(count: number): number {
  if (!Number.isFinite(count)) return DEFAULT_LIST_COLUMNS;
  return Math.min(LIST_COLUMNS_MAX, Math.max(LIST_COLUMNS_MIN, Math.round(count)));
}

/** The server blob's own numeric `cardSize` (clamped), or null when it has none. */
function explicitCardSizeOf(p: LibraryViewPreferencesDto): number | null {
  const stored = Number(p.cardSize);
  return p.cardSize != null && p.cardSize !== '' && Number.isFinite(stored) ? clampCardSize(stored) : null;
}

/** The px width the legacy viewMode + density combination used (poster 210 / 168, grid 150 / 112), else null. */
function legacyCardSizeOf(p: LibraryViewPreferencesDto): number | null {
  const compact = p.density === 'compact';
  if (p.viewMode === 'poster') return compact ? 168 : 210;
  if (p.viewMode === 'grid') return compact ? 112 : 150;
  return null;
}

function listColumnsOf(p: LibraryViewPreferencesDto): number | null {
  const n = Number(p.listColumns);
  return p.listColumns != null && validListColumns(n) ? n : null;
}

function readNumber(key: string, valid: (n: number) => boolean): number | null {
  try {
    const raw = localStorage.getItem(key);
    if (raw === null || raw.trim() === '') return null;
    const n = Number(raw);
    return valid(n) ? n : null;
  } catch {
    return null;
  }
}

function writeStored(key: string, value: string): void {
  try {
    localStorage.setItem(key, value);
  } catch {
    /* storage unavailable (private mode): the in-memory value lasts the session */
  }
}
