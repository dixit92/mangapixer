import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';

import { CompanionDto } from '../../../core/api/api-types';
import { MetadataApiService } from '../metadata-api.service';
import { companionStateText, looksLikeMangaDexReference, mangaDexCompanion } from './mangadex-match';
import { MangaDexMatchDialogComponent } from './mangadex-match-dialog.component';

const ID = '801513ba-a712-498c-8f57-cae55b38cc92';
const mdx = (overrides: Partial<CompanionDto> = {}): CompanionDto => ({
  provider: 'mangadex', providerName: 'MangaDex', siteUrl: 'https://mangadex.org/title/' + ID, state: 'Auto',
  checkedAt: '2026-09-29T00:00:00Z', ...overrides,
});

/** "Change MangaDex match..." (1.29.0): the helpers, the API request shapes, and the dialog's three actions. */
describe('MangaDex match', () => {
  it('accepts a title id or a mangadex.org/title address only', () => {
    expect(looksLikeMangaDexReference(ID)).toBe(true);
    expect(looksLikeMangaDexReference(' https://mangadex.org/title/' + ID + '/berserk ')).toBe(true);
    expect(looksLikeMangaDexReference('mangadex.org/title/' + ID.toUpperCase())).toBe(true);
    expect(looksLikeMangaDexReference('https://mangadex.org/chapter/' + ID)).toBe(false);
    expect(looksLikeMangaDexReference('https://example.org/title/' + ID)).toBe(false);
    expect(looksLikeMangaDexReference('berserk')).toBe(false);
    expect(looksLikeMangaDexReference('')).toBe(false);
  });

  it('describes each state and picks the MangaDex companion', () => {
    expect(companionStateText('Auto')).toContain('MangaUpdates link');
    expect(companionStateText('None')).toContain('Not on MangaDex');
    expect(companionStateText(null)).toBe('Not checked yet.');
    expect(mangaDexCompanion([{ ...mdx(), provider: 'anilist' }, mdx()])!.provider).toBe('mangadex');
    expect(mangaDexCompanion([])).toBeNull();
  });

  describe('API calls', () => {
    let api: MetadataApiService;
    let http: HttpTestingController;
    beforeEach(() => {
      TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
      api = TestBed.inject(MetadataApiService);
      http = TestBed.inject(HttpTestingController);
    });
    afterEach(() => http.verify());

    it('uses the admin companion and volume-cover routes', () => {
      api.getCompanions('n 1').subscribe();
      http.expectOne({ method: 'GET', url: '/api/v1/admin/metadata/nodes/n%201/companions' }).flush([]);
      api.setMangaDexCompanion('n1', ID).subscribe();
      const put = http.expectOne({ method: 'PUT', url: '/api/v1/admin/metadata/nodes/n1/companions/mangadex' });
      expect(put.request.body).toEqual({ reference: ID });
      put.flush([]);
      api.clearMangaDexCompanion('n1').subscribe();
      http.expectOne({ method: 'DELETE', url: '/api/v1/admin/metadata/nodes/n1/companions/mangadex' }).flush([]);
      api.recheckMangaDexCompanion('n1').subscribe();
      http.expectOne({ method: 'POST', url: '/api/v1/admin/metadata/nodes/n1/companions/mangadex/recheck' }).flush([]);
      api.getVolumeCoverStatus().subscribe();
      http.expectOne({ method: 'GET', url: '/api/v1/admin/metadata/volume-covers/status' }).flush({});
      api.deleteStoredVolumeCovers().subscribe();
      http.expectOne({ method: 'DELETE', url: '/api/v1/admin/metadata/volume-covers' }).flush({});
    });
  });

  describe('dialog', () => {
    function create(list: CompanionDto[] = [mdx()]) {
      const api = {
        getCompanions: vi.fn(() => of(list)),
        setMangaDexCompanion: vi.fn(() => of([mdx({ state: 'Confirmed' })])),
        clearMangaDexCompanion: vi.fn(() => of([mdx({ state: 'None', siteUrl: null })])),
        recheckMangaDexCompanion: vi.fn(() => throwError(() => ({ message: 'Volume covers from the web are off.' }))),
      };
      const ref = { close: vi.fn() };
      TestBed.configureTestingModule({
        imports: [MangaDexMatchDialogComponent],
        providers: [
          provideNoopAnimations(),
          { provide: MetadataApiService, useValue: api },
          { provide: MatDialogRef, useValue: ref },
          { provide: MAT_DIALOG_DATA, useValue: { nodeId: 'n1' } },
        ],
      });
      const fixture = TestBed.createComponent(MangaDexMatchDialogComponent);
      fixture.detectChanges();
      const el = fixture.nativeElement as HTMLElement;
      return { fixture, c: fixture.componentInstance, api, ref, el, q: (s: string) => el.querySelector(s) as HTMLElement | null };
    }

    it('shows the current match with its MangaDex page', () => {
      const { q, api } = create();
      expect(api.getCompanions).toHaveBeenCalledWith('n1');
      expect(q('[data-testid="mdx-current"]')!.textContent).toContain('Found automatically');
      expect(q('[data-testid="mdx-link"]')!.getAttribute('href')).toBe('https://mangadex.org/title/' + ID);
      expect(q('[data-testid="mdx-link"]')!.getAttribute('rel')).toContain('noreferrer');
    });

    it('uses a pasted title only when it looks like one, then closes with a change', () => {
      const { c, fixture, api, ref, q } = create();
      c.reference.set('berserk');
      fixture.detectChanges();
      expect(q('[data-testid="mdx-reference-error"]')).not.toBeNull();
      expect((q('[data-testid="mdx-use"]') as HTMLButtonElement).disabled).toBe(true);
      c.reference.set('https://mangadex.org/title/' + ID);
      fixture.detectChanges();
      (q('[data-testid="mdx-use"]') as HTMLButtonElement).click();
      expect(api.setMangaDexCompanion).toHaveBeenCalledWith('n1', 'https://mangadex.org/title/' + ID);
      fixture.detectChanges();
      expect(q('[data-testid="mdx-current"]')!.textContent).toContain('Chosen by an admin');
      c.close();
      expect(ref.close).toHaveBeenCalledWith(true);
    });

    it('"Not on MangaDex" and a refused re-check', () => {
      const { c, fixture, api, q, ref } = create([]);
      expect(q('[data-testid="mdx-current"]')!.textContent).toContain('Not checked yet');
      (q('[data-testid="mdx-none"]') as HTMLButtonElement).click();
      expect(api.clearMangaDexCompanion).toHaveBeenCalledWith('n1');
      fixture.detectChanges();
      expect((q('[data-testid="mdx-none"]') as HTMLButtonElement).disabled).toBe(true);
      (q('[data-testid="mdx-recheck"]') as HTMLButtonElement).click();
      fixture.detectChanges();
      expect(q('[data-testid="mdx-error"]')!.textContent).toContain('Volume covers from the web are off.');
      c.close();
      expect(ref.close).toHaveBeenCalledWith(true);
    });
  });
});
