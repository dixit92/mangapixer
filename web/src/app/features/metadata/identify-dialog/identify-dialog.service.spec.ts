import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { of } from 'rxjs';

import { IdentifyDialogService } from './identify-dialog.service';

/** Identify dialog host choice (1.24.0, lane B2): centered on desktop, full-screen on phone. */
describe('IdentifyDialogService', () => {
  const originalMatchMedia = globalThis.matchMedia;
  afterEach(() => (globalThis.matchMedia = originalMatchMedia));

  function create(phone: boolean, closedWith: unknown) {
    globalThis.matchMedia = ((q: string) => ({ matches: phone && q.includes('max-width') })) as unknown as typeof matchMedia;
    const dialog = { open: vi.fn(() => ({ afterClosed: () => of(closedWith) })) };
    TestBed.configureTestingModule({ providers: [{ provide: MatDialog, useValue: dialog }] });
    return { service: TestBed.inject(IdentifyDialogService), dialog };
  }

  it('opens a centered dialog on desktop and reports a link', async () => {
    const { service, dialog } = create(false, true);
    expect(await service.open('n1')).toBe(true);
    const config = (dialog.open.mock.calls[0] as unknown[])[1] as { data: unknown; width: string; panelClass: string };
    expect(config.data).toEqual({ nodeId: 'n1' });
    expect(config.width).toBe('760px');
    expect(config.panelClass).toBe('identify-dialog-panel');
  });

  it('goes full-screen on phone; closing without a link resolves false', async () => {
    const { service, dialog } = create(true, undefined);
    expect(await service.open('n1')).toBe(false);
    const config = (dialog.open.mock.calls[0] as unknown[])[1] as { width: string; height: string };
    expect(config.width).toBe('100vw');
    expect(config.height).toBe('100vh');
  });
});
