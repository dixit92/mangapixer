import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MatBottomSheet } from '@angular/material/bottom-sheet';
import { MatDialog } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';
import { CatalogNodeDto, FolderMetadataContentDto, IdentifyContextDto, MetadataSettingsDto, SeriesInfoDto } from '../../core/api/api-types';
import { IdentifyDialogService } from './identify-dialog/identify-dialog.service';
import { MetadataStateService } from './metadata-state.service';
import { MetadataApiService } from './metadata-api.service';
import { SeriesInfoButtonComponent } from './series-info-button.component';
import { SeriesInfoOverlayService } from './series-info-overlay.service';
import { InfoApiService } from './info/info-api.service';
import { InfoPanelDto } from './info/info-types';
import { emptyPanel, panel } from './info/info.testing';
import { SeriesSelectionActionsComponent } from './series-selection-actions.component';
import { seriesInfo } from './series-info.testing';

/** Browse-embedded series surfaces (1.24.0): the top-bar button, the selection menu, the overlay host choice. */
describe('SeriesInfoButtonComponent', () => {
  @Component({
    standalone: true,
    imports: [SeriesInfoButtonComponent],
    template: `<app-series-info-button [nodeId]="nodeId()" />`,
  })
  class Host {
    readonly nodeId = signal('folder-1');
  }

  const settings = (show = true, libraryShow = true) => ({
    showSeriesInfo: show,
    libraries: [{ libraryId: 'lib1', name: 'L', fetchEnabled: true, showSeriesInfo: libraryShow, linkCount: 0 }],
  }) as MetadataSettingsDto;

  function create(info: SeriesInfoDto | 'error', admin = false,
    opts: { fetchAvailable?: boolean; settings?: MetadataSettingsDto; works?: number | 'error' } = {}) {
    const open = vi.fn(() => Promise.resolve());
    const identifyOpen = vi.fn(() => Promise.resolve(true));
    let current = info;
    const api = {
      getSeriesInfo: vi.fn(() => (current === 'error' ? throwError(() => ({ error: 'x' })) : of(current))),
      getIdentifyContext: vi.fn(() => of({ libraryId: 'lib1', fetchAvailable: opts.fetchAvailable ?? true } as IdentifyContextDto)),
      getSettings: vi.fn(() => of(opts.settings ?? settings())),
    };
    // 1.45.0: the tag index's answer for the folder (works at or below it); default: nothing indexed (lane Q's route answers 0 works).
    const works = opts.works ?? 0;
    const infoApi = {
      getInfo: vi.fn(() => (works === 'error'
        ? throwError(() => ({ status: 404 }))
        : of((works > 0 ? panel({ nodeId: 'folder-1', mode: 'Container', worksBelow: works }) : emptyPanel('folder-1')) as InfoPanelDto))),
    };
    TestBed.configureTestingModule({
      imports: [Host],
      providers: [
        provideNoopAnimations(),
        { provide: MetadataApiService, useValue: api },
        { provide: InfoApiService, useValue: infoApi },
        { provide: SeriesInfoOverlayService, useValue: { open } },
        { provide: AuthService, useValue: { isAdmin: () => admin } },
        { provide: IdentifyDialogService, useValue: { open: identifyOpen } },
      ],
    });
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    const button = () => fixture.nativeElement.querySelector('[data-testid="series-info-button"]') as HTMLButtonElement | null;
    const identify = () => fixture.nativeElement.querySelector('[data-testid="series-identify-button"]') as HTMLButtonElement | null;
    const setInfo = (next: SeriesInfoDto) => { current = next; };
    return { fixture, api, infoApi, open, identifyOpen, button, identify, setInfo };
  }

  it('reads "Info" (owner decision 7, 1.45.0)', () => {
    const { button } = create(seriesInfo({ state: 'Web' }));
    expect(button()!.textContent).toContain('Info');
    expect(button()!.textContent).not.toContain('Series info');
    expect(button()!.getAttribute('aria-label')).toBe('Info');
  });

  it('1.45.0: appears for a folder WITHOUT series information when the tag index knows works below it, for readers and admins', () => {
    const reader = create(seriesInfo({ state: 'None' }), false, { works: 3 });
    expect(reader.infoApi.getInfo).toHaveBeenCalledWith('folder-1');
    expect(reader.button()).not.toBeNull();
    reader.button()!.click();
    expect(reader.open).toHaveBeenCalledWith('folder-1');
    TestBed.resetTestingModule();
    const admin = create(seriesInfo({ state: 'None' }), true, { works: 3 });
    expect(admin.button()).not.toBeNull();
    expect(admin.identify()).toBeNull();
  });

  it('1.45.0: keeps the old rule when the tag index has nothing or cannot answer', () => {
    expect(create(seriesInfo({ state: 'None' }), false, { works: 'error' }).button()).toBeNull();
    TestBed.resetTestingModule();
    expect(create(seriesInfo({ state: 'Web' }), false, { works: 'error' }).button()).not.toBeNull();
    TestBed.resetTestingModule();
    expect(create(seriesInfo({ state: 'None' }), true, { works: 'error' }).identify()).not.toBeNull();
  });

  it('appears when the current folder resolves to a series and opens the overlay', () => {
    const { api, open, button } = create(seriesInfo({ state: 'Web' }));
    expect(api.getSeriesInfo).toHaveBeenCalledWith('folder-1');
    button()!.click();
    expect(open).toHaveBeenCalledWith('folder-1');
  });

  it('stays hidden for readers when there is nothing to show', () => {
    expect(create(seriesInfo({ state: 'None' })).button()).toBeNull();
    TestBed.resetTestingModule();
    expect(create(seriesInfo({ state: 'DontMatch' })).button()).toBeNull();
    TestBed.resetTestingModule();
    expect(create('error').button()).toBeNull();
  });

  it('offers admins "Identify..." for THIS folder when it has no information and Identify is possible', () => {
    const { api, identify, identifyOpen, button } = create(seriesInfo({ state: 'None' }), true);
    expect(button()).toBeNull();
    expect(api.getIdentifyContext).toHaveBeenCalledWith('folder-1'); // the no-network availability check
    identify()!.click();
    expect(identifyOpen).toHaveBeenCalledWith('folder-1');
    TestBed.resetTestingModule();
    expect(create(seriesInfo({ state: 'DontMatch' }), true).identify()).not.toBeNull();
  });

  it('shows nothing for admins when Identify is not possible or series information is hidden', () => {
    expect(create(seriesInfo({ state: 'None' }), true, { fetchAvailable: false }).identify()).toBeNull();
    TestBed.resetTestingModule();
    expect(create(seriesInfo({ state: 'None' }), true, { settings: settings(false) }).identify()).toBeNull();
    TestBed.resetTestingModule();
    expect(create(seriesInfo({ state: 'None' }), true, { settings: settings(true, false) }).identify()).toBeNull();
  });

  it('never asks readers\' sessions for the admin identify context', () => {
    const { api, identify } = create(seriesInfo({ state: 'None' }));
    expect(identify()).toBeNull();
    expect(api.getIdentifyContext).not.toHaveBeenCalled();
  });

  it('flips "Identify..." <-> "Info" on a link change for this folder, without a reload', () => {
    const { fixture, api, button, identify, setInfo } = create(seriesInfo({ state: 'None' }), true);
    expect(identify()).not.toBeNull();
    const state = TestBed.inject(MetadataStateService);

    setInfo(seriesInfo({ state: 'Web' }));
    state.announce('other-folder', true); // not this folder: nothing re-resolves
    expect(api.getSeriesInfo).toHaveBeenCalledTimes(1);
    state.announce('folder-1', true); // Link from the dialog
    fixture.detectChanges();
    expect(button()).not.toBeNull();
    expect(identify()).toBeNull();

    setInfo(seriesInfo({ state: 'None' }));
    state.announce('folder-1', false); // Unlink / Don't match
    fixture.detectChanges();
    expect(button()).toBeNull();
    expect(identify()).not.toBeNull();
  });

  it('re-resolves when the browsed folder changes', () => {
    const { fixture, api } = create(seriesInfo());
    fixture.componentInstance.nodeId.set('folder-2');
    fixture.detectChanges();
    expect(api.getSeriesInfo).toHaveBeenLastCalledWith('folder-2');
  });
});

