import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { OverlayContainer } from '@angular/cdk/overlay';

import { CatalogNodeDto } from '../../core/api/api-types';
import { TrackerSyncActionComponent } from './tracker-sync-action.component';
import { summary, trackerAccount } from './trackers.testing';

function node(id: string, kind: CatalogNodeDto['kind']): CatalogNodeDto {
  return { id, kind, displayName: id, parentId: 'root', libraryId: 'L1' } as CatalogNodeDto;
}

/**
 * The browse selection's "Sync to trackers" (1.43.0, owner Q3): only for a user with a connected tracker; On / Off / As default
 * for the selected folders and archives (never a volume stack), with the Private-library and folder-covers-inside notes.
 */
describe('TrackerSyncActionComponent', () => {
  function create(trackers: ReturnType<typeof summary>[], selected = ['f1', 'a1', 'v1']) {
    TestBed.configureTestingModule({
      imports: [TrackerSyncActionComponent],
      providers: [provideNoopAnimations(), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(TrackerSyncActionComponent);
    fixture.componentRef.setInput('nodes', [node('f1', 'Folder'), node('a1', 'Archive'), node('v1', 'VolumeStack'), node('f2', 'Folder')]);
    fixture.componentRef.setInput('selected', new Set(selected));
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/v1/trackers').flush({ trackers });
    fixture.detectChanges();
    return { fixture, http, el: fixture.nativeElement as HTMLElement, overlay: TestBed.inject(OverlayContainer).getContainerElement() };
  }

  // Reset first: a failed verify must not leave the module instantiated for the next test (one failure, one red).
  afterEach(() => {
    const http = TestBed.inject(HttpTestingController);
    TestBed.resetTestingModule();
    http.verify();
  });

  it('is hidden without a connected tracker', () => {
    const { el } = create([summary(), summary({ slug: 'mangaupdates', allowed: false, account: trackerAccount({ tracker: 'mangaupdates' }) })]);
    expect(el.querySelector('[data-testid="tracker-sync-action"]')).toBeNull();
  });

  it('sets On for the selected folders and archives and explains Private libraries and folders', async () => {
    const { fixture, http, el, overlay } = create([summary({ account: trackerAccount() })]);
    const button = el.querySelector('[data-testid="tracker-sync-action"]') as HTMLButtonElement;
    expect(button).not.toBeNull();
    button.click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(overlay.querySelector('[data-testid="tracker-sync-caption"]')!.textContent).toContain('Sync to trackers (2)');
    const note = overlay.querySelector('[data-testid="tracker-sync-note"]')!.textContent!;
    expect(note).toContain("A folder's setting covers everything inside it");
    expect(note).toContain('Private libraries and doujinshi are not synced');
    (overlay.querySelector('[data-testid="tracker-sync-on"]') as HTMLButtonElement).click();
    const req = http.expectOne('/api/v1/trackers/sync-overrides');
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ nodeIds: ['f1', 'a1'], enabled: true });
    req.flush([]);
  });

  it.each([
    ['tracker-sync-off', false],
    ['tracker-sync-default', null],
  ])('%s sends enabled %s', async (testId, enabled) => {
    const { fixture, http, el, overlay } = create([summary({ account: trackerAccount() })], ['f2']);
    (el.querySelector('[data-testid="tracker-sync-action"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
    (overlay.querySelector(`[data-testid="${testId}"]`) as HTMLButtonElement).click();
    expect(http.expectOne('/api/v1/trackers/sync-overrides').request.body).toEqual({ nodeIds: ['f2'], enabled });
  });

  it('is off when only a volume stack is selected', () => {
    const { el } = create([summary({ account: trackerAccount() })], ['v1']);
    expect((el.querySelector('[data-testid="tracker-sync-action"]') as HTMLButtonElement).disabled).toBe(true);
  });
});
