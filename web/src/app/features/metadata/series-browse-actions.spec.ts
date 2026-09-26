import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MatBottomSheet } from '@angular/material/bottom-sheet';
import { MatDialog } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';
import { CatalogNodeDto, IdentifyContextDto, MetadataSettingsDto, SeriesInfoDto } from '../../core/api/api-types';
import { IdentifyDialogService } from './identify-dialog/identify-dialog.service';
import { MetadataStateService } from './metadata-state.service';
import { MetadataApiService } from './metadata-api.service';
import { SeriesInfoButtonComponent } from './series-info-button.component';
import { SeriesInfoOverlayService } from './series-info-overlay.service';
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
    opts: { fetchAvailable?: boolean; settings?: MetadataSettingsDto } = {}) {
    const open = vi.fn(() => Promise.resolve());
    const identifyOpen = vi.fn(() => Promise.resolve(true));
    let current = info;
    const api = {
      getSeriesInfo: vi.fn(() => (current === 'error' ? throwError(() => ({ error: 'x' })) : of(current))),
      getIdentifyContext: vi.fn(() => of({ libraryId: 'lib1', fetchAvailable: opts.fetchAvailable ?? true } as IdentifyContextDto)),
      getSettings: vi.fn(() => of(opts.settings ?? settings())),
    };
    TestBed.configureTestingModule({
      imports: [Host],
      providers: [
        provideNoopAnimations(),
        { provide: MetadataApiService, useValue: api },
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
    return { fixture, api, open, identifyOpen, button, identify, setInfo };
  }

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

  it('flips "Identify..." <-> "Series info" on a link change for this folder, without a reload', () => {
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

  function create(settings = settingsFor(true, true), openMenu = true) {
    const api = {
      getSettings: vi.fn(() => of(settings)),
      getSeriesInfo: vi.fn(() => of(seriesInfo({ state: 'None' }))),
      setDontMatch: vi.fn(() => of({})),
      clearDontMatch: vi.fn(() => of({})),
      setFolderPrecedence: vi.fn(() => of({})),
      clearFolderPrecedence: vi.fn(() => of(undefined)),
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
});
