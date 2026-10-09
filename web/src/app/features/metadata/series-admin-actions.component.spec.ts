import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MatSnackBar, MatSnackBarRef, TextOnlySnackBar } from '@angular/material/snack-bar';
import { Subject, of, throwError } from 'rxjs';

import { FolderMetadataContentDto, IdentifyContextDto, SeriesInfoDto } from '../../core/api/api-types';
import { IdentifyDialogService } from './identify-dialog/identify-dialog.service';
import { MangaDexMatchDialogService } from './companion/mangadex-match-dialog.service';
import { MetadataApiService } from './metadata-api.service';
import { MetadataStateService } from './metadata-state.service';
import { SeriesAdminActionsComponent } from './series-admin-actions.component';
import { CoverPickerDialogService } from '../../shared/cover-picker/cover-picker-dialog.service';
import { seriesInfo } from './series-info.testing';
import { ArtistFolderDialogService } from './artist-folder/artist-folder-dialog.service';
import { DeclaredFactsApiService } from './declared/declared-facts-api.service';

@Component({
  standalone: true,
  imports: [SeriesAdminActionsComponent],
  template: `<app-series-admin-actions [info]="info()" (changed)="changes = changes + 1" />`,
})
class HostComponent {
  readonly info = signal<SeriesInfoDto>(seriesInfo());
  changes = 0;
}

/**
 * Admin actions for one node (1.24.0): Identify (lane B2; enabled only when the web
 * switches allow it, checked with no network when the menu opens) and Refresh,
 * Don't match / Clear, Unlink for an own link, precedence for folders only.
 */
