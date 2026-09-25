import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { SeriesInfoDto } from '../../core/api/api-types';
import { MetadataApiService } from './metadata-api.service';
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
 * Admin actions for one node (1.24.0): the disabled Identify slot (lane B2),
 * Don't match / Clear, Unlink for an own link, precedence for folders only.
 */
describe('SeriesAdminActionsComponent', () => {
  function create(info: SeriesInfoDto) {
    const api = {
      setDontMatch: vi.fn(() => of({ nodeId: info.nodeId })),
      clearDontMatch: vi.fn(() => of({ nodeId: info.nodeId })),
      unlink: vi.fn(() => of({ nodeId: info.nodeId })),
      setFolderPrecedence: vi.fn(() => of({ nodeId: info.nodeId, precedence: 'WebFirst' })),
      clearFolderPrecedence: vi.fn(() => of(undefined)),
    };
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [provideNoopAnimations(), { provide: MetadataApiService, useValue: api }],
    });
    const fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.info.set(info);
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('[data-testid="series-admin-menu"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    return { fixture, api };
  }

  const item = (sel: string) => document.querySelector(sel) as HTMLButtonElement | null;

  it('reserves a disabled Identify slot for the web lookup', () => {
    create(seriesInfo());
    const identify = item('[data-slot="identify"]');
    expect(identify).not.toBeNull();
    expect(identify!.disabled).toBe(true);
  });

  it('marks a node Don\'t match and reports the change', () => {
    const { fixture, api } = create(seriesInfo({ nodeId: 'f1' }));
    item('[data-testid="dont-match"]')!.click();
    expect(api.setDontMatch).toHaveBeenCalledWith('f1');
    expect(fixture.componentInstance.changes).toBe(1);
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
    const { api } = create(seriesInfo({ nodeId: 'f1', state: 'Web', link: { state: 'Confirmed', nodeId: 'f1', inherited: false } }));
    item('[data-testid="unlink"]')!.click();
    expect(api.unlink).toHaveBeenCalledWith('f1');
  });

  it('sets and clears folder precedence, and hides it for archives', () => {
    const { api } = create(seriesInfo({ nodeId: 'f1', nodeKind: 'Folder' }));
    item('[data-testid="precedence-comicinfo"]')!.click();
    expect(api.setFolderPrecedence).toHaveBeenCalledWith('f1', 'ComicInfoFirst');
    TestBed.resetTestingModule();
    document.querySelectorAll('.cdk-overlay-container').forEach((c) => (c.innerHTML = ''));
    create(seriesInfo({ nodeId: 'a1', nodeKind: 'Archive' }));
    expect(item('[data-testid="precedence-web"]')).toBeNull();
  });
});
