import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { AuthorSearchResultDto } from '../../../core/api/api-types';

import { ArtistFolderDialogComponent } from './artist-folder-dialog.component';
import { ARTIST_ROLE_OPTIONS, artistFolderLabel, artistFolderResultMessage, isOwnArtistFolder } from './artist-folder-labels';
import { AuthorSearchApiService } from './author-search-api.service';
import { authorCandidateDetails, authorCandidateName, authorPageUrl } from './author-search-labels';

/** "Artist folder" (1.37.0): the labels and the small dialog that asks for the artist (name + role) before a folder is marked. */
describe('artist folder', () => {
  describe('labels', () => {
    it('names the artist when known', () => {
      expect(artistFolderLabel(' Beta Painter ')).toBe('Artist folder: Beta Painter');
      expect(artistFolderLabel(null)).toBe('Artist folder');
      expect(isOwnArtistFolder({ state: 'ArtistFolder' })).toBe(true);
      expect(isOwnArtistFolder({ state: 'CollectionAbout' })).toBe(false);
    });

    it('says what happens to the works inside', () => {
      const change = { nodeId: 'f1' };
      expect(artistFolderResultMessage({ change, artist: { name: 'Beta Painter', role: 'author' }, queued: 3 }))
        .toBe('Artist folder: Beta Painter - 3 works inside will be matched');
      expect(artistFolderResultMessage({ change, artist: { name: 'Beta Painter', role: 'author' }, queued: 0 }))
        .toBe('Artist folder: Beta Painter - the works inside are matched when automatic matching runs');
    });

    it('offers the declared creator roles, Story & art first', () => {
      expect(ARTIST_ROLE_OPTIONS.map((o) => o.value)).toEqual(['author', 'writer', 'artist']);
      expect(ARTIST_ROLE_OPTIONS[0].label).toBe('Story & art');
    });
  });

  describe('dialog', () => {
    const found: AuthorSearchResultDto = {
      total: 2,
      candidates: [
        { provider: 'mangaupdates', externalId: '4815162342', name: 'Beta Painter', hitName: null, totalSeries: 12, genres: ['Drama'],
          siteUrl: 'https://www.mangaupdates.com/author/abc/beta-painter' },
        { provider: 'mangaupdates', externalId: '77', name: 'Painter Beta', hitName: 'B. Painter', totalSeries: null, genres: [] },
      ],
    };

    function create(folderName: string, nodeId?: string) {
      const ref = { close: vi.fn() };
      const authors = { search: vi.fn(() => of(found)) };
      TestBed.configureTestingModule({
        imports: [ArtistFolderDialogComponent],
        providers: [
          provideNoopAnimations(),
          { provide: MatDialogRef, useValue: ref },
          { provide: MAT_DIALOG_DATA, useValue: { folderName, nodeId } },
          { provide: AuthorSearchApiService, useValue: authors },
        ],
      });
      const fixture = TestBed.createComponent(ArtistFolderDialogComponent);
      fixture.detectChanges();
      const el = fixture.nativeElement as HTMLElement;
      return { fixture, el, ref, authors, c: fixture.componentInstance };
    }

    it('starts with the folder name and "Story & art", and returns what was chosen', () => {
      const { el, ref, c, fixture } = create('Beta Painter');
      expect(el.textContent).toContain('Mark Beta Painter as one artist\'s folder.');
      expect(el.querySelector('[data-testid="artist-folder-explain"]')!.textContent).toContain('never sent');
      expect(c.name()).toBe('Beta Painter');
      expect(c.role()).toBe('author');

      c.name.set('  Gamma Inker ');
      c.role.set('artist');
      fixture.detectChanges();
      (el.querySelector('[data-testid="artist-folder-save"]') as HTMLButtonElement).click();
      expect(ref.close).toHaveBeenCalledWith({ name: 'Gamma Inker', role: 'artist' });
    });

    it('an empty name means the folder\'s own name (decided on the server)', () => {
      const { el, ref, c, fixture } = create('');
      expect(el.textContent).toContain('Mark this folder as one artist\'s folder.');
      c.name.set('   ');
      fixture.detectChanges();
      (el.querySelector('[data-testid="artist-folder-save"]') as HTMLButtonElement).click();
      expect(ref.close).toHaveBeenCalledWith({ name: null, role: 'author' });
    });

    it('refuses a name longer than the server takes', () => {
      const { el, ref, c, fixture } = create('Beta Painter');
      c.name.set('x'.repeat(201));
      fixture.detectChanges();
      expect((el.querySelector('[data-testid="artist-folder-save"]') as HTMLButtonElement).disabled).toBe(true);
      c.save();
      expect(ref.close).not.toHaveBeenCalled();
    });

    describe('1.45.0: Find on MangaUpdates', () => {
      it('is offered only with a folder id and sends nothing until pressed', () => {
        const without = create('Beta Painter');
        expect(without.el.querySelector('[data-testid="artist-folder-find"]')).toBeNull();
        TestBed.resetTestingModule();
        const { el, authors } = create('Beta Painter', 'f1');
        expect(el.querySelector('[data-testid="artist-folder-find"]')).not.toBeNull();
        expect(el.querySelector('[data-testid="artist-folder-find-section"]')!.textContent).toContain("only the text in the Artist field");
        expect(authors.search).not.toHaveBeenCalled();
      });

      it('searches the name field once, offers the authors, and a pick returns its MangaUpdates id with the mark', async () => {
        const { el, ref, authors, c, fixture } = create('Beta Painter', 'f1');
        c.name.set('  Beta   Painter ');
        fixture.detectChanges();
        (el.querySelector('[data-testid="artist-folder-find"]') as HTMLButtonElement).click();
        await fixture.whenStable();
        fixture.detectChanges();
        expect(authors.search).toHaveBeenCalledTimes(1);
        expect(authors.search).toHaveBeenCalledWith('f1', 'Beta Painter');
        expect(el.querySelector('[data-testid="artist-folder-candidates"]')!.textContent).toContain('Painter Beta (as B. Painter)');
        expect(c.pick()).toBe(-1); // nothing chosen automatically

        c.choose(1);
        c.role.set('artist');
        fixture.detectChanges();
        expect(c.name()).toBe('Painter Beta');
        (el.querySelector('[data-testid="artist-folder-save"]') as HTMLButtonElement).click();
        expect(ref.close).toHaveBeenCalledWith({ name: 'Painter Beta', role: 'artist', provider: 'mangaupdates', externalId: '77' });
      });

      it('editing the name away from the picked author drops the pick', () => {
        const { ref, c } = create('Beta Painter', 'f1');
        c.candidates.set(found.candidates);
        c.choose(0);
        c.name.set('Someone Else');
        c.save();
        expect(ref.close).toHaveBeenCalledWith({ name: 'Someone Else', role: 'author' });
      });

      it('an empty field searches the folder name; a refusal is shown and nothing is marked', async () => {
        const { el, authors, c, fixture, ref } = create('Gamma Inker', 'f2');
        authors.search.mockImplementationOnce(() => throwError(() => ({ status: 409, error: 'metadata_disabled', message: 'Fetching is off.' })));
        c.name.set('');
        await c.find();
        fixture.detectChanges();
        expect(authors.search).toHaveBeenCalledWith('f2', 'Gamma Inker');
        expect(el.querySelector('[data-testid="artist-folder-find-error"]')!.textContent).toContain('Fetching is off.');
        expect(ref.close).not.toHaveBeenCalled();
      });

      it('candidate texts', () => {
        expect(authorCandidateName(found.candidates[0])).toBe('Beta Painter');
        expect(authorCandidateDetails(found.candidates[0])).toBe('12 series - Drama');
        expect(authorCandidateDetails(found.candidates[1])).toBe('');
        expect(authorPageUrl(found.candidates[0])).toBe('https://www.mangaupdates.com/author/abc/beta-painter');
        expect(authorPageUrl({ ...found.candidates[0], siteUrl: 'http://www.mangaupdates.com/x' })).toBeNull();
        expect(authorPageUrl({ ...found.candidates[0], siteUrl: 'https://mangaupdates.com.evil.example/x' })).toBeNull();
      });
    });
  });
});
