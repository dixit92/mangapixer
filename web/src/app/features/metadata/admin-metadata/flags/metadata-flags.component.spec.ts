import { TestBed } from '@angular/core/testing';
import { MatSnackBar } from '@angular/material/snack-bar';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { MetadataFlagDto } from '../../../../core/api/api-types';
import { IdentifyDialogService } from '../../identify-dialog/identify-dialog.service';
import { MetadataApiService } from '../../metadata-api.service';
import { MetadataReviewStateService } from '../../metadata-review-state.service';
import { MetadataStateService } from '../../metadata-state.service';
import { flag } from '../metadata-admin.testing';
import { MetadataFlagsComponent } from './metadata-flags.component';

/**
 * Flags tab (stage 2, section 5 + decision 12): open flags in the server's order (auto
 * links first), reporter / reason / note, the four resolutions, and "not one series"
 * making Don't match the primary action.
 */
describe('MetadataFlagsComponent', () => {
  function create(items: MetadataFlagDto[] | 'error' = [flag(), flag({ flagId: 'f2', nodeId: 'n2', reason: 'NotOneSeries',
    nodeDisplayName: 'Mixed Folder', note: null, currentLink: { state: 'Confirmed', title: 'Other', updatedAt: '2026-09-01T00:00:00Z' } })],
  linked = true) {
    const api = {
      getFlags: vi.fn(() => (items === 'error' ? throwError(() => ({ status: 501 })) : of({ items, total: items.length }))),
      resolveFlag: vi.fn((flagId: string, outcome: string) => of({ ...flag({ flagId }), state: outcome })),
    };
    const identify = { open: vi.fn(() => Promise.resolve(linked)) };
    const reviewState = { refresh: vi.fn() };
    const metadataState = { refresh: vi.fn() };
    TestBed.configureTestingModule({
      imports: [MetadataFlagsComponent],
      providers: [
        provideNoopAnimations(),
        { provide: MetadataApiService, useValue: api },
        { provide: IdentifyDialogService, useValue: identify },
        { provide: MetadataReviewStateService, useValue: reviewState },
        { provide: MetadataStateService, useValue: metadataState },
        { provide: MatSnackBar, useValue: { open: vi.fn() } },
      ],
    });
    const fixture = TestBed.createComponent(MetadataFlagsComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const rowsEl = () => { fixture.detectChanges(); return Array.from(el.querySelectorAll('[data-testid="flag-row"]')) as HTMLElement[]; };
    return { fixture, el, c: fixture.componentInstance, api, identify, reviewState, metadataState, rowsEl };
  }

  it('lists open flags with reporter, reason, note and how the current link was made', () => {
    const { api, rowsEl } = create();
    expect(api.getFlags).toHaveBeenCalledWith('open', null, null);
    const [first, second] = rowsEl();
    expect(first.textContent).toContain('Reported by Reader One');
    expect(first.querySelector('[data-testid="flag-reason"]')!.textContent).toBe('Wrong series');
    expect(first.querySelector('[data-testid="flag-note"]')!.textContent).toBe('The cover is from another series.');
    expect(first.textContent).toContain('automatic');
    expect(first.classList).toContain('auto');
    expect(second.textContent).toContain('by an admin');
  });

  it('"This folder is not one series" makes Don\'t match the primary, suggested action', () => {
    const [first, second] = create().rowsEl();
    const primary = (row: HTMLElement) => row.querySelector('.actions button')!.getAttribute('data-testid');
    expect(primary(first)).toBe('flag-reidentify');
    expect(primary(second)).toBe('flag-dontmatch');
    expect(second.querySelector('[data-testid="flag-dontmatch"]')!.textContent).toContain('suggested');
  });

  it('resolves with an outcome, drops it from the open list and refreshes the badge', () => {
    const { el, api, reviewState, metadataState, rowsEl } = create();
    (el.querySelector('[data-testid="flag-unlink"]') as HTMLButtonElement).click();
    expect(api.resolveFlag).toHaveBeenCalledWith('f1', 'Unlinked');
    expect(rowsEl()).toHaveLength(1);
    expect(reviewState.refresh).toHaveBeenCalled();
    expect(metadataState.refresh).toHaveBeenCalledWith('n1');
    (rowsEl()[0].querySelector('[data-testid="flag-dismiss"]') as HTMLButtonElement).click();
    expect(api.resolveFlag).toHaveBeenLastCalledWith('f2', 'Dismissed');
  });

  it('Re-identify resolves as Relinked only after a link was made', async () => {
    const ok = create();
    ok.c.reidentify(ok.c.items()[0]);
    expect(ok.identify.open).toHaveBeenCalledWith('n1');
    await vi.waitFor(() => expect(ok.api.resolveFlag).toHaveBeenCalledWith('f1', 'Relinked'));
    TestBed.resetTestingModule();
    const cancelled = create(undefined, false);
    cancelled.c.reidentify(cancelled.c.items()[0]);
    await Promise.resolve();
    await Promise.resolve();
    expect(cancelled.api.resolveFlag).not.toHaveBeenCalled();
  });

  it('keeps resolved flags in place on the All list', () => {
    const { c, api } = create();
    c.setFilter('all');
    expect(api.getFlags).toHaveBeenLastCalledWith('all', null, null);
    c.resolve(c.items()[0], 'DontMatch');
    expect(c.items()).toHaveLength(2);
    expect(c.items()[0].state).toBe('DontMatch');
  });

  it('explains a server without flags (501)', () => {
    const { c } = create('error');
    expect(c.error()).toContain('not available on this server yet');
  });
});
