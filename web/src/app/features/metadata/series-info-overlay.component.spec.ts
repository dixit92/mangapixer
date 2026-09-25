import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Router, provideRouter } from '@angular/router';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { MAT_BOTTOM_SHEET_DATA, MatBottomSheetRef } from '@angular/material/bottom-sheet';
import { of, throwError } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';
import { SeriesInfoDto } from '../../core/api/api-types';
import { MetadataApiService } from './metadata-api.service';
import { SeriesInfoOverlayComponent } from './series-info-overlay.component';
import { seriesInfo } from './series-info.testing';

/**
 * The series-info overlay (1.24.0): loads through either host (dialog / bottom
 * sheet), attributes web data with a no-referrer link, shows ComicInfo coverage,
 * opens the series page on the ANCHOR, and gates the admin menu.
 */
describe('SeriesInfoOverlayComponent', () => {
  const dialogRef = { close: vi.fn() };
  const sheetRef = { dismiss: vi.fn() };

  function create(info: SeriesInfoDto | 'error', opts: { admin?: boolean; sheet?: boolean } = {}) {
    dialogRef.close.mockClear();
    sheetRef.dismiss.mockClear();
    const api = {
      getSeriesInfo: vi.fn(() => (info === 'error' ? throwError(() => ({ error: 'not_found' })) : of(info))),
    };
    const providers: unknown[] = [
      provideNoopAnimations(),
      provideRouter([]),
      { provide: MetadataApiService, useValue: api },
      { provide: AuthService, useValue: { isAdmin: () => !!opts.admin } },
    ];
    if (opts.sheet) {
      providers.push({ provide: MAT_BOTTOM_SHEET_DATA, useValue: { nodeId: 'card-1' } }, { provide: MatBottomSheetRef, useValue: sheetRef });
    } else {
      providers.push({ provide: MAT_DIALOG_DATA, useValue: { nodeId: 'card-1' } }, { provide: MatDialogRef, useValue: dialogRef });
    }
    TestBed.configureTestingModule({ imports: [SeriesInfoOverlayComponent], providers: providers as never[] });
    const fixture = TestBed.createComponent(SeriesInfoOverlayComponent);
    fixture.detectChanges();
    return { fixture, api, el: fixture.nativeElement as HTMLElement };
  }

  it('loads the node it was opened for and renders the summary', () => {
    const { api, el } = create(seriesInfo({ nodeId: 'card-1', state: 'ComicInfo', comicInfo: { itemsWithComicInfo: 12, itemsTotal: 14 } }));
    expect(api.getSeriesInfo).toHaveBeenCalledWith('card-1');
    expect(el.querySelector('[data-testid="series-title"]')!.textContent).toBe('Synthetic Saga');
    expect(el.querySelector('[data-testid="series-comicinfo-source"]')!.textContent).toContain('ComicInfo in 12 of 14 items');
  });

  it('works in a bottom sheet too (phone)', () => {
    const { api, el } = create(seriesInfo({ nodeId: 'card-1' }), { sheet: true });
    expect(api.getSeriesInfo).toHaveBeenCalledWith('card-1');
    (el.querySelector('button.close') as HTMLButtonElement).click();
    expect(sheetRef.dismiss).toHaveBeenCalled();
  });

  it('credits the web provider with a no-referrer link', () => {
    const { el } = create(seriesInfo({
      state: 'Web',
      web: { provider: 'mangaupdates', providerName: 'MangaUpdates', siteUrl: 'https://www.mangaupdates.com/series/abc/x', fetchedAt: new Date().toISOString() },
    }));
    const link = el.querySelector('.source a') as HTMLAnchorElement;
    expect(link.textContent).toBe('MangaUpdates');
    expect(link.getAttribute('rel')).toBe('noopener noreferrer');
    expect(link.getAttribute('referrerpolicy')).toBe('no-referrer');
    expect(el.textContent).toContain('today');
  });

  it('opens the series page on the anchor and closes itself', () => {
    const { fixture, el } = create(seriesInfo({ nodeId: 'card-1', anchorNodeId: 'folder-9' }));
    const router = TestBed.inject(Router);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    (el.querySelector('[data-testid="open-series-page"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(navigate).toHaveBeenCalledWith(['/series', 'folder-9']);
    expect(dialogRef.close).toHaveBeenCalled();
  });

  it('shows no series-page button for a multi-series folder (the overlay already lists them)', () => {
    const { el } = create(seriesInfo({ state: 'Mixed', title: null }));
    expect(el.querySelector('[data-testid="open-series-page"]')).toBeNull();
  });

  it('shows no series-page button for a node without information', () => {
    const { el } = create(seriesInfo({ state: 'None', title: null }));
    expect(el.querySelector('[data-testid="open-series-page"]')).toBeNull();
    expect(el.querySelector('[data-testid="series-none"]')).not.toBeNull();
  });

  it('shows the admin menu to admins only', () => {
    expect(create(seriesInfo(), { admin: false }).el.querySelector('app-series-admin-actions')).toBeNull();
    TestBed.resetTestingModule();
    expect(create(seriesInfo(), { admin: true }).el.querySelector('app-series-admin-actions')).not.toBeNull();
  });

  it('shows an error state when the node cannot be loaded', () => {
    const { el } = create('error');
    expect(el.querySelector('.error')!.textContent).toContain('not available');
  });
});
