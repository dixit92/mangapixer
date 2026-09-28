import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { SeriesInfoDto } from '../../../core/api/api-types';
import { MetadataApiService } from '../metadata-api.service';
import { MetadataStateService } from '../metadata-state.service';
import { SeriesAdminActionsComponent } from '../series-admin-actions.component';
import { seriesInfo } from '../series-info.testing';
import { DeclaredFactsDialogService } from './declared-facts-dialog.service';

/** The folder admin menu offers "Declared facts…" (1.28.0) for folders only, opening the editor in folder scope. */
describe('SeriesAdminActionsComponent - declared facts', () => {
  function open(info: SeriesInfoDto) {
    const dialog = { open: vi.fn(() => Promise.resolve(undefined)) };
    TestBed.configureTestingModule({
      imports: [SeriesAdminActionsComponent],
      providers: [
        provideNoopAnimations(),
        {
          provide: MetadataApiService,
          useValue: {
            getIdentifyContext: vi.fn(() => of({ fetchAvailable: false })),
            getFolderContent: vi.fn(() => throwError(() => ({ status: 501 }))),
          },
        },
        { provide: MetadataStateService, useValue: { refresh: vi.fn() } },
        { provide: DeclaredFactsDialogService, useValue: dialog },
      ],
    });
    const fixture = TestBed.createComponent(SeriesAdminActionsComponent);
    fixture.componentRef.setInput('info', info);
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('[data-testid="series-admin-menu"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    return { dialog, item: document.querySelector('[data-testid="declared-facts"]') as HTMLButtonElement | null };
  }

  it('opens the editor for the folder', () => {
    const { dialog, item } = open(seriesInfo({ nodeId: 'f1', nodeKind: 'Folder' }));
    item!.click();
    expect(dialog.open).toHaveBeenCalledWith({ kind: 'folder', id: 'f1' });
  });

  it('is not offered on an archive', () => {
    expect(open(seriesInfo({ nodeId: 'a1', nodeKind: 'Archive' })).item).toBeNull();
  });
});