describe('SeriesSelectionActionsComponent', () => {
  const node = (id: string, kind: 'Folder' | 'Archive') =>
    ({ id, kind, parentId: '', libraryId: 'lib1', displayName: id, availability: 'Available' }) as CatalogNodeDto;

  @Component({
    standalone: true,
    imports: [SeriesSelectionActionsComponent],
    template: `<app-series-selection-actions [nodes]="nodes" [selected]="selected" />`,
  })
  class Host {
    nodes = [node('f1', 'Folder'), node('f2', 'Folder'), node('a1', 'Archive')];
    selected: ReadonlySet<string> = new Set(['f1', 'a1']);
  }

  const settingsFor = (show: boolean, libraryShow: boolean) => ({
    showSeriesInfo: show,
    libraries: [{ libraryId: 'lib1', name: 'L', fetchEnabled: true, showSeriesInfo: libraryShow, linkCount: 0 }],
  }) as MetadataSettingsDto;

  function create(settings = settingsFor(true, true), openMenu = true, content: FolderMetadataContentDto | 'none' | null = null) {
    const api = {
      getSettings: vi.fn(() => of(settings)),
      getSeriesInfo: vi.fn(() => of(seriesInfo({ state: 'None' }))),
      setDontMatch: vi.fn(() => of({})),
      clearDontMatch: vi.fn(() => of({})),
      setFolderPrecedence: vi.fn(() => of({})),
      clearFolderPrecedence: vi.fn(() => of(undefined)),
      // Stage 2 Content: 'none' = a server without the setting (501).
      getFolderContent: vi.fn((id: string) => (content === 'none' ? throwError(() => ({ status: 501 }))
        : of(content ?? { nodeId: id, effective: 'Auto' }))),
      setFolderContent: vi.fn(() => of({})),
      clearFolderContent: vi.fn(() => of({})),
    };
    TestBed.configureTestingModule({
      imports: [Host],
      providers: [provideNoopAnimations(), { provide: MetadataApiService, useValue: api }],
    });
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    const menu = () => fixture.nativeElement.querySelector('[data-testid="series-selection-menu"]') as HTMLButtonElement | null;
    if (openMenu) {
      menu()!.click();
      fixture.detectChanges();
    }
    return { api, menu };
  }

  const click = (sel: string) => (document.querySelector(sel) as HTMLButtonElement).click();

  it('marks every selected node Don\'t match', () => {
    const { api } = create();
    click('[data-testid="bulk-dont-match"]');
    expect(api.setDontMatch.mock.calls.map((c: unknown[]) => c[0])).toEqual(['f1', 'a1']);
  });

  it('announces each changed node from one series-info GET so the cards update in place', () => {
    const { api } = create();
    const changes: unknown[] = [];
    TestBed.inject(MetadataStateService).changed$.subscribe((c) => changes.push(c));
    click('[data-testid="bulk-dont-match"]');
    expect(api.getSeriesInfo.mock.calls.map((c: unknown[]) => c[0])).toEqual(['f1', 'a1']);
    expect(changes).toEqual([{ nodeId: 'f1', hasSeriesInfo: false }, { nodeId: 'a1', hasSeriesInfo: false }]);
  });

  it('is hidden while "Show series information" is off globally or for this library', () => {
    expect(create(settingsFor(false, true), false).menu()).toBeNull();
    TestBed.resetTestingModule();
    expect(create(settingsFor(true, false), false).menu()).toBeNull();
    TestBed.resetTestingModule();
    expect(create(settingsFor(true, true), false).menu()).not.toBeNull();
  });

  it('applies precedence to the selected folders only', () => {
    const { api } = create();
    click('[data-testid="bulk-precedence-comicinfo"]');
    expect(api.setFolderPrecedence).toHaveBeenCalledTimes(1);
    expect(api.setFolderPrecedence).toHaveBeenCalledWith('f1', 'ComicInfoFirst');
  });
  it('sets the Content of the selected folders only, showing the one folder\'s value and suggestion (stage 2)', () => {
    const { api } = create(undefined, true, { nodeId: 'f1', content: null, effective: 'Auto', suggested: 'DoujinshiAndAdultOneShots' });
    expect(api.getFolderContent).toHaveBeenCalledWith('f1');
    expect(document.querySelector('[data-testid="bulk-content-caption"]')!.textContent).toBe('Content: Auto (default)');
    expect(document.querySelector('[data-testid="bulk-content-DoujinshiAndAdultOneShots"]')!.textContent).toContain('suggested');
    click('[data-testid="bulk-content-NotDoujinshi"]');
    expect(api.setFolderContent.mock.calls).toEqual([['f1', 'NotDoujinshi']]);
    click('[data-testid="bulk-content-inherit"]');
    expect(api.clearFolderContent.mock.calls).toEqual([['f1']]);
  });

  it('hides the Content section when the server has no Content setting', () => {
    create(undefined, true, 'none');
    expect(document.querySelector('[data-testid="bulk-content-caption"]')).toBeNull();
  });
});

