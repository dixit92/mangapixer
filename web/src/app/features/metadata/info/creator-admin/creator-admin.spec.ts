import { Type } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MAT_DIALOG_DATA, MatDialog, MatDialogRef } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { of, throwError } from 'rxjs';

import { InfoApiService } from '../info-api.service';
import { CreatorDto } from '../info-types';
import { CreatorAdminService } from './creator-admin.service';
import { CreatorMergeDialogComponent } from './creator-merge-dialog.component';
import { CreatorRenameDialogComponent } from './creator-rename-dialog.component';
import { CreatorSplitDialogComponent } from './creator-split-dialog.component';
import { creatorErrorText, creatorSummary, providerIdLabel } from './creator-admin-labels';

const creator = (o: Partial<CreatorDto> = {}): CreatorDto => ({
  id: 'cr_1',
  displayName: 'Synthetic Author',
  displayNameSource: 'provider',
  names: [{ name: 'Synthetic Author', source: 'providerAuthor', script: 'latin' }, { name: 'S. Author', source: 'comicInfo', script: 'latin' }],
  providerIds: ['mangaupdates:101', 'mangaupdates:202'],
  valueId: 'fv_a',
  works: 3,
  ...o,
});

/** Admin creator dialogs (1.45.0, D1 2.2) on lane F's routes: Rename, Merge into... (find + confirm), Split... (choose + confirm). */
describe('creator admin dialogs', () => {
  const ref = { close: vi.fn() };

  function render<T>(component: Type<T>, api: Partial<Record<keyof InfoApiService, unknown>>, c: CreatorDto = creator()) {
    ref.close.mockClear();
    TestBed.configureTestingModule({
      imports: [component],
      providers: [
        provideNoopAnimations(),
        { provide: MAT_DIALOG_DATA, useValue: { creator: c, display: 'Synthetic Author' } },
        { provide: MatDialogRef, useValue: ref },
        { provide: InfoApiService, useValue: api },
      ],
    });
    const fixture = TestBed.createComponent(component);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const button = (id: string) => el.querySelector<HTMLButtonElement>(`[data-testid="${id}"]`);
    return { fixture, el, button };
  }

  it('Rename: sends the picked spelling and closes with the answer', () => {
    const renameCreator = vi.fn(() => of(creator({ displayName: 'S. Author', displayNameSource: 'admin' })));
    const { fixture, el, button } = render(CreatorRenameDialogComponent, { renameCreator });
    expect(button('creator-rename-save')!.disabled).toBe(true); // unchanged name
    expect(button('creator-rename-auto')).toBeNull(); // not an admin-set name
    const pick = Array.from(el.querySelectorAll<HTMLButtonElement>('.pick')).find((b) => b.textContent === 'S. Author')!;
    pick.click();
    fixture.detectChanges();
    button('creator-rename-save')!.click();
    expect(renameCreator).toHaveBeenCalledWith('cr_1', { displayName: 'S. Author' });
    expect(ref.close).toHaveBeenCalledWith(expect.objectContaining({ displayName: 'S. Author' }));
  });

  it('Rename: an admin-set name can go back to the automatic one', () => {
    const renameCreator = vi.fn(() => of(creator()));
    const { button } = render(CreatorRenameDialogComponent, { renameCreator }, creator({ displayNameSource: 'admin' }));
    button('creator-rename-auto')!.click();
    expect(renameCreator).toHaveBeenCalledWith('cr_1', { displayName: null });
  });

  it('Merge: finds the other creator by name (never itself), asks to confirm, then merges into it', () => {
    vi.useFakeTimers();
    try {
      const findCreators = vi.fn(() => of([creator(), creator({ id: 'cr_2', displayName: 'Synthetic Auteur', providerIds: ['mangaupdates:303'], works: 1 })]));
      const mergeCreator = vi.fn(() => of(creator({ id: 'cr_2', displayName: 'Synthetic Auteur' })));
      const { fixture, el, button } = render(CreatorMergeDialogComponent, { findCreators, mergeCreator });
      fixture.componentInstance.onQuery('Synth');
      vi.advanceTimersByTime(300);
      fixture.detectChanges();
      expect(findCreators).toHaveBeenCalledWith('Synth');
      const candidates = el.querySelectorAll<HTMLButtonElement>('[data-testid="creator-merge-candidate"]');
      expect(candidates.length).toBe(1);
      expect(candidates[0].textContent).toContain('1 work · MangaUpdates author 303');
      expect(button('creator-merge-save')).toBeNull();

      candidates[0].click();
      fixture.detectChanges();
      expect(el.querySelector('[data-testid="creator-merge-confirm"]')!.textContent).toContain('Merge Synthetic Author into Synthetic Auteur?');
      expect(mergeCreator).not.toHaveBeenCalled();
      button('creator-merge-save')!.click();
      expect(mergeCreator).toHaveBeenCalledWith('cr_1', { intoId: 'cr_2' });
      expect(ref.close).toHaveBeenCalledWith(expect.objectContaining({ id: 'cr_2' }));
    } finally {
      vi.useRealTimers();
    }
  });

  it('Merge: shows the server\'s refusal and stays open', () => {
    vi.useFakeTimers();
    try {
      const { fixture, el, button } = render(CreatorMergeDialogComponent, {
        findCreators: vi.fn(() => of([creator({ id: 'cr_2', displayName: 'Other' })])),
        mergeCreator: vi.fn(() => throwError(() => ({ error: 'same_creator', message: 'x' }))),
      });
      fixture.componentInstance.onQuery('Other');
      vi.advanceTimersByTime(300);
      fixture.detectChanges();
      el.querySelector<HTMLButtonElement>('[data-testid="creator-merge-candidate"]')!.click();
      fixture.detectChanges();
      button('creator-merge-save')!.click();
      fixture.detectChanges();
      expect(el.querySelector('[data-testid="creator-error"]')!.textContent).toContain('cannot be merged into itself');
      expect(ref.close).not.toHaveBeenCalled();
    } finally {
      vi.useRealTimers();
    }
  });

  it('Split: needs something to move and something to keep, confirms, then sends the chosen ids and names', () => {
    const splitCreator = vi.fn(() => of(creator({ id: 'cr_9', displayName: 'S. Author' })));
    const { fixture, el, button } = render(CreatorSplitDialogComponent, { splitCreator });
    expect(button('creator-split-next')!.disabled).toBe(true);
    expect(Array.from(el.querySelectorAll('[data-testid="creator-split-id"]')).map((c) => c.textContent!.trim()))
      .toEqual(['MangaUpdates author 101', 'MangaUpdates author 202']);

    const c = fixture.componentInstance;
    c.toggleId('mangaupdates:202');
    c.toggleName('S. Author');
    fixture.detectChanges();
    expect(button('creator-split-next')!.disabled).toBe(false);

    // Everything ticked: nothing would stay.
    c.toggleId('mangaupdates:101');
    c.toggleName('Synthetic Author');
    fixture.detectChanges();
    expect(button('creator-split-next')!.disabled).toBe(true);
    expect(el.textContent).toContain('Keep at least one name or id');
    c.toggleId('mangaupdates:101');
    c.toggleName('Synthetic Author');
    fixture.detectChanges();

    button('creator-split-next')!.click();
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="creator-split-confirm"]')!.textContent).toContain('Move 1 name and 1 id from Synthetic Author to a new creator?');
    expect(splitCreator).not.toHaveBeenCalled();
    button('creator-split-save')!.click();
    expect(splitCreator).toHaveBeenCalledWith('cr_1', { providerIds: ['mangaupdates:202'], names: ['S. Author'] });
    expect(ref.close).toHaveBeenCalledWith(expect.objectContaining({ id: 'cr_9' }));
  });

  it('labels: a creator line, provider ids and error words', () => {
    expect(creatorSummary(creator({ providerIds: [], works: 1 }))).toBe('1 work');
    expect(providerIdLabel('mangaupdates:42')).toBe('MangaUpdates author 42');
    expect(creatorErrorText({ status: 403 })).toBe('Only an admin can change creators.');
    expect(creatorErrorText({ error: 'split_all' })).toContain('Keep at least one');
    expect(creatorErrorText({ error: 'not_found' })).toContain('no longer exists');
  });
});

