import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MatBottomSheet } from '@angular/material/bottom-sheet';
import { MatDialog } from '@angular/material/dialog';
import { of, throwError } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';
import { CatalogNodeDto, SeriesInfoDto } from '../../core/api/api-types';
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

  function create(info: SeriesInfoDto | 'error', admin = false) {
    const open = vi.fn(() => Promise.resolve());
    const api = { getSeriesInfo: vi.fn(() => (info === 'error' ? throwError(() => ({ error: 'x' })) : of(info))) };
    TestBed.configureTestingModule({
      imports: [Host],
      providers: [
        provideNoopAnimations(),
        { provide: MetadataApiService, useValue: api },
        { provide: SeriesInfoOverlayService, useValue: { open } },
        { provide: AuthService, useValue: { isAdmin: () => admin } },
      ],
    });
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    const button = () => fixture.nativeElement.querySelector('[data-testid="series-info-button"]') as HTMLButtonElement | null;
    return { fixture, api, open, button };
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

  it('is hidden for admins too when the folder has no series information', () => {
    // Not every folder is a series (author / magazine / Volumes folders); admins reach
    // Identify, Don't match and precedence from the selection "Series" menu (1.24.0).
    expect(create(seriesInfo({ state: 'None' }), true).button()).toBeNull();
    TestBed.resetTestingModule();
    expect(create(seriesInfo({ state: 'DontMatch' }), true).button()).toBeNull();
    TestBed.resetTestingModule();
    expect(create(seriesInfo(), true).button()).not.toBeNull();
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

  function create() {
    const api = {
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
    (fixture.nativeElement.querySelector('[data-testid="series-selection-menu"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    return { api };
  }

  const click = (sel: string) => (document.querySelector(sel) as HTMLButtonElement).click();

  it('marks every selected node Don\'t match', () => {
    const { api } = create();
    click('[data-testid="bulk-dont-match"]');
    expect(api.setDontMatch.mock.calls.map((c: unknown[]) => c[0])).toEqual(['f1', 'a1']);
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
