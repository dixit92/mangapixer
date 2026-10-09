import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { DeclaredFactsScopeDto, LibraryDto, NodeDeclaredFactsDto } from '../../../core/api/api-types';
import { DeclaredFactsApiService } from './declared-facts-api.service';
import { DeclaredFactsDialogComponent } from './declared-facts-dialog.component';
import { DeclaredFactsDialogService } from './declared-facts-dialog.service';
import { DeclaredFactsLineComponent } from './declared-facts-line.component';
import { LibraryDeclaredFactsComponent } from './library-declared-facts.component';

const FOLDER_SCOPE: DeclaredFactsScopeDto = {
  nodeId: 'f1',
  libraryId: 'lib1',
  displayName: 'Synthetic Series',
  own: { type: null, creators: [] },
  inherited: {
    type: 'Manhwa', typeSource: 'Inherited', typeFrom: 'Synthetic Shelf',
    creators: [{ name: 'Library Author', role: 'author' }], creatorsSource: 'Library', creatorsFrom: 'Comics',
  },
};

function text(el: Element): string {
  return (el.textContent ?? '').replace(/\s+/g, ' ').trim();
}

/** Declared-facts editor (1.28.0): shows what is inherited, edits type + creator chips, saves / clears the scope. */
describe('DeclaredFactsDialogComponent', () => {
  function create(scope: DeclaredFactsScopeDto = FOLDER_SCOPE, kind: 'folder' | 'library' = 'folder') {
    const api = {
      get: vi.fn(() => of(scope)),
      set: vi.fn((_s: unknown, req: { type: unknown; creators: unknown }) => of({ ...scope, own: { type: req.type, creators: req.creators } })),
      clear: vi.fn(() => of({ ...scope, own: { type: null, creators: [] } })),
      setEdition: vi.fn((_id: string, req: { volumeTotal: number | null; edition: unknown; tracking: boolean }) =>
        of({ ...scope, own: { ...scope.own, edition: req } })),
    };
    const ref = { close: vi.fn() };
    TestBed.configureTestingModule({
      imports: [DeclaredFactsDialogComponent],
      providers: [
        provideNoopAnimations(),
        { provide: DeclaredFactsApiService, useValue: api },
        { provide: MatDialogRef, useValue: ref },
        { provide: MAT_DIALOG_DATA, useValue: { kind, id: kind === 'folder' ? 'f1' : 'lib1' } },
      ],
    });
    const fixture = TestBed.createComponent(DeclaredFactsDialogComponent);
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, c: fixture.componentInstance, api, ref };
  }

  it('shows what applies from above when nothing is declared here', () => {
    const { c, el, api } = create();
    expect(api.get).toHaveBeenCalledWith({ kind: 'folder', id: 'f1' });
    expect(text(el.querySelector('[data-testid="declared-lead"]')!)).toContain('For this folder and everything below it: Synthetic Series');
    expect(c.inheritedTypeText()).toBe('Inherit: Manhwa (Korea), from Synthetic Shelf');
    expect(text(el.querySelector('[data-testid="declared-creators-inherited"]')!))
      .toBe('Inherits Library Author (Story & art) (from the Comics library)');
    expect(el.querySelector('[data-testid="declared-clear"]')).toBeNull();
  });

  it('adds creators (deduped, trimmed, with a role), removes one, and saves the scope', () => {
    const { c, api, ref, fixture, el } = create();
    c.type.set('Webtoon');
    c.draftName.set('  A   Writer ');
    c.draftRole.set('writer');
    c.add();
    c.draftName.set('a writer');
    c.draftRole.set('writer');
    c.add();
    c.draftName.set('An Artist');
    c.draftRole.set('');
    c.add();
    c.draftName.set('Removed Name');
    c.add();
    c.remove(2);
    fixture.detectChanges();
    expect(text(el.querySelector('[data-testid="declared-creators"]')!)).toContain('A Writer · Story');

    // A typed but not yet added name is still saved.
    c.draftName.set('Last Typed');
    c.save();
    expect(api.set).toHaveBeenCalledWith({ kind: 'folder', id: 'f1' }, {
      type: 'Webtoon',
      creators: [{ name: 'A Writer', role: 'writer' }, { name: 'An Artist', role: null }, { name: 'Last Typed', role: null }],
    });
    expect(ref.close).toHaveBeenCalledWith(expect.objectContaining({ nodeId: 'f1' }));
  });

  it('clears an own declaration in library scope, and explains a failed save', () => {
    const own: DeclaredFactsScopeDto = { ...FOLDER_SCOPE, nodeId: null, displayName: 'Comics', own: { type: 'Manga', creators: [] }, inherited: {} };
    const { el, api, ref, c, fixture } = create(own, 'library');
    expect(text(el.querySelector('[data-testid="declared-lead"]')!)).toContain('For every folder in the library: Comics');
    expect(c.type()).toBe('Manga');
    (el.querySelector('[data-testid="declared-clear"]') as HTMLButtonElement).click();
    expect(api.clear).toHaveBeenCalledWith({ kind: 'library', id: 'lib1' });
    expect(ref.close).toHaveBeenCalled();

    api.set.mockReturnValueOnce(throwError(() => ({ error: 'creator_name_invalid' })));
    c.save();
    fixture.detectChanges();
    expect(text(el.querySelector('[data-testid="declared-error"]')!)).toContain('A creator name must be');
  });
});

