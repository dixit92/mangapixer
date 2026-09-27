import { TestBed } from '@angular/core/testing';
import { MAT_DIALOG_DATA, MatDialogRef } from '@angular/material/dialog';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { ApiService } from '../../../core/api/api.service';
import { CatalogNodeDto } from '../../../core/api/api-types';
import { ReattachDialogComponent } from './reattach-dialog.component';

/** Folder picker for "Re-attach to..." (stage 2): the same library's folders only, via the browse API. */
describe('ReattachDialogComponent', () => {
  const node = (id: string, kind: 'Folder' | 'Archive', children = 0, availability = 'Available') =>
    ({ id, kind, displayName: `Name ${id}`, parentId: '', libraryId: 'lib1', availability, childFolderCount: children }) as CatalogNodeDto;

  function create() {
    const pages: Record<string, CatalogNodeDto[]> = {
      root: [node('f1', 'Folder', 2), node('a1', 'Archive'), node('gone', 'Folder', 0, 'Tombstoned')],
      f1: [node('f2', 'Folder')],
    };
    const api = {
      browseLibrary: vi.fn((_lib: string, parent: string | null) =>
        of({ items: pages[parent ?? 'root'] ?? [], totalCount: 0, nextCursor: null, hasMore: false })),
    };
    const ref = { close: vi.fn() };
    TestBed.configureTestingModule({
      imports: [ReattachDialogComponent],
      providers: [
        provideNoopAnimations(),
        { provide: ApiService, useValue: api },
        { provide: MatDialogRef, useValue: ref },
        { provide: MAT_DIALOG_DATA, useValue: { libraryId: 'lib1', libraryName: 'Library One', displayName: 'Old Name' } },
      ],
    });
    const fixture = TestBed.createComponent(ReattachDialogComponent);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const folders = () => { fixture.detectChanges(); return Array.from(el.querySelectorAll('[data-testid="reattach-folder"]')).map((f) => f.textContent!.trim()); };
    return { fixture, c: fixture.componentInstance, api, ref, folders };
  }

  it('lists live folders of the library (no archives, no removed folders) and browses into one', () => {
    const { c, api, folders } = create();
    expect(api.browseLibrary).toHaveBeenCalledWith('lib1', null, null, 100, 'name');
    expect(folders()).toEqual(['folder Name f1']);
    c.open(c.folders()[0]);
    expect(folders()).toEqual(['folder Name f2']);
    expect(c.crumbs().map((x) => x.name)).toEqual(['Library One', 'Name f1']);
    c.goTo(0);
    expect(folders()).toEqual(['folder Name f1']);
  });

  it('returns the chosen folder', () => {
    const { c, ref } = create();
    c.confirm();
    expect(ref.close).not.toHaveBeenCalled();
    c.chosen.set(c.folders()[0]);
    c.confirm();
    expect(ref.close).toHaveBeenCalledWith({ targetNodeId: 'f1', targetName: 'Name f1' });
  });
});
