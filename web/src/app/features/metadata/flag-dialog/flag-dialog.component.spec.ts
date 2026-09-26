import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Subject, of, throwError } from 'rxjs';

import { MetadataMyFlagDto, MetadataMyFlagStateDto } from '../../../core/api/api-types';
import { MetadataApiService } from '../metadata-api.service';
import { FlagDialogComponent, flagErrorText } from './flag-dialog.component';
import { WrongSeriesFlagComponent } from './wrong-series-flag.component';

const MY_FLAG: MetadataMyFlagDto = {
  flagId: 'f1', anchorNodeId: 'n1', reason: 'WrongSeries', state: 'Open', createdAt: '2026-09-26T00:00:00Z',
};

/** "Wrong series?" dialog (stage 2): reason radio + optional 500-char note, then the thanks state; errors explained. */
describe('FlagDialogComponent', () => {
  function create(result: 'ok' | { status: number; error?: string; message?: string } = 'ok') {
    const api = {
      createFlag: vi.fn(() => (result === 'ok' ? of(MY_FLAG) : throwError(() => result))),
    };
    const ref = { close: vi.fn() };
    TestBed.configureTestingModule({
      imports: [FlagDialogComponent],
      providers: [
        provideNoopAnimations(),
        { provide: MetadataApiService, useValue: api },
        { provide: MatDialogRef, useValue: ref },
        { provide: MAT_DIALOG_DATA, useValue: { nodeId: 'n1', title: 'Synthetic Saga' } },
      ],
    });
    const fixture = TestBed.createComponent(FlagDialogComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    return { fixture, el, c: fixture.componentInstance, api, ref };
  }

  it('needs a reason; sends the reason and the trimmed note; thanks the reader', () => {
    const { c, api, el, fixture, ref } = create();
    expect(c.canSend()).toBe(false);
    expect(el.textContent).toContain('This folder is not one series');
    c.reason.set('NotOneSeries');
    c.note.set('  An anthology.  ');
    c.send();
    expect(api.createFlag).toHaveBeenCalledWith('n1', { reason: 'NotOneSeries', note: 'An anthology.' });
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="flag-thanks"]')!.textContent).toContain('Thanks - an admin will review this.');
    (el.querySelector('[data-testid="flag-done"]') as HTMLButtonElement).click();
    expect(ref.close).toHaveBeenCalledWith(MY_FLAG);
  });

  it('sends no note when it is empty and refuses one over 500 characters', () => {
    const { c, api } = create();
    c.reason.set('Other');
    c.note.set('x'.repeat(501));
    expect(c.canSend()).toBe(false);
    c.note.set('   ');
    c.send();
    expect(api.createFlag).toHaveBeenCalledWith('n1', { reason: 'Other', note: null });
  });

  it('explains the daily limit', () => {
    const { c } = create({ status: 429, error: 'flag_limit', message: 'Too many' });
    c.reason.set('WrongSeries');
    c.send();
    expect(c.error()).toContain('most reports allowed for today');
    expect(c.sent()).toBeNull();
  });

  it('words the other errors', () => {
    expect(flagErrorText({ status: 409, error: 'x', message: 'm' } as never)).toContain('already reported');
    expect(flagErrorText({ status: 404, error: 'not_found', message: 'm' } as never)).toContain('no longer available');
    expect(flagErrorText({ status: 500, error: 'x', message: 'Server says no' } as never)).toBe('Server says no');
    expect(flagErrorText(null)).toBe('The report was not sent.');
  });
});

/** The reader's "Wrong series?" button and their own flag state. */
describe('WrongSeriesFlagComponent', () => {
  function create(state: MetadataMyFlagStateDto | 'error', dialogResult: MetadataMyFlagDto | undefined = MY_FLAG) {
    const api = { getMyFlag: vi.fn(() => (state === 'error' ? throwError(() => ({ status: 501 })) : of(state))) };
    const closed = new Subject<MetadataMyFlagDto | undefined>();
    const dialog = { open: vi.fn(() => ({ afterClosed: () => closed })) };
    TestBed.configureTestingModule({
      imports: [WrongSeriesFlagComponent],
      providers: [provideNoopAnimations(), { provide: MetadataApiService, useValue: api }, { provide: MatDialog, useValue: dialog }],
    });
    const fixture = TestBed.createComponent(WrongSeriesFlagComponent);
    fixture.componentRef.setInput('nodeId', 'n1');
    fixture.componentRef.setInput('title', 'Synthetic Saga');
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const q = (id: string) => { fixture.detectChanges(); return el.querySelector(`[data-testid="${id}"]`); };
    return { fixture, c: fixture.componentInstance, api, dialog, q, close: () => closed.next(dialogResult) };
  }

  it('offers the button when the reader can flag, and shows "You reported this" after sending', async () => {
    const { c, q, dialog, close, api } = create({ canFlag: true, flag: null });
    expect(api.getMyFlag).toHaveBeenCalledWith('n1');
    expect(q('wrong-series')).not.toBeNull();
    await c.open();
    expect(dialog.open).toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ data: { nodeId: 'n1', title: 'Synthetic Saga' } }));
    close();
    expect(q('flag-reported')!.textContent).toContain('You reported this');
    expect(q('wrong-series')).toBeNull();
  });

  it('shows "Reviewed" once resolved (and allows a new report)', () => {
    const { q } = create({ canFlag: true, flag: { ...MY_FLAG, state: 'Dismissed', resolvedAt: '2026-09-27T00:00:00Z' } });
    expect(q('flag-reviewed')!.textContent).toContain('Reviewed');
    expect(q('wrong-series')).not.toBeNull();
  });

  it('shows nothing when the reader cannot flag or the server has no flags', () => {
    const cannot = create({ canFlag: false, flag: null });
    expect(cannot.q('wrong-series')).toBeNull();
    TestBed.resetTestingModule();
    const old = create('error');
    expect(old.q('wrong-series')).toBeNull();
  });
});