/**
 * The chip -> creator step. 1.47.0: a chip with `creatorId` reads that creator by id (no name lookup); a chip without it (an answer from
 * a server before 1.47.0) still finds the creator holding its value (lane F's `valueId`) by the chip's name.
 */
describe('CreatorAdminService', () => {
  function setup(found: CreatorDto[] | 'error', byId: CreatorDto | 'error' = creator()) {
    const open = vi.fn(() => ({ afterClosed: () => of(creator({ displayName: 'Renamed' })) }));
    const snack = vi.fn();
    const findCreators = vi.fn(() => (found === 'error' ? throwError(() => ({ status: 500 })) : of(found)));
    const getCreator = vi.fn(() => (byId === 'error' ? throwError(() => ({ status: 404 })) : of(byId)));
    TestBed.configureTestingModule({
      providers: [
        { provide: MatDialog, useValue: { open } },
        { provide: MatSnackBar, useValue: { open: snack } },
        // creatorForChip is the real one, over the mocked reads.
        { provide: InfoApiService, useFactory: () => {
          const real = Object.create(InfoApiService.prototype) as InfoApiService;
          return Object.assign(real, { findCreators, getCreator });
        } },
      ],
    });
    return { service: TestBed.inject(CreatorAdminService), open, snack, findCreators, getCreator };
  }

  const chip = { id: 'fv_a', display: 'Synthetic Author' };

  it('reads the creator by the chip\'s creatorId - no name lookup', async () => {
    const { service, open, findCreators, getCreator } = setup([], creator({ id: 'cr_9' }));
    const result = await service.split({ ...chip, creatorId: 'cr_9' });
    expect(getCreator).toHaveBeenCalledWith('cr_9');
    expect(findCreators).not.toHaveBeenCalled();
    expect(open).toHaveBeenCalledWith(CreatorSplitDialogComponent, expect.objectContaining({ data: { creator: expect.objectContaining({ id: 'cr_9' }), display: 'Synthetic Author' } }));
    expect(result?.displayName).toBe('Renamed');
  });

  it('says so when the creator of the id is gone', async () => {
    const { service, open, snack, findCreators } = setup([creator()], 'error');
    expect(await service.rename({ ...chip, creatorId: 'cr_gone' })).toBeNull();
    expect(findCreators).not.toHaveBeenCalled();
    expect(open).not.toHaveBeenCalled();
    expect(snack).toHaveBeenCalled();
  });

  it('without a creatorId, opens the dialog for the creator whose tag value the chip is', async () => {
    const { service, open, findCreators, getCreator } = setup([creator({ id: 'cr_0', valueId: 'fv_other' }), creator()]);
    const result = await service.rename(chip);
    expect(getCreator).not.toHaveBeenCalled();
    expect(findCreators).toHaveBeenCalledWith('Synthetic Author', 50);
    expect(open).toHaveBeenCalledWith(CreatorRenameDialogComponent, expect.objectContaining({ data: { creator: expect.objectContaining({ id: 'cr_1' }), display: 'Synthetic Author' } }));
    expect(result?.displayName).toBe('Renamed');
  });

  it('says so and opens nothing when no creator holds the value', async () => {
    const { service, open, snack } = setup([creator({ valueId: 'fv_other' })]);
    expect(await service.merge(chip)).toBeNull();
    expect(open).not.toHaveBeenCalled();
    expect(snack).toHaveBeenCalled();
    TestBed.resetTestingModule();
    const failed = setup('error');
    expect(await failed.service.split(chip)).toBeNull();
    expect(failed.open).not.toHaveBeenCalled();
  });
});
