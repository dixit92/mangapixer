import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { FolderMatchPreviewDto, IdentifySearchResultDto } from '../../../core/api/api-types';
import { MetadataApiService } from '../metadata-api.service';
import { FolderMatchApiService } from './folder-match-api.service';
import { FolderMatchDialogComponent } from './folder-match-dialog.component';
import {
  MAX_WEB_SEARCHES,
  appliedMessage,
  artistChoiceLabel,
  recordChoiceLabel,
  resultText,
  statusText,
  stopsTheRun,
} from './folder-match-labels';

/** "Match folders by name" (1.38.0): the labels, the preview with tick boxes, the apply, and the opt-in web search. Synthetic names. */
describe('match folders by name', () => {
  describe('labels', () => {
    it('describes the matches', () => {
      expect(artistChoiceLabel({ name: 'Qzv Painter', role: 'author', matchedName: 'Painter Qzv', provider: 'mangaupdates', recordCount: 3 }))
        .toBe('Qzv Painter (as Painter Qzv) - Story & art, 3 series');
      expect(artistChoiceLabel({ name: 'Comic Writer', role: 'artist', matchedName: 'Comic Writer', provider: 'gcd', recordCount: 1 }))
        .toBe('Comic Writer - Art, 1 series, Grand Comics Database');
      expect(recordChoiceLabel({
        provider: 'mangaupdates', externalId: '1', title: 'Moonlit Garden', matchedTitle: 'Tsuki no Niwa', year: 2001,
        providerType: 'Manga', linkedAsSeries: true,
      })).toBe('Moonlit Garden (as Tsuki no Niwa) - 2001, Manga, linked here');
      expect(statusText({ nodeId: 'f', displayName: 'F', status: 'Decided', currentState: 'DontMatch' }, 'Artists'))
        .toBe("Already Don't match - ticking it replaces that");
      expect(statusText({ nodeId: 'f', displayName: 'F', status: 'Ambiguous', records: [] }, 'Collections')).toContain('pick one');
      expect(resultText('record_not_stored')).toBe('The series record is no longer stored');
      expect(resultText('provider_backoff', 'Slow down')).toBe('Slow down');
      expect(appliedMessage('Collections', 2, 1, 5)).toBe('2 collections marked, 1 not - 5 works inside will be matched');
      expect(appliedMessage('Artists', 1, 0, 0)).toBe('1 artist folder marked');
      expect(MAX_WEB_SEARCHES).toBe(50);
    });

    it('stops the web run on backoff, budget, busy, switched off or a failing provider', () => {
      for (const s of [409, 429, 502, 503, 0, undefined]) expect(stopsTheRun(s)).toBe(true);
      for (const s of [400, 404]) expect(stopsTheRun(s)).toBe(false);
    });
  });

  describe('dialog', () => {
    const artistsPreview: FolderMatchPreviewDto = {
      kind: 'Artists',
      compared: 4,
      rows: [
        { nodeId: 'a1', displayName: 'Qzv Painter', status: 'Proposed',
          artists: [{ name: 'Qzv Painter', role: 'author', matchedName: 'Qzv Painter', provider: 'mangaupdates', recordCount: 3 }] },
        { nodeId: 'a2', displayName: 'Shared Name', status: 'Ambiguous', artists: [
          { name: 'Shared Name', role: 'author', matchedName: 'Shared Name', provider: 'mangaupdates', recordCount: 1 },
          { name: 'Other Person', role: 'artist', matchedName: 'Shared Name', provider: 'mangaupdates', recordCount: 3 },
        ] },
        { nodeId: 'a3', displayName: 'Nobody Known', status: 'NoMatch' },
        { nodeId: 'a4', displayName: 'Second Inker', status: 'Decided', currentState: 'DontMatch',
          artists: [{ name: 'Second Inker', role: 'artist', matchedName: 'Second Inker', provider: 'mangaupdates', recordCount: 1 }] },
      ],
    };
    const collectionsPreview: FolderMatchPreviewDto = {
      kind: 'Collections',
      compared: 9,
      rows: [
        { nodeId: 'c1', displayName: 'Tsuki no Niwa', status: 'Proposed', records: [
          { provider: 'mangaupdates', externalId: '1001', title: 'Moonlit Garden', matchedTitle: 'Tsuki no Niwa', linkedAsSeries: true }] },
        { nodeId: 'c2', displayName: 'Unknown One [Scans]', status: 'NoMatch', searchText: 'Unknown One' },
        { nodeId: 'c3', displayName: 'Unknown Two', status: 'NoMatch', searchText: 'Unknown Two' },
      ],
    };

    function create(preview: FolderMatchPreviewDto) {
      const ref = { close: vi.fn() };
      const api = {
        preview: vi.fn(() => of(preview)),
        apply: vi.fn((req: { items: { nodeId: string }[] }) => of({
          kind: preview.kind, succeeded: req.items.length, failed: 0,
          results: req.items.map((i) => ({ nodeId: i.nodeId, code: 'ok', queued: 2 })),
        })),
      };
      const searchResult: IdentifySearchResultDto = {
        provider: 'mangaupdates', page: 1, totalHits: 1, budgetUsedToday: 1, dailyBudget: 5000,
        candidates: [{ externalId: '777', title: 'Unknown One Series', year: 2010, score: 0.9, strength: 'Strong' }],
      };
      const metadata = {
        search: vi.fn(() => of(searchResult)),
        setCollection: vi.fn(() => of({ change: { nodeId: 'c2' }, queued: 1 })),
      };
      TestBed.configureTestingModule({
        imports: [FolderMatchDialogComponent],
        providers: [
          provideNoopAnimations(),
          { provide: MatDialogRef, useValue: ref },
          { provide: MAT_DIALOG_DATA, useValue: { folders: preview.rows.map((r) => ({ id: r.nodeId, displayName: r.displayName })), ignored: 1 } },
          { provide: FolderMatchApiService, useValue: api },
          { provide: MetadataApiService, useValue: metadata },
        ],
      });
      const fixture = TestBed.createComponent(FolderMatchDialogComponent);
      fixture.componentInstance.paceMs = 0;
      fixture.detectChanges();
      const el = fixture.nativeElement as HTMLElement;
      return { fixture, el, ref, api, metadata, c: fixture.componentInstance };
    }

    it('changes nothing before the kind is chosen, then previews with tick boxes', () => {
      const { el, api, c, fixture } = create(artistsPreview);
      expect(api.preview).not.toHaveBeenCalled();
      expect(el.textContent).toContain('4 folders selected');
      expect(el.textContent).toContain('1 archive ignored');
      expect(el.querySelector('[data-testid="folder-match-explain"]')!.textContent).toContain('nothing is sent');

      c.choose('Artists');
      fixture.detectChanges();
      expect(api.preview).toHaveBeenCalledWith({ kind: 'Artists', nodeIds: ['a1', 'a2', 'a3', 'a4'] });
      expect(el.querySelector('[data-testid="folder-match-summary"]')!.textContent)
        .toContain('1 matched, 1 with several matches, 1 without a match, 1 already decided');
      // Proposed ticked; ambiguous, no match and decided unticked.
      expect(c.rows().map((r) => r.ticked)).toEqual([true, false, false, false]);
      expect(el.querySelector('[data-testid="folder-match-apply"]')!.textContent).toContain('Mark 1 folder');
      expect(api.apply).not.toHaveBeenCalled();
    });

    it('applies the proposal, a picked ambiguous artist and an opted-in own name - and returns what was marked', async () => {
      const { el, api, c, ref, fixture } = create(artistsPreview);
      c.choose('Artists');
      fixture.detectChanges();
      c.choosePick(c.rows()[1], 1);
      c.setOwnName(c.rows()[2], true);
      fixture.detectChanges();
      expect(el.querySelector('[data-testid="folder-match-apply"]')!.textContent).toContain('Mark 3 folders');

      await c.apply();
      fixture.detectChanges();
      expect(api.apply).toHaveBeenCalledWith({
        kind: 'Artists',
        items: [
          { nodeId: 'a1', name: 'Qzv Painter', role: 'author' },
          { nodeId: 'a2', name: 'Other Person', role: 'artist' },
          { nodeId: 'a3' },
        ],
        setDoujinContent: true,
      });
      expect(el.querySelector('[data-testid="folder-match-result-a1"]')!.textContent).toContain('Marked');
      (el.querySelector('[data-testid="folder-match-close"]') as HTMLButtonElement).click();
      expect(ref.close).toHaveBeenCalledWith({ kind: 'Artists', marked: ['a1', 'a2', 'a3'], failed: 0, queued: 6 });
    });

    it('ticking a decided folder replaces its state with the listed match', async () => {
      const { api, c, fixture } = create(artistsPreview);
      c.choose('Artists');
      fixture.detectChanges();
      c.tick(c.rows()[3], true);
      c.tick(c.rows()[0], false);
      await c.apply();
      expect(api.apply).toHaveBeenCalledWith(expect.objectContaining({ items: [{ nodeId: 'a4', name: 'Second Inker', role: 'artist' }] }));
    });

    it('sends nothing to the web unless "Search the web for the rest" is ticked, then only the ticked rows, with the text shown', async () => {
      const { el, metadata, c, fixture } = create(collectionsPreview);
      c.choose('Collections');
      fixture.detectChanges();
      expect(el.querySelector('[data-testid="folder-match-web-section"]')).not.toBeNull();
      expect(el.querySelector('[data-testid="folder-match-web-text-c2"]')).toBeNull(); // shown once opted in
      await c.search();
      expect(metadata.search).not.toHaveBeenCalled();

      c.setWeb(true);
      fixture.detectChanges();
      const text = el.querySelector('[data-testid="folder-match-web-text-c2"]') as HTMLInputElement;
      await fixture.whenStable();
      expect(text.value).toBe('Unknown One');
      c.patch(c.rows()[2], { webTicked: false });
      c.patch(c.rows()[1], { webText: 'Unknown One Edited' });
      fixture.detectChanges();
      await c.search();
      fixture.detectChanges();
      expect(metadata.search).toHaveBeenCalledTimes(1);
      expect(metadata.search).toHaveBeenCalledWith('c2', 'Unknown One Edited', 1, true, 'mangaupdates');
      // A web result is only offered.
      expect(c.rows()[1].webPick).toBe(-1);
      expect(c.applyCount()).toBe(1); // the local proposal only
      expect(el.querySelector('[data-testid="folder-match-web-note"]')!.textContent).toContain('1 search sent');

      c.chooseWeb(c.rows()[1], 0);
      await c.apply();
      expect(metadata.setCollection).toHaveBeenCalledWith('c2',
        { provider: 'mangaupdates', externalId: '777', matchMethod: 'Search', setDoujinContent: true });
    });

    it('searches at most 50 per run and stops on the provider backoff', async () => {
      const rows = Array.from({ length: 53 }, (_, i) => ({
        nodeId: 'n' + i, displayName: 'Name ' + i, status: 'NoMatch' as const, searchText: 'Name ' + i,
      }));
      const { metadata, c, fixture } = create({ kind: 'Collections', compared: 0, rows });
      c.choose('Collections');
      c.setWeb(true);
      fixture.detectChanges();
      expect(c.webQueueLabel()).toBe('the first 50 of 53');
      await c.search();
      expect(metadata.search).toHaveBeenCalledTimes(50);
      expect(c.webQueue().length).toBe(3);

      metadata.search.mockImplementation(() => throwError(() => ({ status: 503, error: 'provider_backoff', message: 'Asked to slow down.' })));
      await c.search();
      expect(metadata.search).toHaveBeenCalledTimes(51);
      expect(c.webNote()).toBe('Stopped: Asked to slow down.');
      expect(c.webQueue().length).toBe(2); // the rest wait for another run
    });
  });
});
