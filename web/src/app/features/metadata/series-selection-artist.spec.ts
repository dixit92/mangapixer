import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { CatalogNodeDto, SetArtistFolderRequest } from '../../core/api/api-types';
import { ArtistFolderDialogService } from './artist-folder/artist-folder-dialog.service';
import { DeclaredFactsApiService } from './declared/declared-facts-api.service';
import { IdentifyDialogService } from './identify-dialog/identify-dialog.service';
import { MetadataApiService } from './metadata-api.service';
import { MetadataStateService } from './metadata-state.service';
import { SeriesSelectionActionsComponent } from './series-selection-actions.component';

/** Browse selection "Series" menu (1.37.0): "Artist folder..." for exactly one selected folder. */
describe('SeriesSelectionActionsComponent - Artist folder', () => {
  const folder = (id: string, name = id) =>
    ({ id, kind: 'Folder', parentId: '', libraryId: 'lib1', displayName: name, availability: 'Available' }) as CatalogNodeDto;
  const archive = (id: string) => ({ ...folder(id), kind: 'Archive' }) as CatalogNodeDto;

  function create(selected: string[], answer: SetArtistFolderRequest | undefined = { name: 'Beta Painter', role: 'author' }) {
    @Component({
      standalone: true,
      imports: [SeriesSelectionActionsComponent],
      template: `<app-series-selection-actions [nodes]="nodes" [selected]="selected" />`,
    })
    class Host {
      nodes = [folder('f1', 'Beta Painter'), folder('f2'), archive('a1')];
      selected: ReadonlySet<string> = new Set(selected);
    }
    const api = {
      getSettings: () => of({ showSeriesInfo: true, libraries: [] }),
      getFolderContent: (id: string) => of({ nodeId: id, effective: 'Auto' }),
      setArtistFolder: vi.fn((id: string) => of({ change: { nodeId: id }, artist: { name: 'Beta Painter', role: 'author' }, queued: 4 })),
    };
    const artist = { open: vi.fn(() => Promise.resolve(answer)) };
    const declaredApi = { version: signal(0) };
    const state = { refresh: vi.fn() };
    const snackBar = { open: vi.fn() };
    TestBed.configureTestingModule({
      imports: [Host],
      providers: [
        provideNoopAnimations(),
        { provide: MetadataApiService, useValue: api },
        { provide: IdentifyDialogService, useValue: { open: vi.fn() } },
        { provide: MetadataStateService, useValue: state },
        { provide: ArtistFolderDialogService, useValue: artist },
        { provide: DeclaredFactsApiService, useValue: declaredApi },
        { provide: MatSnackBar, useValue: snackBar },
      ],
    });
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('[data-testid="series-selection-menu"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    const item = (id: string) => document.querySelector(`[data-testid="${id}"]`) as HTMLButtonElement;
    return { api, artist, declaredApi, state, snackBar, item };
  }

  const flush = async () => {
    await Promise.resolve();
    await Promise.resolve();
  };

  it('asks for the artist with the folder name, marks the folder and says what happens inside', async () => {
    const { api, artist, declaredApi, state, snackBar, item } = create(['f1']);
    expect(item('bulk-artist-folder').disabled).toBe(false);
    item('bulk-artist-folder').click();
    await flush();
    expect(artist.open).toHaveBeenCalledWith('Beta Painter');
    expect(api.setArtistFolder).toHaveBeenCalledWith('f1', { name: 'Beta Painter', role: 'author' });
    expect(state.refresh).toHaveBeenCalledWith('f1');
    expect(declaredApi.version()).toBe(1);
    expect(snackBar.open.mock.calls[0][0]).toBe('Artist folder: Beta Painter - 4 works inside will be matched');
  });

  it('a cancelled dialog sends nothing', async () => {
    const { api, item } = create(['f1'], undefined);
    item('bulk-artist-folder').click();
    await flush();
    expect(api.setArtistFolder).not.toHaveBeenCalled();
  });

  it('is disabled for an archive or a multi-selection', () => {
    expect(create(['a1']).item('bulk-artist-folder').disabled).toBe(true);
    TestBed.resetTestingModule();
    expect(create(['f1', 'f2']).item('bulk-artist-folder').disabled).toBe(true);
  });
});