/** 1.39.0: the folder's own edition facts in the editor - folders only, saved apart from the type / creators. */
describe('DeclaredFactsDialogComponent edition', () => {
  function create(scope: DeclaredFactsScopeDto = FOLDER_SCOPE, kind: 'folder' | 'library' = 'folder') {
    const api = {
      get: vi.fn(() => of(scope)),
      set: vi.fn(() => of(scope)),
      clear: vi.fn(() => of(scope)),
      setEdition: vi.fn((_id: string, req: unknown) => of({ ...scope, own: { ...scope.own, edition: req } })),
    };
    const ref = { close: vi.fn() };
    TestBed.configureTestingModule({
      imports: [DeclaredFactsDialogComponent],
      providers: [
        provideNoopAnimations(),
        { provide: DeclaredFactsApiService, useValue: api },
        { provide: MatDialogRef, useValue: ref },
        { provide: MAT_DIALOG_DATA, useValue: { kind, id: kind === 'folder' ? 'f1' : 'lib1' } },
      ],
    });
    const fixture = TestBed.createComponent(DeclaredFactsDialogComponent);
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, c: fixture.componentInstance, api, ref };
  }

  it('shows the edition controls for a folder only', () => {
    expect(create().el.querySelector('[data-testid="declared-volumes"]')).not.toBeNull();
    TestBed.resetTestingModule();
    const library: DeclaredFactsScopeDto = { ...FOLDER_SCOPE, nodeId: null, inherited: {} };
    const { el } = create(library, 'library');
    expect(el.querySelector('[data-testid="declared-volumes"]')).toBeNull();
    expect(el.querySelector('[data-testid="declared-tracking"]')).toBeNull();
  });

  it('loads the own edition, saves the type first and then the changed edition', () => {
    const scope: DeclaredFactsScopeDto = { ...FOLDER_SCOPE, own: { type: 'Manga', creators: [], edition: { volumeTotal: 10, edition: 'Omnibus', tracking: true } } };
    const { c, api, ref, el, fixture } = create(scope);
    expect([c.volumes(), c.edition(), c.tracking()]).toEqual([10, 'Omnibus', true]);
    expect(el.querySelector('[data-testid="declared-clear"]')).not.toBeNull();

    c.setVolumes('12');
    c.tracking.set(false);
    fixture.detectChanges();
    expect(text(el.querySelector('[data-testid="declared-tracking-off"]')!)).toContain('Completion, missing volumes and upgrades are not shown');
    c.save();
    expect(api.set).toHaveBeenCalledWith({ kind: 'folder', id: 'f1' }, { type: 'Manga', creators: [] });
    expect(api.setEdition).toHaveBeenCalledWith('f1', { volumeTotal: 12, edition: 'Omnibus', tracking: false });
    expect(ref.close).toHaveBeenCalledWith(expect.objectContaining({ own: expect.objectContaining({ edition: { volumeTotal: 12, edition: 'Omnibus', tracking: false } }) }));
  });

  it('does not save the edition when it did not change, and refuses a count out of range', () => {
    const { c, api, el, fixture } = create();
    c.save();
    expect(api.set).toHaveBeenCalledTimes(1);
    expect(api.setEdition).not.toHaveBeenCalled();

    c.setVolumes(0);
    c.save();
    fixture.detectChanges();
    expect(api.set).toHaveBeenCalledTimes(1);
    expect(text(el.querySelector('[data-testid="declared-error"]')!)).toContain('Volumes in this edition must be a whole number from 1 to 999');
    c.setVolumes('');
    expect(c.volumes()).toBeNull();
  });
});

