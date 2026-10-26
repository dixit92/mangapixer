import { TestBed } from '@angular/core/testing';

import { LibraryViewPreferencesDto } from '../api/api-types';
import {
  CARD_SIZE_MAX,
  CARD_SIZE_MIN,
  CARD_SIZE_STORAGE_KEY,
  DEFAULT_CARD_SIZE,
  DEFAULT_LIST_COLUMNS,
  DeviceDisplayPreferencesService,
  LIST_COLUMNS_STORAGE_KEY,
} from './device-display-preferences.service';

const prefs = (extra: Partial<LibraryViewPreferencesDto> = {}): LibraryViewPreferencesDto =>
  ({ viewMode: 'card', density: 'comfortable', sort: 'name', ...extra });

function create(): DeviceDisplayPreferencesService {
  TestBed.resetTestingModule();
  return TestBed.inject(DeviceDisplayPreferencesService);
}

describe('DeviceDisplayPreferencesService', () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => vi.restoreAllMocks());

  it('starts on the defaults when the device has no copy and nothing was seeded', () => {
    const svc = create();
    expect(svc.cardSize()).toBe(DEFAULT_CARD_SIZE);
    expect(svc.listColumns()).toBe(DEFAULT_LIST_COLUMNS);
  });

  describe('seeding from the per-user server value', () => {
    it('seeds a device without a copy from the server value and stores it on the device', () => {
      const svc = create();
      svc.seedFrom(prefs({ cardSize: '200', listColumns: 3 }));
      expect(svc.cardSize()).toBe(200);
      expect(svc.listColumns()).toBe(3);
      expect(localStorage.getItem(CARD_SIZE_STORAGE_KEY)).toBe('200');
      expect(localStorage.getItem(LIST_COLUMNS_STORAGE_KEY)).toBe('3');
    });

    it('shows the size a legacy view mode implied, as before, but does not store it as the device\'s choice', () => {
      const svc = create();
      svc.seedFrom(prefs({ viewMode: 'poster', density: 'compact' }));
      expect(svc.cardSize()).toBe(168);
      expect(localStorage.getItem(CARD_SIZE_STORAGE_KEY)).toBeNull();
      // so another device's later choice still reaches this one
      svc.seedFrom(prefs({ viewMode: 'poster', density: 'compact', cardSize: '200' }));
      expect(svc.cardSize()).toBe(200);
      expect(localStorage.getItem(CARD_SIZE_STORAGE_KEY)).toBe('200');
    });

    it('clamps an out-of-range server card size and ignores an out-of-range column count', () => {
      const svc = create();
      svc.seedFrom(prefs({ cardSize: '9999', listColumns: 7 }));
      expect(svc.cardSize()).toBe(CARD_SIZE_MAX);
      expect(svc.listColumns()).toBe(DEFAULT_LIST_COLUMNS);
      expect(localStorage.getItem(LIST_COLUMNS_STORAGE_KEY)).toBeNull();
    });

    it('leaves the device unset when the server has nothing usable, so a later server value can still seed it', () => {
      const svc = create();
      svc.seedFrom(prefs({ cardSize: '', listColumns: 0 }));
      expect(svc.cardSize()).toBe(DEFAULT_CARD_SIZE);
      expect(localStorage.getItem(CARD_SIZE_STORAGE_KEY)).toBeNull();
      svc.seedFrom(prefs({ cardSize: '180' }));
      expect(svc.cardSize()).toBe(180);
    });

    it('the device copy wins: a server value never replaces it', () => {
      localStorage.setItem(CARD_SIZE_STORAGE_KEY, '130');
      localStorage.setItem(LIST_COLUMNS_STORAGE_KEY, '1');
      const svc = create();
      svc.seedFrom(prefs({ cardSize: '240', listColumns: 3 }));
      expect(svc.cardSize()).toBe(130);
      expect(svc.listColumns()).toBe(1);
      expect(localStorage.getItem(CARD_SIZE_STORAGE_KEY)).toBe('130');
    });

    it('ignores a stored value that is not valid and seeds over it', () => {
      localStorage.setItem(CARD_SIZE_STORAGE_KEY, 'huge');
      localStorage.setItem(LIST_COLUMNS_STORAGE_KEY, '9');
      const svc = create();
      expect(svc.cardSize()).toBe(DEFAULT_CARD_SIZE);
      svc.seedFrom(prefs({ cardSize: '170', listColumns: 2 }));
      expect(svc.cardSize()).toBe(170);
      expect(svc.listColumns()).toBe(2);
    });
  });

  describe('changes', () => {
    it('setCardSize clamps, shows and stores the value on the device', () => {
      const svc = create();
      expect(svc.setCardSize(9999)).toBe(CARD_SIZE_MAX);
      expect(svc.cardSize()).toBe(CARD_SIZE_MAX);
      expect(svc.setCardSize(1)).toBe(CARD_SIZE_MIN);
      expect(localStorage.getItem(CARD_SIZE_STORAGE_KEY)).toBe(String(CARD_SIZE_MIN));
    });

    it('setListColumns clamps to 1-3 and stores', () => {
      const svc = create();
      expect(svc.setListColumns(99)).toBe(3);
      expect(localStorage.getItem(LIST_COLUMNS_STORAGE_KEY)).toBe('3');
      expect(svc.setListColumns(0)).toBe(1);
    });

    it('a preview shows the value but stores nothing until it is committed', () => {
      const svc = create();
      svc.previewCardSizeOnly(220);
      svc.previewListColumnsOnly(3);
      expect(svc.cardSize()).toBe(220);
      expect(svc.listColumns()).toBe(3);
      expect(localStorage.getItem(CARD_SIZE_STORAGE_KEY)).toBeNull();
      expect(localStorage.getItem(LIST_COLUMNS_STORAGE_KEY)).toBeNull();
      svc.setCardSize(220);
      expect(localStorage.getItem(CARD_SIZE_STORAGE_KEY)).toBe('220');
    });

    it('another tab changing the stored value is followed', () => {
      const svc = create();
      svc.setCardSize(150);
      localStorage.setItem(CARD_SIZE_STORAGE_KEY, '210');
      window.dispatchEvent(new StorageEvent('storage', { key: CARD_SIZE_STORAGE_KEY }));
      expect(svc.cardSize()).toBe(210);
    });
  });

  describe('stamp', () => {
    it('puts the device values over a stale blob', () => {
      const svc = create();
      svc.setCardSize(190);
      svc.setListColumns(3);
      const stale = prefs({ cardSize: '120', listColumns: 1, libraryPageSize: 100 });
      expect(svc.stamp(stale)).toEqual({ ...stale, cardSize: '190', listColumns: 3 });
      expect(stale.cardSize).toBe('120'); // the input is not mutated
    });

    it('leaves the blob as it is when the device has no copy', () => {
      const svc = create();
      const blob = prefs({ cardSize: '120', listColumns: 1 });
      expect(svc.stamp(blob)).toBe(blob);
    });

    it('stamps only the value the device has', () => {
      const svc = create();
      svc.setCardSize(200);
      expect(svc.stamp(prefs({ cardSize: '120', listColumns: 1 }))).toMatchObject({ cardSize: '200', listColumns: 1 });
    });
  });

  describe('storage unavailable (private mode)', () => {
    function breakStorage(): void {
      vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('denied'); });
      vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('denied'); });
    }

    it('does not throw, and the in-memory value lasts the session', () => {
      breakStorage();
      const svc = create();
      expect(svc.cardSize()).toBe(DEFAULT_CARD_SIZE);
      expect(() => svc.seedFrom(prefs({ cardSize: '170', listColumns: 3 }))).not.toThrow();
      expect(svc.cardSize()).toBe(170);
      expect(() => svc.setCardSize(230)).not.toThrow();
      expect(svc.cardSize()).toBe(230);
      expect(svc.stamp(prefs({ cardSize: '120' })).cardSize).toBe('230');
    });
  });
});