describe('SeriesAdminActionsComponent', () => {
  const context = (fetchAvailable: boolean): Partial<IdentifyContextDto> => ({
    fetchAvailable,
    unavailableCode: fetchAvailable ? null : 'library_metadata_disabled',
    unavailableMessage: fetchAvailable ? null : 'Fetching series information from the web is off for this library.',
  });

  function create(info: SeriesInfoDto, fetchAvailable = false, content: FolderMetadataContentDto | null = null) {
    const dialog = { open: vi.fn(() => Promise.resolve(true)) };
    const api = {
      getIdentifyContext: vi.fn(() => of(context(fetchAvailable))),
      refresh: vi.fn(() => of({ state: 'Ok', fetchedAt: '2026-09-25T00:00:00Z', imageUpdated: false })),
      setDontMatch: vi.fn(() => of({ nodeId: info.nodeId })),
      clearDontMatch: vi.fn(() => of({ nodeId: info.nodeId })),
      unlink: vi.fn(() => of({ nodeId: info.nodeId })),
      clearCollection: vi.fn(() => of({ nodeId: info.nodeId })),
      setArtistFolder: vi.fn(() => of({ change: { nodeId: info.nodeId }, artist: { name: 'Beta Painter', role: 'author' }, queued: 2 })),
      clearArtistFolder: vi.fn(() => of({ nodeId: info.nodeId })),
      setFolderPrecedence: vi.fn(() => of({ nodeId: info.nodeId, precedence: 'WebFirst' })),
      clearFolderPrecedence: vi.fn(() => of(undefined)),
      // Stage 2 Content setting; null = a server without it (501).
      getFolderContent: vi.fn(() => (content ? of(content) : throwError(() => ({ status: 501 })))),
      setFolderContent: vi.fn((id: string, value: string) => of({ nodeId: id, content: value, effective: value })),
      clearFolderContent: vi.fn((id: string) => of({ nodeId: id, content: null, effective: 'Auto' })),
    };
    const state = { announce: vi.fn(), refresh: vi.fn() };
    const mangaDex = { open: vi.fn(() => Promise.resolve(true)) };
    const coverPicker = { open: vi.fn(() => Promise.resolve({ mode: 'FilePinned', imageUrl: '/api/v1/items/a1/cover?v=1' })) };
    const artist = { open: vi.fn(() => Promise.resolve({ name: 'Beta Painter', role: 'author' })) };
    const declaredApi = { version: signal(0) };
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        provideNoopAnimations(),
        { provide: MetadataApiService, useValue: api },
        { provide: IdentifyDialogService, useValue: dialog },
        { provide: MetadataStateService, useValue: state },
        { provide: MangaDexMatchDialogService, useValue: mangaDex },
        { provide: CoverPickerDialogService, useValue: coverPicker },
        { provide: ArtistFolderDialogService, useValue: artist },
        { provide: DeclaredFactsApiService, useValue: declaredApi },
      ],
    });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.info.set(info);
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('[data-testid="series-admin-menu"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    return { fixture, api, dialog, state, mangaDex, coverPicker, artist, declaredApi };
  }

  const item = (sel: string) => document.querySelector(sel) as HTMLButtonElement | null;

  it('disables Identify with the reason when web lookups are off (checked without network)', () => {
    const { api } = create(seriesInfo({ nodeId: 'f1' }));
    expect(api.getIdentifyContext).toHaveBeenCalledWith('f1');
    expect(item('[data-slot="identify"]')!.disabled).toBe(true);
    expect(document.querySelector('[data-testid="identify-why"]')!.textContent).toContain('off for this library');
  });

  it('opens the identify dialog when allowed and reports a link', async () => {
    const { fixture, dialog } = create(seriesInfo({ nodeId: 'f1' }), true);
    fixture.detectChanges();
    const identify = item('[data-slot="identify"]')!;
    expect(identify.disabled).toBe(false);
    identify.click();
    expect(dialog.open).toHaveBeenCalledWith('f1');
    await Promise.resolve();
    await Promise.resolve();
    expect(fixture.componentInstance.changes).toBe(1);
  });

  it('opens the cover picker for the node (1.29.0) and reports a change', async () => {
    const { fixture, coverPicker } = create(seriesInfo({ nodeId: 'f1', anchorNodeId: 'f1', anchorDisplayName: 'Series Folder' }));
    item('[data-testid="choose-cover"]')!.click();
    expect(coverPicker.open).toHaveBeenCalledWith('f1', 'Series Folder');
    await Promise.resolve();
    await Promise.resolve();
    expect(fixture.componentInstance.changes).toBe(1);
  });

  it('offers Refresh when a web record applies', () => {
    const web = { provider: 'mangaupdates', providerName: 'MangaUpdates', fetchedAt: '2026-09-20T00:00:00Z', hasImage: false };
    const { fixture, api } = create(seriesInfo({ nodeId: 'a1', state: 'Web', web }), true);
    fixture.detectChanges();
    item('[data-testid="refresh"]')!.click();
    expect(api.refresh).toHaveBeenCalledWith('a1');
    expect(fixture.componentInstance.changes).toBe(1);
  });

  it('has no Refresh without a web record', () => {
    create(seriesInfo(), true);
    expect(item('[data-testid="refresh"]')).toBeNull();
    expect(item('[data-testid="mangadex-match"]')).toBeNull();
  });

  it('offers Change MangaDex match for a MangaUpdates link and reports a change (1.29.0)', async () => {
    const web = { provider: 'mangaupdates', providerName: 'MangaUpdates', fetchedAt: '2026-09-20T00:00:00Z', hasImage: false };
    const { fixture, mangaDex } = create(seriesInfo({ nodeId: 'a1', state: 'Web', web }), true);
    fixture.detectChanges();
    item('[data-testid="mangadex-match"]')!.click();
    expect(mangaDex.open).toHaveBeenCalledWith('a1');
    await Promise.resolve();
    await Promise.resolve();
    expect(fixture.componentInstance.changes).toBe(1);
  });

  it('marks a node Don\'t match and reports the change', () => {
    const { fixture, api, state } = create(seriesInfo({ nodeId: 'f1' }));
    item('[data-testid="dont-match"]')!.click();
    expect(api.setDontMatch).toHaveBeenCalledWith('f1');
    expect(fixture.componentInstance.changes).toBe(1);
    expect(state.refresh).toHaveBeenCalledWith('f1'); // browse (i) / top bar re-derive in place
  });

  it('offers Clear instead when the node itself is Don\'t match', () => {
    const { api } = create(seriesInfo({ nodeId: 'f1', state: 'DontMatch', link: { state: 'DontMatch', nodeId: 'f1', inherited: false } }));
    expect(item('[data-testid="dont-match"]')).toBeNull();
    item('[data-testid="clear-dont-match"]')!.click();
    expect(api.clearDontMatch).toHaveBeenCalledWith('f1');
  });

  it('offers Unlink only for the node\'s own web link', () => {
    create(seriesInfo({ link: { state: 'Confirmed', nodeId: 'parent', inherited: true } }));
    expect(item('[data-testid="unlink"]')).toBeNull();
    TestBed.resetTestingModule();
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = ''));
    const { api, state } = create(seriesInfo({ nodeId: 'f1', state: 'Web', link: { state: 'Confirmed', nodeId: 'f1', inherited: false } }));
    item('[data-testid="unlink"]')!.click();
    expect(api.unlink).toHaveBeenCalledWith('f1');
    expect(state.refresh).toHaveBeenCalledWith('f1');
  });

  it('sets and clears folder precedence, and hides it for archives', () => {
    const { api, state } = create(seriesInfo({ nodeId: 'f1', nodeKind: 'Folder' }));
    item('[data-testid="precedence-comicinfo"]')!.click();
    expect(api.setFolderPrecedence).toHaveBeenCalledWith('f1', 'ComicInfoFirst');
    expect(state.refresh).not.toHaveBeenCalled(); // precedence never changes the card (i)
    TestBed.resetTestingModule();
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = ''));
    create(seriesInfo({ nodeId: 'a1', nodeKind: 'Archive' }));
    expect(item('[data-testid="precedence-web"]')).toBeNull();
  });

  it('says what a Content change queued again, and offers "Match again" when it asked first', () => {
    const action = new Subject<void>();
    const open = vi.spyOn(MatSnackBar.prototype, 'open')
      .mockReturnValue({ onAction: () => action } as unknown as MatSnackBarRef<TextOnlySnackBar>);
    const { api, fixture } = create(seriesInfo({ nodeId: 'f1', nodeKind: 'Folder' }), false, { nodeId: 'f1', effective: 'Auto' });
    fixture.detectChanges();
    api.setFolderContent.mockReturnValueOnce(of({ nodeId: 'f1', content: 'DoujinshiAndAdultOneShots', effective: 'DoujinshiAndAdultOneShots',
      rematch: { affected: 6, queued: 6 } }) as never);
    item('[data-testid="content-DoujinshiAndAdultOneShots"]')!.click();
    expect(open).toHaveBeenLastCalledWith('Content set: Doujinshi & adult one-shots · 6 items below queued to match again', 'Close', { duration: 5000 });

    const rematch = vi.fn(() => of({ affected: 250, queued: 250 }));
    (api as unknown as { rematchFolderContent: typeof rematch }).rematchFolderContent = rematch;
    api.setFolderContent.mockReturnValueOnce(of({ nodeId: 'f1', content: 'DoujinshiAndAdultOneShots', effective: 'DoujinshiAndAdultOneShots',
      rematch: { affected: 250, queued: 0, needsConfirmation: true } }) as never);
    (fixture.nativeElement.querySelector('[data-testid="series-admin-menu"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    item('[data-testid="content-DoujinshiAndAdultOneShots"]')!.click();
    expect(open.mock.calls.at(-1)![1]).toBe('Match again');
    expect(open.mock.calls.at(-1)![0]).toContain('250 items below were matched without this setting');
    expect(rematch).not.toHaveBeenCalled();
    action.next();
    expect(rematch).toHaveBeenCalledWith('f1');
    expect(open).toHaveBeenLastCalledWith('250 items queued to match again', 'Close', { duration: 3000 });
    open.mockRestore();
  });

  it('shows the folder Content setting with its source and the detector\'s suggestion (stage 2)', () => {
    const { fixture, api } = create(seriesInfo({ nodeId: 'f1', nodeKind: 'Folder' }), false,
      { nodeId: 'f1', content: null, effective: 'Auto', sourceNodeId: 'p1', suggested: 'DoujinshiAndAdultOneShots' });
    fixture.detectChanges();
    expect(api.getFolderContent).toHaveBeenCalledWith('f1');
    expect(document.querySelector('[data-testid="content-caption"]')!.textContent).toBe('Content: Auto (inherited)');
    expect(item('[data-testid="content-DoujinshiAndAdultOneShots"]')!.textContent).toContain('suggested');
    expect(item('[data-testid="content-inherit"]')).toBeNull(); // nothing of its own to clear
    item('[data-testid="content-DoujinshiAndAdultOneShots"]')!.click();
    expect(api.setFolderContent).toHaveBeenCalledWith('f1', 'DoujinshiAndAdultOneShots');
    expect(fixture.componentInstance.changes).toBe(0); // Content never changes the shown information
  });

  it('clears an own Content value, and hides the section without the server setting or for archives', () => {
    const { api } = create(seriesInfo({ nodeId: 'f1', nodeKind: 'Folder' }), false,
      { nodeId: 'f1', content: 'NotDoujinshi', effective: 'NotDoujinshi' });
    expect(document.querySelector('[data-testid="content-caption"]')!.textContent).toBe('Content: Not doujinshi (set here)');
    item('[data-testid="content-inherit"]')!.click();
    expect(api.clearFolderContent).toHaveBeenCalledWith('f1');
    TestBed.resetTestingModule();
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = ''));
    create(seriesInfo({ nodeId: 'f2', nodeKind: 'Folder' }));
    expect(document.querySelector('[data-testid="content-caption"]')).toBeNull();
    TestBed.resetTestingModule();
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = ''));
    const archive = create(seriesInfo({ nodeId: 'a1', nodeKind: 'Archive' }), false, { nodeId: 'a1', effective: 'Auto' });
    expect(archive.api.getFolderContent).not.toHaveBeenCalled();
  });

  describe('1.34.0: Collection about', () => {
    const web = { provider: 'mangaupdates', providerName: 'MangaUpdates', fetchedAt: '2026-10-04T00:00:00Z', hasImage: false };

    it('a folder offers "Collection about..." which opens the identify dialog in its collection mode', async () => {
      const { fixture, dialog } = create(seriesInfo({ nodeId: 'f1' }), true);
      fixture.detectChanges();
      const action = item('[data-testid="collection-about"]')!;
      expect(action.textContent).toContain('Collection about');
      action.click();
      expect(dialog.open).toHaveBeenCalledWith('f1', 'collection');
      await Promise.resolve();
      await Promise.resolve();
      expect(fixture.componentInstance.changes).toBe(1);
    });

    it('an archive does not offer it', () => {
      create(seriesInfo({ nodeId: 'a1', nodeKind: 'Archive' }), true);
      expect(item('[data-testid="collection-about"]')).toBeNull();
    });

    it('an own collection offers Change series / Clear collection, not Unlink or the MangaDex match', () => {
      const info = seriesInfo({
        nodeId: 'f1', state: 'CollectionAbout', web,
        link: { state: 'CollectionAbout', nodeId: 'f1', inherited: false, linkedAt: '2026-10-04T00:00:00Z' },
      });
      const { fixture, api, state } = create(info, true);
      fixture.detectChanges();
      expect(item('[data-testid="collection-about"]')!.textContent).toContain('Change series');
      expect(item('[data-testid="unlink"]')).toBeNull();
      expect(item('[data-testid="mangadex-match"]')).toBeNull();
      item('[data-testid="clear-collection"]')!.click();
      expect(api.clearCollection).toHaveBeenCalledWith('f1');
      expect(state.refresh).toHaveBeenCalledWith('f1');
    });
  });

  describe('1.37.0: Artist folder', () => {
    it('a folder offers "Artist folder..." - the dialog starts with its name; marking announces the change and the declared artist', async () => {
      const { fixture, api, artist, state, declaredApi } = create(seriesInfo({ nodeId: 'f1', anchorNodeId: 'f1', anchorDisplayName: 'Beta Painter' }));
      item('[data-testid="artist-folder"]')!.click();
      await Promise.resolve();
      await Promise.resolve();
      expect(artist.open).toHaveBeenCalledWith('Beta Painter');
      expect(api.setArtistFolder).toHaveBeenCalledWith('f1', { name: 'Beta Painter', role: 'author' });
      expect(state.refresh).toHaveBeenCalledWith('f1');
      expect(declaredApi.version()).toBe(1);
      expect(fixture.componentInstance.changes).toBe(1);
    });

    it('starts with an empty name when the information shown is inherited (the server uses the folder name)', async () => {
      const { artist } = create(seriesInfo({ nodeId: 'f2', anchorNodeId: 'p1', anchorDisplayName: 'Parent Series' }));
      item('[data-testid="artist-folder"]')!.click();
      await Promise.resolve();
      expect(artist.open).toHaveBeenCalledWith('');
    });

    it('an archive does not offer it', () => {
      create(seriesInfo({ nodeId: 'a1', nodeKind: 'Archive' }), true);
      expect(item('[data-testid="artist-folder"]')).toBeNull();
    });

    it('an own artist folder offers Remove artist folder, not Unlink', () => {
      const info = seriesInfo({
        nodeId: 'f1', state: 'ArtistFolder', title: 'Beta Painter',
        link: { state: 'ArtistFolder', nodeId: 'f1', inherited: false, linkedAt: '2026-10-08T00:00:00Z' },
      });
      const { api, state } = create(info, true);
      expect(item('[data-testid="artist-folder"]')).toBeNull();
      expect(item('[data-testid="unlink"]')).toBeNull();
      item('[data-testid="clear-artist-folder"]')!.click();
      expect(api.clearArtistFolder).toHaveBeenCalledWith('f1');
      expect(state.refresh).toHaveBeenCalledWith('f1');
    });
  });
});