/** The "Declared" line (Info panel, series page): type + creators with their source, and a conflict badge with both sides. */
describe('DeclaredFactsLineComponent', () => {
  function create(dto: NodeDeclaredFactsDto | 'error') {
    const version = signal(0);
    const api = { version, forNode: vi.fn(() => (dto === 'error' ? throwError(() => ({ error: 'x' })) : of(dto))) };
    TestBed.configureTestingModule({ imports: [DeclaredFactsLineComponent], providers: [{ provide: DeclaredFactsApiService, useValue: api }] });
    const fixture = TestBed.createComponent(DeclaredFactsLineComponent);
    fixture.componentRef.setInput('nodeId', 'n1');
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement, api, version };
  }

  it('renders nothing when nothing is declared, or when the read fails', () => {
    expect(create({ nodeId: 'n1', effective: { creators: [] } }).el.querySelector('[data-testid="declared-line"]')).toBeNull();
    TestBed.resetTestingModule();
    expect(create('error').el.querySelector('[data-testid="declared-line"]')).toBeNull();
  });

  it('shows the declared type and creators, without a badge when they agree', () => {
    const { el, api } = create({
      nodeId: 'n1',
      effective: { type: 'Manga', typeSource: 'Library', typeFrom: 'Comics', creators: [{ name: 'Web Author', role: 'author' }], creatorsSource: 'Own' },
    });
    expect(api.forNode).toHaveBeenCalledWith('n1');
    expect(text(el.querySelector('[data-testid="declared-line"]')!)).toBe('Declared: Manga (Japan) · Web Author (Story & art)');
    expect(el.querySelector('[data-testid="declared-conflict"]')).toBeNull();
  });

  it('shows a conflict badge with the record side next to the declaration', () => {
    const { el } = create({
      nodeId: 'n1',
      effective: { type: 'Manhwa', typeSource: 'Own', creators: [{ name: 'Someone Else' }], creatorsSource: 'Own' },
      conflict: { providerName: 'MangaUpdates', type: true, recordType: 'Manga', creators: true, recordCreators: ['Web Author'] },
    });
    expect(text(el.querySelector('[data-testid="declared-type"]')!)).toBe('Manhwa (Korea)');
    const conflict = el.querySelector('[data-testid="declared-conflict"]')!;
    expect(conflict.getAttribute('role')).toBe('note');
    expect(text(conflict)).toContain('Conflict MangaUpdates says: Manga · Web Author');
  });

  it('shows the own edition of the folder, also when nothing else is declared (1.39.0)', () => {
    const { el } = create({ nodeId: 'n1', effective: { creators: [] }, edition: { volumeTotal: 12, edition: 'Omnibus', tracking: false } });
    expect(text(el.querySelector('[data-testid="declared-line"]')!)).toBe('Declared: Omnibus - 12 volumes · Completion not tracked');
    TestBed.resetTestingModule();
    const both = create({ nodeId: 'n1', effective: { type: 'Manga', creators: [] }, edition: { volumeTotal: 1, tracking: true } });
    expect(text(both.el.querySelector('[data-testid="declared-line"]')!)).toBe('Declared: Manga (Japan) · 1 volume');
  });

  it('reads again after a declared-facts change', () => {
    const { api, version, fixture } = create({ nodeId: 'n1', effective: { type: 'Novel' } });
    expect(api.forNode).toHaveBeenCalledTimes(1);
    version.set(1);
    fixture.detectChanges();
    expect(api.forNode).toHaveBeenCalledTimes(2);
  });
});

/** Library row of the admin Libraries card: a one-line summary and Edit (library scope). */
describe('LibraryDeclaredFactsComponent', () => {
  const LIB: LibraryDto = { id: 'lib1', name: 'Comics', isScanning: false, itemCount: 3, lastScanCompleted: null, defaultReaderMode: null, icon: null };

  it('summarizes the library declaration and opens the editor in library scope', async () => {
    const scope: DeclaredFactsScopeDto = { ...FOLDER_SCOPE, nodeId: null, own: { type: 'Manga', creators: [{ name: 'A' }, { name: 'B' }] }, inherited: {} };
    const api = { get: vi.fn(() => of({ ...scope, own: { creators: [] } })) };
    const dialog = { open: vi.fn(async () => scope) };
    TestBed.configureTestingModule({
      imports: [LibraryDeclaredFactsComponent],
      providers: [{ provide: DeclaredFactsApiService, useValue: api }, { provide: DeclaredFactsDialogService, useValue: dialog }],
    });
    const fixture = TestBed.createComponent(LibraryDeclaredFactsComponent);
    fixture.componentRef.setInput('library', LIB);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(api.get).toHaveBeenCalledWith({ kind: 'library', id: 'lib1' });
    expect(text(el.querySelector('[data-testid="library-declared-summary"]')!)).toBe('nothing');

    (el.querySelector('[data-testid="library-declared-edit"]') as HTMLButtonElement).click();
    expect(dialog.open).toHaveBeenCalledWith({ kind: 'library', id: 'lib1' });
    await fixture.whenStable();
    fixture.detectChanges();
    expect(text(el.querySelector('[data-testid="library-declared-summary"]')!)).toBe('Manga (Japan) · 2 creators');
  });
});
