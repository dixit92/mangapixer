import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { ArtistFolderDialogComponent } from './artist-folder-dialog.component';
import { ARTIST_ROLE_OPTIONS, artistFolderLabel, artistFolderResultMessage, isOwnArtistFolder } from './artist-folder-labels';

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
    function create(folderName: string) {
      const ref = { close: vi.fn() };
      TestBed.configureTestingModule({
        imports: [ArtistFolderDialogComponent],
        providers: [
          provideNoopAnimations(),
          { provide: MatDialogRef, useValue: ref },
          { provide: MAT_DIALOG_DATA, useValue: { folderName } },
        ],
      });
      const fixture = TestBed.createComponent(ArtistFolderDialogComponent);
      fixture.detectChanges();
      const el = fixture.nativeElement as HTMLElement;
      return { fixture, el, ref, c: fixture.componentInstance };
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
  });
});
