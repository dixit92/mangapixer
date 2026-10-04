import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Subject, of, throwError } from 'rxjs';

import { IdentifyContextDto, IdentifyPreviewDto, IdentifySearchResultDto } from '../../../core/api/api-types';
import { MetadataApiService } from '../metadata-api.service';
import { MetadataStateService } from '../metadata-state.service';
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
    local: { displayName: '[Grp] Berserk (1989)', itemCount: 41, comicInfoSeries: 'Berserk', tallStrips: false, yearHint: 1989, coverUrl: '/api/v1/items/a1/cover' },
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
    providerType: 'Manga',
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

  function create(context: IdentifyContextDto = ctx(), mode: 'link' | 'collection' = 'link') {
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
      setCollection: vi.fn(() => of({
        change: { nodeId: 'n1', link: { nodeId: 'n1', state: 'CollectionAbout', updatedAt: 'x' }, previous: null }, contentSet: true, queued: 34,
      })),
      candidateImageUrl: (t: string) => `/api/v1/admin/metadata/candidates/${t}/image`,
    };
    const state = { announce: vi.fn(), refresh: vi.fn() };
    TestBed.configureTestingModule({
      imports: [IdentifyDialogComponent],
      providers: [
        provideNoopAnimations(),
        provideRouter([]),
        { provide: MetadataApiService, useValue: api },
        { provide: MAT_DIALOG_DATA, useValue: { nodeId: 'n1', mode } },
        { provide: MatDialogRef, useValue: dialogRef },
        { provide: MatSnackBar, useValue: snackBar },
        { provide: MetadataStateService, useValue: state },
      ],
    });
    const fixture = TestBed.createComponent(IdentifyDialogComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const q = (sel: string) => el.querySelector(sel) as HTMLElement | null;
    const render = () => fixture.detectChanges();
    return { fixture, c: fixture.componentInstance, api, state, dialogRef, snackBar, undo, el, q, render };
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
    expect(api.search).toHaveBeenCalledWith('n1', 'Berserk Deluxe', 1, true);
  });

  it('hides doujinshi and novels by default; unticking sends the search without the filter', () => {
    const { c, api, q, render } = create();
    const box = q('[data-testid="identify-hide-doujinshi"] input') as HTMLInputElement;
    expect(box.checked).toBe(true);
    box.click();
    render();
    expect(c.hideDoujinshi()).toBe(false);
    c.runSearch();
    c.moreResults(); // later pages keep the filter the search was made with
    expect(api.search).toHaveBeenNthCalledWith(1, 'n1', 'Berserk', 1, false);
    expect(api.search).toHaveBeenNthCalledWith(2, 'n1', 'Berserk', 2, false);
  });

  it('starts with the filter off in a doujinshi folder (Content "Doujinshi & adult one-shots")', () => {
    const { c, api, q } = create(ctx({ doujinshiContent: true }));
    expect((q('[data-testid="identify-hide-doujinshi"] input') as HTMLInputElement).checked).toBe(false);
    c.runSearch();
    expect(api.search).toHaveBeenCalledWith('n1', 'Berserk', 1, false);
  });

  it('keeps a long name from widening the dialog: full name in the title tooltip', () => {
    const long = 'A Very Long Archive Name v00 (2008) [Some Scan Team] [OneShot] Extra Words To Overflow.cbz';
    const { q } = create(ctx({ displayName: long, suggestions: [long] }));
    expect(q('.title')!.getAttribute('title')).toBe(long);
    expect(q('.suggestions .chip')!.getAttribute('title')).toBe(long);
  });

  it('lists ranked results with proxied thumbnails, hit titles and a novel warning', () => {
    const { c, el, q, render } = create();
    c.runSearch();
    render();
    const items = el.querySelectorAll('[data-testid="identify-results"] li');
    expect(items.length).toBe(3);
    expect(items[0].querySelector('img')!.getAttribute('src')).toBe('/api/v1/admin/metadata/candidates/tok1/image');
    expect(items[0].textContent).toContain('Strong 97%'); // owner decision, 1.27.0: whole percent, not a raw 0-1 score
    expect(items[1].textContent).toContain('matched as “Berserk Counterattack”');
    expect(items[2].textContent).toContain('Novel, not a comic');
    expect(q('.results-head')!.textContent).toContain('4/5000');
    expect(el.textContent).toContain('More results');
  });

  it('pages with More results, appending', () => {
    const { c, api } = create();
    c.runSearch();
    c.moreResults();
    expect(api.search).toHaveBeenLastCalledWith('n1', 'Berserk', 2, true);
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

  it('lists the alternative titles, folding a long list behind "+N more"', () => {
    const { c, api, q, render } = create();
    const many = Array.from({ length: 9 }, (_, i) => `Alt Title ${i + 1}`);
    api.preview.mockReturnValueOnce(of({ ...preview, altTitles: many }));
    c.usePreview('mangaupdates', '51239621230', 'Search');
    render();
    expect(q('[data-testid="identify-alt-titles"]')!.textContent).toContain('Alt Title 6');
    expect(q('[data-testid="identify-alt-titles"]')!.textContent).not.toContain('Alt Title 7');
    (q('[data-testid="identify-alt-more"]') as HTMLButtonElement).click();
    render();
    expect(q('[data-testid="identify-alt-titles"]')!.textContent).toContain('Alt Title 9');
    expect(q('[data-testid="identify-alt-more"]')).toBeNull();
  });

  it('previews side by side with warnings, the webtoon flag and a no-referrer attribution', () => {
    const { c, el, q, render } = create();
    c.usePreview('mangaupdates', '51239621230', 'Search');
    render();
    expect(el.textContent).toContain('41 items');
    expect(el.textContent).toContain('ComicInfo: “Berserk”');
    expect(q('[data-testid="identify-alt-titles"]')!.textContent).toContain('also: Beruseruku, Berserk: The Black Swordsman');
    expect(q('[data-testid="identify-alt-more"]')).toBeNull(); // two titles: nothing folded
    expect(q('[data-testid="identify-local-cover"]')!.getAttribute('src')).toBe('/api/v1/items/a1/cover');
    expect(q('[data-testid="identify-webtoon"]')!.textContent).toContain('MangaUpdates: webtoon');
    expect(q('[data-warning="count_mismatch"]')).not.toBeNull();
    const a = el.querySelector('a[href^="https://www.mangaupdates.com"]') as HTMLAnchorElement;
    expect(a.getAttribute('rel')).toBe('noopener noreferrer');
    expect(a.getAttribute('referrerpolicy')).toBe('no-referrer');
    expect(el.textContent).toContain('this folder and everything inside');
    expect(el.textContent).toContain('Manga · Japan · 1989'); // the provider type, like the results list
    expect(el.textContent).not.toContain('Comic ·');
    expect(el.textContent).toContain('Title match: Strong 97%'); // labelled: title similarity only, distinct from a review row's Overall score
  });

  it('links, closes with true and offers Undo that restores the previous state', () => {
    const { c, api, state, dialogRef, snackBar, undo, q, render } = create();
    c.usePreview('mangaupdates', '51239621230', 'Search');
    render();
    (q('[data-testid="identify-link"]') as HTMLButtonElement).click();
    expect(state.announce).toHaveBeenCalledWith('n1', true); // the card (i) + top bar update in place
    expect(api.link).toHaveBeenCalledWith('n1', { provider: 'mangaupdates', externalId: '51239621230', matchMethod: 'Search', matchScore: 0.97 });
    expect(dialogRef.close).toHaveBeenCalledWith(true);
    expect(snackBar.open).toHaveBeenCalledWith('Linked to Berserk', 'Undo', expect.anything());
    undo.next();
    expect(api.unlink).toHaveBeenCalledWith('n1');
    expect(state.refresh).toHaveBeenCalledWith('n1'); // the restored state is re-derived from series-info
  });

  it('keeps the search score of the candidate a preview came from', () => {
    const { c, api } = create();
    c.runSearch();
    c.usePreview('mangaupdates', '2', 'Search', results.candidates[1]);
    expect(c.match()).toEqual({ score: 0.5, strength: 'Weak' });
    c.link();
    expect((api.link.mock.calls[0] as unknown[])[1]).toMatchObject({ matchMethod: 'Search', matchScore: 0.5 });
  });

  it('replaces a thumbnail that fails to load with the placeholder', () => {
    const { c, el, render } = create();
    c.runSearch();
    render();
    c.imageFailed('tok1');
    render();
    const first = el.querySelectorAll('[data-testid="identify-results"] li')[0];
    expect(first.querySelector('img')).toBeNull();
    expect(first.querySelector('.thumb.empty')).not.toBeNull();
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

  // --- 1.32.0: the site switch (MangaUpdates | Grand Comics Database) ---

  const sites = [
    { id: 'mangaupdates', name: 'MangaUpdates', available: true },
    { id: 'gcd', name: 'Grand Comics Database', available: true, note: 'Comics and graphic novels. Answers about 25 requests an hour.' },
  ];
  const comicsCtx = () => ctx({
    displayName: 'Bone (1991)', provider: 'gcd', providerName: 'Grand Comics Database', comicsSignalled: true, sites,
    local: { displayName: 'Bone (1991)', itemCount: 2, yearHint: 1991 },
  });
  const gcdResults: IdentifySearchResultDto = {
    provider: 'gcd', page: 1, totalHits: 3, budgetUsedToday: 1, dailyBudget: 5000,
    candidates: [{ externalId: '4347', title: 'Bone', providerType: 'was ongoing series', origin: 'EnglishOriginal', year: 1991,
      language: 'en', unitCount: 20, unitKind: 'issues', score: 1, strength: 'Strong' }],
  };

  it('shows no site switch with a single site', () => {
    const { q } = create();
    expect(q('[data-testid="identify-site"]')).toBeNull();
  });

  it('starts on the comics site for a comics folder, with its pace note and the start-year option', () => {
    const { c, q } = create(comicsCtx());
    expect(c.site()).toBe('gcd');
    expect(q('[data-testid="identify-site"]')).not.toBeNull();
    expect(q('[data-testid="identify-site-note"]')!.textContent).toContain('25 requests an hour');
    expect(q('[data-testid="identify-start-year"]')!.textContent).toContain('Only series that began in 1991');
    expect(q('[data-testid="identify-hide-doujinshi"]')).toBeNull();
    expect(q('mat-label')!.textContent).toContain('Search Grand Comics Database');
  });

  it('searches the comics site with the name\'s start year; unticked, without it; previews from that site', () => {
    const { c, api, render } = create(comicsCtx());
    api.search.mockReturnValue(of(gcdResults));
    c.runSearch();
    expect(api.search).toHaveBeenLastCalledWith('n1', 'Berserk', 1, false, 'gcd', 1991);
    render();
    c.usePreview(c.resultsSite(), '4347', 'Search', gcdResults.candidates[0]);
    expect(api.preview).toHaveBeenCalledWith('n1', { provider: 'gcd', externalId: '4347' });
    c.back();
    c.useStartYear.set(false);
    c.runSearch();
    expect(api.search).toHaveBeenLastCalledWith('n1', 'Berserk', 1, false, 'gcd', null);
  });

  it('switching to MangaUpdates clears the comics results and searches MangaUpdates with its type filter', () => {
    const { c, api, render } = create(comicsCtx());
    api.search.mockReturnValueOnce(of(gcdResults));
    c.runSearch();
    render();
    expect(c.candidates().length).toBe(1);
    c.chooseSite('mangaupdates');
    render();
    expect(c.candidates()).toEqual([]);
    c.runSearch();
    expect(api.search).toHaveBeenLastCalledWith('n1', 'Berserk', 1, true);
  });

  it('a removed site is shown but cannot be chosen; the dialog stays usable on the other one', () => {
    const { c, q } = create(ctx({
      fetchAvailable: false, unavailableCode: 'provider_not_allowed', provider: 'gcd',
      sites: [sites[0], { ...sites[1], available: false, unavailableCode: 'provider_not_allowed' }],
    }));
    expect(q('[data-testid="identify-unavailable"]')).toBeNull();
    expect(c.site()).toBe('mangaupdates');
    const gcd = q('[data-site="gcd"] button') as HTMLButtonElement;
    expect(gcd.disabled).toBe(true);
    expect(c.siteUnavailable({ ...sites[1], available: false, unavailableCode: 'provider_not_allowed' })).toContain('allowlist');
  });

  it('shows the CC BY-SA credit of a comics preview', () => {
    const { c, api, q, render } = create(comicsCtx());
    api.preview.mockReturnValueOnce(of({ ...preview, provider: 'gcd', providerName: 'Grand Comics Database',
      credit: 'Data: Grand Comics Database, CC BY-SA 4.0', siteUrl: 'https://www.comics.org/series/4347/' }));
    c.usePreview('gcd', '4347', 'Search');
    render();
    expect(q('[data-testid="identify-credit"]')!.textContent).toContain('CC BY-SA 4.0');
    expect(q('[data-testid="identify-credit"]')!.textContent).toContain('keeps its own cover');
  });

  it('a slow comics site answers "busy" with the time to try again', () => {
    const { c, api, q, render } = create(comicsCtx());
    api.search.mockReturnValueOnce(throwError(() => ({ error: 'provider_busy', message: 'Grand Comics Database answers only about 25 requests an hour.', detail: '2026-10-02T14:05:00Z' })));
    c.runSearch();
    render();
    expect(q('[role="alert"]')!.textContent).toMatch(/25 requests an hour\. Try again after /);
  });

  it('1.34.0: the collection mode marks the folder "Collection about" the previewed series, with the Content box and Undo', () => {
    const { c, api, state, dialogRef, snackBar, undo, q, render } = create(ctx(), 'collection');
    expect(q('[data-testid="identify-collection-title"]')!.textContent).toContain('pick the series');
    expect(c.hideDoujinshi()).toBe(true);
    c.usePreview('mangaupdates', '51239621230', 'Search');
    render();
    expect(q('[data-testid="identify-link"]')).toBeNull();
    expect(q('[data-testid="identify-collection-content"]')).not.toBeNull();
    (q('[data-testid="identify-set-collection"]') as HTMLButtonElement).click();
    expect(api.setCollection).toHaveBeenCalledWith('n1',
      { provider: 'mangaupdates', externalId: '51239621230', matchMethod: 'Search', setDoujinContent: true });
    expect(api.link).not.toHaveBeenCalled();
    expect(state.announce).toHaveBeenCalledWith('n1', true);
    expect(dialogRef.close).toHaveBeenCalledWith(true);
    expect(snackBar.open).toHaveBeenCalledWith(
      'Collection about Berserk - 34 works inside will be matched - Content set to Doujinshi & adult one-shots', 'Undo', expect.anything());
    undo.next();
    expect(api.unlink).toHaveBeenCalledWith('n1');
  });

  it('1.34.0: in a doujinshi folder the collection mode keeps doujinshi hidden and asks nothing about the Content', () => {
    const { c, api, q, render } = create(ctx({ doujinshiContent: true }), 'collection');
    expect(c.hideDoujinshi()).toBe(true);
    c.usePreview('mangaupdates', '51239621230', 'Search');
    render();
    expect(q('[data-testid="identify-collection-content"]')).toBeNull();
    (q('[data-testid="identify-set-collection"]') as HTMLButtonElement).click();
    expect(api.setCollection).toHaveBeenCalledWith('n1', expect.objectContaining({ setDoujinContent: false }));
  });

  it('restorePrevious puts back a collection (1.34.0) without touching the Content', () => {
    const { api } = create();
    restorePrevious(api as unknown as MetadataApiService, 'n1',
      { nodeId: 'n1', state: 'CollectionAbout', provider: 'mangaupdates', externalId: '7', matchMethod: 'Search', updatedAt: 'x' });
    expect(api.setCollection).toHaveBeenCalledWith('n1', { provider: 'mangaupdates', externalId: '7', matchMethod: 'Search', setDoujinContent: false });
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
