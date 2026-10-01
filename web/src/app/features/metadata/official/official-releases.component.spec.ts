import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

import { OfficialReleaseRowDto, OfficialReleasesPageDto, SeriesProgressDto } from '../../../core/api/api-types';
import { CompletionMarkComponent } from './completion-mark.component';
import { OfficialReleasesComponent, officialEmptyText } from './official-releases.component';

/**
 * Official releases tab (1.30.0): loads "to act on" by default, switches filter and library, renders the upgrade, trackers and
 * folder lines with the completion mark, pages with Load more, and explains an empty list per language. Mocked HTTP backend.
 */
describe('OfficialReleasesComponent', () => {
  function progress(over: Partial<SeriesProgressDto> = {}): SeriesProgressDto {
    return {
      trackers: { language: 'en', origin: 'Japan', originStatus: 'Ongoing', originVolumes: 22, officialPublisher: 'Synthetic Press', officialVolumes: 15,
        officialStatus: 'Ongoing', latestChapter: 57 },
      reach: { volumeFiles: [{ from: 1, to: 14 }], chapters: [{ from: 43, to: 57 }], overlapChapters: 0, resolution: 'VolumeList', reachChapter: 57, reachVolume: 19 },
      missingVolumes: 0, missingChapters: 0, releaseKnown: true, upgradeVolumes: [15], upgradeCount: 1, completion: 'None', ...over,
    };
  }
  function row(over: Partial<OfficialReleaseRowDto> = {}): OfficialReleaseRowDto {
    return {
      nodeId: 'series-1', displayName: 'Synthetic Series', libraryId: 'lib1', libraryName: 'Library One', recordTitle: 'Synthetic Record',
      linkState: 'Auto', progress: progress(), ...over,
    };
  }
  function page(items: OfficialReleaseRowDto[], nextCursor: string | null = null, language = 'en'): OfficialReleasesPageDto {
    return { items, summary: { series: 3, upgrades: 1, finishedNotHeld: 1, completeCollections: 1 }, total: items.length, nextCursor, language };
  }
  function create() {
    TestBed.configureTestingModule({
      imports: [OfficialReleasesComponent],
      providers: [provideNoopAnimations(), provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    const fixture = TestBed.createComponent(OfficialReleasesComponent);
    fixture.componentRef.setInput('libraries', [{ id: 'lib1', name: 'Library One' }]);
    fixture.detectChanges();
    return { fixture, c: fixture.componentInstance, http: TestBed.inject(HttpTestingController), el: fixture.nativeElement as HTMLElement };
  }
  const text = (el: HTMLElement, id: string) => el.querySelector(`[data-testid="${id}"]`)?.textContent?.replace(/\s+/g, ' ').trim();

  it('loads the series to act on and renders their lines', () => {
    const { fixture, http, el } = create();
    const req = http.expectOne((r) => r.url === '/api/v1/admin/metadata/official-releases');
    expect(req.request.params.get('filter')).toBe('ToAct');
    expect(req.request.params.has('library')).toBe(false);
    req.flush(page([row()]));
    fixture.detectChanges();

    expect(text(el, 'official-summary')).toBe('3 linked series · 1 with official volumes to get · 1 finished, not complete · 1 complete collections');
    expect(text(el, 'official-upgrade')).toBe('Volume 15 available in English - you hold it as chapters');
    expect(text(el, 'official-trackers')).toBe('Ongoing (Japan): 22 volumes · English (Synthetic Press): 15 volumes, ongoing · English scanlation: to chapter 57');
    expect(text(el, 'official-folder')).toBe('You have volumes 1-14 + chapters 43-57');
    expect(el.querySelector('[data-testid="official-series-link"]')!.getAttribute('href')).toBe('/series/series-1');
    expect(el.querySelector('.auto')).not.toBeNull();
    expect(el.querySelector('[data-testid="completion-mark"]')).toBeNull();
    http.verify();
  });

  it('shows the completion mark and pages with Load more', () => {
    const { fixture, http, el, c } = create();
    const complete = row({ nodeId: 'series-2', progress: progress({ upgradeVolumes: [], upgradeCount: 0, completion: 'CompleteCollection',
      completionBasis: 'OfficialVolumes', completionTarget: 14, completionHeld: 14 }) });
    http.expectOne((r) => r.url === '/api/v1/admin/metadata/official-releases').flush(page([complete], '1'));
    fixture.detectChanges();
    expect(text(el, 'completion-mark')).toContain('Complete collection');

    (el.querySelector('[data-testid="official-more"]') as HTMLButtonElement).click();
    const more = http.expectOne((r) => r.url === '/api/v1/admin/metadata/official-releases');
    expect(more.request.params.get('cursor')).toBe('1');
    more.flush(page([row()]));
    fixture.detectChanges();
    expect(c.items().map((i) => i.nodeId)).toEqual(['series-2', 'series-1']);
  });

  it('switches filter and library, and explains an empty list', () => {
    const { fixture, http, el, c } = create();
    http.expectOne((r) => r.url === '/api/v1/admin/metadata/official-releases').flush(page([]));
    c.setFilter('Complete');
    c.setLibrary('lib1');
    const reqs = http.match((r) => r.url === '/api/v1/admin/metadata/official-releases');
    expect(reqs[1].request.params.get('filter')).toBe('Complete');
    expect(reqs[1].request.params.get('library')).toBe('lib1');
    reqs.forEach((r) => r.flush(page([], null, 'fr')));
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="official-empty"] p')!.textContent).toBe('No linked folder holds a finished series whole yet.');
    expect(el.querySelector('.intro')!.textContent).toContain('released in French');
  });

  it('empty-state wording names the language and why only English has official totals', () => {
    expect(officialEmptyText('Upgrades', 'en')).toBe('No linked series has an official English volume that you hold only as chapters.');
    expect(officialEmptyText('ToAct', 'fr')).toContain("your preferred language is French, so only finished series can appear here.");
    expect(officialEmptyText('All', 'en')).toBe('No linked series folders yet.');
  });

  it('reports a failed load', () => {
    const { fixture, http, el } = create();
    http.expectOne((r) => r.url === '/api/v1/admin/metadata/official-releases').flush('x', { status: 500, statusText: 'err' });
    fixture.detectChanges();
    expect(el.querySelector('[role="alert"]')!.textContent).toContain('could not be loaded');
  });
});

describe('CompletionMarkComponent', () => {
  it('shows the prompt for a series finished in the preferred language, and nothing otherwise', () => {
    const fixture = TestBed.createComponent(CompletionMarkComponent);
    fixture.componentRef.setInput('progress', {
      trackers: { language: 'en' }, missingVolumes: 2, missingChapters: 0, releaseKnown: true, upgradeVolumes: [], upgradeCount: 0,
      completion: 'FinishedNotHeld', completionBasis: 'OfficialVolumes', completionTarget: 14, completionHeld: 12,
    } satisfies SeriesProgressDto);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="completion-mark"]')!.textContent).toContain('Finished - Official, English (14 volumes) - you have 12');
    expect(el.querySelector('.prompt')).not.toBeNull();
    fixture.componentRef.setInput('progress', null);
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="completion-mark"]')).toBeNull();
  });
});