describe('SeriesInfoOverlayService', () => {
  const originalMatchMedia = globalThis.matchMedia;

  afterEach(() => {
    globalThis.matchMedia = originalMatchMedia;
  });

  function create(phone: boolean) {
    globalThis.matchMedia = ((q: string) => ({ matches: phone && q.includes('max-width') })) as unknown as typeof matchMedia;
    const dialog = { open: vi.fn() };
    const sheet = { open: vi.fn() };
    TestBed.configureTestingModule({
      providers: [provideNoopAnimations(), { provide: MatDialog, useValue: dialog }, { provide: MatBottomSheet, useValue: sheet }],
    });
    return { service: TestBed.inject(SeriesInfoOverlayService), dialog, sheet };
  }

  it('opens a right-side sheet on desktop', async () => {
    const { service, dialog, sheet } = create(false);
    await service.open('n1');
    expect(sheet.open).not.toHaveBeenCalled();
    const config = dialog.open.mock.calls[0][1];
    expect(config.data).toEqual({ nodeId: 'n1' });
    expect(config.position).toEqual({ right: '0', top: '0' });
    expect(config.panelClass).toBe('series-info-side-sheet');
  });

  it('opens a bottom sheet on phone', async () => {
    const { service, dialog, sheet } = create(true);
    await service.open('n1');
    expect(dialog.open).not.toHaveBeenCalled();
    expect(sheet.open.mock.calls[0][1].data).toEqual({ nodeId: 'n1' });
  });

  it('1.47.0: opens a library root\'s overlay the same way, with the library as its data', async () => {
    const desktop = create(false);
    await desktop.service.openLibrary('lib1', 'Manga');
    expect(desktop.dialog.open.mock.calls[0][1].data).toEqual({ libraryId: 'lib1', libraryName: 'Manga' });
    TestBed.resetTestingModule();
    const phone = create(true);
    await phone.service.openLibrary('lib1', 'Manga');
    expect(phone.sheet.open.mock.calls[0][1].data).toEqual({ libraryId: 'lib1', libraryName: 'Manga' });
  });
});

