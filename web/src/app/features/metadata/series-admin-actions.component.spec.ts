import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { IdentifyContextDto, SeriesInfoDto } from '../../core/api/api-types';
import { IdentifyDialogService } from './identify-dialog/identify-dialog.service';
import { MetadataApiService } from './metadata-api.service';
import { MetadataStateService } from './metadata-state.service';
import { SeriesAdminActionsComponent } from './series-admin-actions.component';
import { seriesInfo } from './series-info.testing';

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

  function create(info: SeriesInfoDto, fetchAvailable = false) {
    const dialog = { open: vi.fn(() => Promise.resolve(true)) };
    const api = {
      getIdentifyContext: vi.fn(() => of(context(fetchAvailable))),
      refresh: vi.fn(() => of({ state: 'Ok', fetchedAt: '2026-09-25T00:00:00Z', imageUpdated: false })),
      setDontMatch: vi.fn(() => of({ nodeId: info.nodeId })),
      clearDontMatch: vi.fn(() => of({ nodeId: info.nodeId })),
      unlink: vi.fn(() => of({ nodeId: info.nodeId })),
      setFolderPrecedence: vi.fn(() => of({ nodeId: info.nodeId, precedence: 'WebFirst' })),
      clearFolderPrecedence: vi.fn(() => of(undefined)),
    };
    const state = { announce: vi.fn(), refresh: vi.fn() };
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        provideNoopAnimations(),
        { provide: MetadataApiService, useValue: api },
        { provide: IdentifyDialogService, useValue: dialog },
        { provide: MetadataStateService, useValue: state },
      ],
    });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.info.set(info);
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('[data-testid="series-admin-menu"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    return { fixture, api, dialog, state };
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
});
