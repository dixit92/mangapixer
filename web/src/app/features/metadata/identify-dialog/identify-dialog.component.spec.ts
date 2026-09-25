import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Subject, of, throwError } from 'rxjs';

import { IdentifyContextDto, IdentifyPreviewDto, IdentifySearchResultDto } from '../../../core/api/api-types';
import { MetadataApiService } from '../metadata-api.service';
import { IdentifyDialogComponent, restorePrevious } from './identify-dialog.component';

/**
 * The identify dialog (1.24.0, lane B2) against a mocked API - never the network:
 * disabled state (no call beyond the context), search with the confirmed text only,
 * results + server-proxied thumbnails, look-up, ComicInfo hint, preview + warnings +
 * attribution, link with Undo, and typed errors.
 */
describe('IdentifyDialogComponent', () => {
  const ctx = (overrides: Partial<IdentifyContextDto> = {}): IdentifyContextDto => ({
    nodeId: 'n1',
    nodeKind: 'Folder',
    displayName: '[Grp] Berserk (1989)',
    libraryId: 'lib1',
    provider: 'mangaupdates',
    providerName: 'MangaUpdates',
    fetchAvailable: true,
    suggestions: ['Berserk', 'Berserk Deluxe'],
    budgetUsedToday: 3,
    dailyBudget: 5000,
    local: { displayName: '[Grp] Berserk (1989)', itemCount: 41, comicInfoSeries: 'Berserk', tallStrips: false, yearHint: 1989 },
    ...overrides,
  });

  const results: IdentifySearchResultDto = {
    provider: 'mangaupdates',
    page: 1,
    totalHits: 12,
    budgetUsedToday: 4,
    dailyBudget: 5000,
    candidates: [
      { externalId: '51239621230', title: 'Berserk', providerType: 'Manga', origin: 'Japan', year: 1989, score: 0.97, strength: 'Strong', imageToken: 'tok1' },
      { externalId: '2', title: 'Kuang Bao Ni Xi', hitTitle: 'Berserk Counterattack', providerType: 'Manhua', score: 0.5, strength: 'Weak', imageToken: null },
      { externalId: '3', title: 'Berserk (Novel)', providerType: 'Novel', format: 'Novel', score: 0.8, strength: 'Possible' },
    ],
  };

  const preview: IdentifyPreviewDto = {
    provider: 'mangaupdates',
    providerName: 'MangaUpdates',
    externalId: '51239621230',
    title: 'Berserk',
    altTitles: ['Beruseruku', 'Berserk: The Black Swordsman'],
    format: 'Comic',
    origin: 'Japan',
    webtoon: true,
    startYear: 1989,
    originVolumes: 43,
    originStatus: 'Ongoing',
    creators: [{ name: 'MIURA Kentaro', role: 'author' }],
    genres: ['Action'],
    siteUrl: 'https://www.mangaupdates.com/series/njeqwry/berserk',
    imageToken: 'tok2',
    score: 0.97,
    strength: 'Strong',
    fetchedAt: '2026-09-25T00:00:00Z',
    local: ctx().local,
    warnings: [{ code: 'count_mismatch', message: 'The record lists 43 volumes/chapters; this folder has 120 items.' }],
  };

  function create(context: IdentifyContextDto = ctx()) {
    const dialogRef = { close: vi.fn() };
    const undo = new Subject<void>();
    const snackBar = { open: vi.fn(() => ({ onAction: () => undo })) };
    const api = {
      getIdentifyContext: vi.fn(() => of(context)),
      search: vi.fn(() => of(results)),
      lookup: vi.fn(() => of(preview)),
      preview: vi.fn(() => of(preview)),
      link: vi.fn(() => of({ nodeId: 'n1', link: { nodeId: 'n1', state: 'Confirmed', updatedAt: 'x' }, previous: null })),
      unlink: vi.fn(() => of({ nodeId: 'n1' })),
      setDontMatch: vi.fn(() => of({ nodeId: 'n1' })),
      candidateImageUrl: (t: string) => `/api/v1/admin/metadata/candidates/${t}/image`,
    };
    TestBed.configureTestingModule({
      imports: [IdentifyDialogComponent],
      providers: [
        provideNoopAnimations(),
        provideRouter([]),
        { provide: MetadataApiService, useValue: api },
        { provide: MAT_DIALOG_DATA, useValue: { nodeId: 'n1' } },
        { provide: MatDialogRef, useValue: dialogRef },
        { provide: MatSnackBar, useValue: snackBar },
      ],
    });
    const fixture = TestBed.createComponent(IdentifyDialogComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const q = (sel: string) => el.querySelector(sel) as HTMLElement | null;
    const render = () => fixture.detectChanges();
    return { fixture, c: fixture.componentInstance, api, dialogRef, snackBar, undo, el, q, render };
  }

  it('shows why it is unavailable and makes no call beyond the context', () => {
    const { api, q } = create(ctx({ fetchAvailable: false, unavailableCode: 'metadata_disabled', unavailableMessage: 'Fetching series information from the web is off.' }));
    expect(api.getIdentifyContext).toHaveBeenCalledWith('n1');
    expect(q('[data-testid="identify-unavailable"]')!.textContent).toContain('is off');
    expect(q('[data-testid="identify-query"]')).toBeNull();
    expect(api.search).not.toHaveBeenCalled();
    expect(api.preview).not.toHaveBeenCalled();
  });

  it('prefills the first suggestion but sends nothing until Search', () => {
    const { c, api, q, render } = create();
    expect(c.query()).toBe('Berserk');
    expect(api.search).not.toHaveBeenCalled();
    c.query.set('  Berserk Deluxe ');
    render();
    (q('[data-testid="identify-search"]') as HTMLButtonElement).click();
    expect(api.search).toHaveBeenCalledWith('n1', 'Berserk Deluxe', 1);
  });

  it('lists ranked results with proxied thumbnails, hit titles and a novel warning', () => {
    const { c, el, q, render } = create();
    c.runSearch();
    render();
    const items = el.querySelectorAll('[data-testid="identify-results"] li');
    expect(items.length).toBe(3);
    expect(items[0].querySelector('img')!.getAttribute('src')).toBe('/api/v1/admin/metadata/candidates/tok1/image');
    expect(items[1].textContent).toContain('matched as “Berserk Counterattack”');
    expect(items[2].textContent).toContain('Novel, not a comic');
    expect(q('.results-head')!.textContent).toContain('4/5000');
    expect(el.textContent).toContain('More results');
  });

  it('pages with More results, appending', () => {
    const { c, api } = create();
    c.runSearch();
    c.moreResults();
    expect(api.search).toHaveBeenLastCalledWith('n1', 'Berserk', 2);
    expect(c.candidates().length).toBe(6);
  });

  it('looks up a pasted reference through the server', () => {
    const { c, api } = create();
    c.reference.set(' mu:njeqwry ');
    c.runLookup();
    expect(api.lookup).toHaveBeenCalledWith('n1', 'mu:njeqwry');
    expect(c.step()).toBe('preview');
  });

  it('offers the ComicInfo hint and previews it', () => {
    const { api, q } = create(ctx({ comicInfoHint: { provider: 'mangaupdates', externalId: '42' } }));
    (q('[data-testid="identify-hint"]') as HTMLButtonElement).click();
    expect(api.preview).toHaveBeenCalledWith('n1', { provider: 'mangaupdates', externalId: '42' });
  });

  it('previews side by side with warnings, the webtoon flag and a no-referrer attribution', () => {
    const { c, el, q, render } = create();
    c.usePreview('mangaupdates', '51239621230', 'Search');
    render();
    expect(el.textContent).toContain('41 items');
    expect(el.textContent).toContain('ComicInfo: “Berserk”');
    expect(el.textContent).toContain('+2 alternative titles');
    expect(q('[data-testid="identify-webtoon"]')!.textContent).toContain('MangaUpdates: webtoon');
    expect(q('[data-warning="count_mismatch"]')).not.toBeNull();
    const a = el.querySelector('a[href^="https://www.mangaupdates.com"]') as HTMLAnchorElement;
    expect(a.getAttribute('rel')).toBe('noopener noreferrer');
    expect(a.getAttribute('referrerpolicy')).toBe('no-referrer');
    expect(el.textContent).toContain('this folder and everything inside');
  });

  it('links, closes with true and offers Undo that restores the previous state', () => {
    const { c, api, dialogRef, snackBar, undo, q, render } = create();
    c.usePreview('mangaupdates', '51239621230', 'Search');
    render();
    (q('[data-testid="identify-link"]') as HTMLButtonElement).click();
    expect(api.link).toHaveBeenCalledWith('n1', { provider: 'mangaupdates', externalId: '51239621230', matchMethod: 'Search', matchScore: 0.97 });
    expect(dialogRef.close).toHaveBeenCalledWith(true);
    expect(snackBar.open).toHaveBeenCalledWith('Linked to Berserk', 'Undo', expect.anything());
    undo.next();
    expect(api.unlink).toHaveBeenCalledWith('n1');
  });

  it('records Reference as the match method for a pasted link', () => {
    const { c, api } = create();
    c.reference.set('mu:1');
    c.runLookup();
    c.link();
    expect((api.link.mock.calls[0] as unknown[])[1]).toMatchObject({ matchMethod: 'Reference' });
  });

  it('shows a typed error with the retry time on backoff', () => {
    const { c, api, q, render } = create();
    api.search.mockReturnValueOnce(throwError(() => ({ error: 'provider_backoff', message: 'The metadata provider is busy.', detail: '2026-09-25T14:05:00Z' })));
    c.runSearch();
    render();
    expect(q('[role="alert"]')!.textContent).toMatch(/busy\. Try again after /);
    expect(c.busy()).toBe(false);
  });

  it('restorePrevious puts back a Don\'t match or an earlier record', () => {
    const { api } = create();
    const svc = api as unknown as MetadataApiService;
    restorePrevious(svc, 'n1', { nodeId: 'n1', state: 'DontMatch', updatedAt: 'x' });
    expect(api.setDontMatch).toHaveBeenCalledWith('n1');
    restorePrevious(svc, 'n1', { nodeId: 'n1', state: 'Confirmed', provider: 'mangaupdates', externalId: '7', matchMethod: 'Search', updatedAt: 'x' });
    expect(api.link).toHaveBeenCalledWith('n1', { provider: 'mangaupdates', externalId: '7', matchMethod: 'Search' });
  });
});