/** 1.47.0 (lane P): the library root's Info slot - a library is not a node: only the tag index decides, never Identify. */
describe('SeriesInfoButtonComponent at a library root', () => {
  @Component({
    standalone: true,
    imports: [SeriesInfoButtonComponent],
    template: `<app-series-info-button [libraryId]="libraryId()" libraryName="Manga" />`,
  })
  class LibraryHost {
    readonly libraryId = signal('lib1');
  }

  function create(answer: InfoPanelDto | 'error', admin = true) {
    const open = vi.fn(() => Promise.resolve());
    const openLibrary = vi.fn(() => Promise.resolve());
    const metadata = { getSeriesInfo: vi.fn(), getIdentifyContext: vi.fn(), getSettings: vi.fn() };
    const infoApi = {
      getInfo: vi.fn(),
      getLibraryInfo: vi.fn(() => (answer === 'error' ? throwError(() => ({ status: 404 })) : of(answer))),
    };
    TestBed.configureTestingModule({
      imports: [LibraryHost],
      providers: [
        provideNoopAnimations(),
        { provide: MetadataApiService, useValue: metadata },
        { provide: InfoApiService, useValue: infoApi },
        { provide: SeriesInfoOverlayService, useValue: { open, openLibrary } },
        { provide: AuthService, useValue: { isAdmin: () => admin } },
        { provide: IdentifyDialogService, useValue: { open: vi.fn() } },
      ],
    });
    const fixture = TestBed.createComponent(LibraryHost);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return {
      fixture, metadata, infoApi, open, openLibrary,
      button: () => el.querySelector<HTMLButtonElement>('[data-testid="series-info-button"]'),
      identify: () => el.querySelector('[data-testid="series-identify-button"]'),
    };
  }

  it('shows "Info" when the library has tags the viewer may see, and opens the library overlay', () => {
    const { button, infoApi, metadata, open, openLibrary } = create(panel({ nodeId: 'lib1', mode: 'Container', worksBelow: 5 }));
    expect(infoApi.getLibraryInfo).toHaveBeenCalledWith('lib1');
    expect(infoApi.getInfo).not.toHaveBeenCalled();
    expect(metadata.getSeriesInfo).not.toHaveBeenCalled(); // a library root has no series information
    expect(button()!.getAttribute('aria-label')).toBe('Info');
    button()!.click();
    expect(openLibrary).toHaveBeenCalledWith('lib1', 'Manga');
    expect(open).not.toHaveBeenCalled();
  });

  it('shows nothing (never Identify) when nothing is indexed or the server cannot answer', () => {
    const empty = create(emptyPanel('lib1'));
    expect(empty.button()).toBeNull();
    expect(empty.identify()).toBeNull();
    expect(empty.metadata.getIdentifyContext).not.toHaveBeenCalled();
    TestBed.resetTestingModule();
    const failed = create('error');
    expect(failed.button()).toBeNull();
    expect(failed.identify()).toBeNull();
  });

  it('asks again for another library', () => {
    const { fixture, infoApi } = create(panel({ nodeId: 'lib1', mode: 'Container' }));
    fixture.componentInstance.libraryId.set('lib2');
    fixture.detectChanges();
    expect(infoApi.getLibraryInfo).toHaveBeenLastCalledWith('lib2');
  });
});
