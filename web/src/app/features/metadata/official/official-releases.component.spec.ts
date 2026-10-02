import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

import { OfficialReleaseRowDto, OfficialReleasesPageDto, OfficialReleasesSummaryDto, SeriesProgressDto } from '../../../core/api/api-types';
import { CompletionMarkComponent } from './completion-mark.component';
import { OfficialReleasesComponent, completionEmptyText, filterCount } from './official-releases.component';
import { SeriesAnswerChipComponent } from './series-answer-chip.component';

/**
 * The Completion tab (1.32.0; the Official releases tab of 1.30.0): loads "Have it all" by default with the answer counts in the
 * filter labels, switches answer, Upgrades only, edition (only for the two Finished answers) and library, renders the answer chip,
 * its sentence, the trackers, the folder and the upgrade lines, pages with Load more, and explains an empty list. Mocked HTTP backend.
 */
describe('OfficialReleasesComponent (Completion tab)', () => {
  const url = '/api/v1/admin/metadata/official-releases';

  function progress(over: Partial<SeriesProgressDto> = {}): SeriesProgressDto {
    return {
      trackers: { language: 'en', origin: 'Korea', originStatus: 'Complete', originChapters: 172, latestChapter: 172, scanlationComplete: true },
      reach: { volumeFiles: [], chapters: [{ from: 1, to: 172 }], overlapChapters: 0, resolution: 'FileNames', reachChapter: 172 },
      missingVolumes: 0, missingChapters: 0, releaseKnown: true, upgradeVolumes: [], upgradeCount: 0, completion: 'CompleteCollection',
      completionBasis: 'AllChapters', completionTarget: 172, completionHeld: 172, completionInChapters: true, answer: 'HaveItAll',
      answerReason: 'None', ...over,
    };
  }
  function row(over: Partial<OfficialReleaseRowDto> = {}): OfficialReleaseRowDto {
    return {
      nodeId: 'series-1', displayName: 'Synthetic Series', libraryId: 'lib1', libraryName: 'Library One', recordTitle: 'Synthetic Record',
      linkState: 'Auto', progress: progress(), ...over,
    };
  }
  const summary: OfficialReleasesSummaryDto = {
    series: 9, upgrades: 1, finishedNotHeld: 1, completeCollections: 2, haveItAll: 2, finishedMissing: 1, upToDate: 3, missingSome: 0, cantTell: 3,
  };
  function page(items: OfficialReleaseRowDto[], nextCursor: string | null = null, language = 'en'): OfficialReleasesPageDto {
    return { items, summary, total: items.length, nextCursor, language };
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

  it('loads Have it all by default and renders the answer, its edition and why', () => {
    const { fixture, http, el } = create();
    const req = http.expectOne((r) => r.url === url);
    expect(req.request.params.get('answer')).toBe('HaveItAll');
    expect(req.request.params.get('filter')).toBe('All');
    expect(req.request.params.has('upgrades')).toBe(false);
    expect(req.request.params.has('library')).toBe(false);
    req.flush(page([row()]));
    fixture.detectChanges();

    expect(text(el, 'official-filter-HaveItAll')).toBe('Have it all 2');
    expect(text(el, 'official-filter-MissingSome')).toBe('Missing some 0');
    expect(text(el, 'official-filter-All')).toBe('All 9');
    expect(text(el, 'official-summary')).toBe('9 linked series · 1 with an upgrade available');
    expect(text(el, 'series-answer')).toContain('Finished - you have it all');
    expect(text(el, 'series-edition')).toBe('Chapter-based');
    expect(text(el, 'official-answer')).toBe('Ended in Korea, and every chapter is out in English: you have all 172 chapters.');
    expect(text(el, 'official-trackers')).toBe('Complete (Korea): 172 chapters · English chapters: to chapter 172, complete');
    expect(text(el, 'official-folder')).toBe('You have chapters 1-172');
    expect(el.querySelector('[data-testid="official-upgrade"]')).toBeNull();
    expect(el.querySelector('[data-testid="series-upgrade"]')).toBeNull();
    expect(el.querySelector('[data-testid="official-series-link"]')!.getAttribute('href')).toBe('/series/series-1');
    expect(el.querySelector('.auto')).not.toBeNull();
    http.verify();
  });

  it('switches answer; the edition applies only to the Finished answers', () => {
    const { fixture, http, el, c } = create();
    http.expectOne((r) => r.url === url).flush(page([row()]));
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="official-edition"]')).not.toBeNull();
    c.setBasis('OfficialChapters');
    expect(http.expectOne((r) => r.url === url).request.params.get('basis')).toBe('OfficialChapters');

    (el.querySelector('[data-testid="official-filter-UpToDate"] button') as HTMLButtonElement).click();
    const soFar = http.expectOne((r) => r.url === url);
    expect(soFar.request.params.get('answer')).toBe('UpToDate');
    expect(soFar.request.params.has('basis')).toBe(false); // the edition does not apply to the other answers
    soFar.flush(page([row({ progress: progress({ trackers: { language: 'en', origin: 'Japan', originStatus: 'Ongoing' },
      completion: 'None', completionBasis: null, answer: 'UpToDate', answerReason: 'Running' }) })]));
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="official-edition"]')).toBeNull();
    expect(text(el, 'series-answer')).toContain('Everything released so far');
    expect(el.querySelector('[data-testid="series-edition"]')).toBeNull();
    expect(text(el, 'official-answer')).toBe('Still running in Japan: you have everything out in English so far.');

    (el.querySelector('[data-testid="official-filter-All"] button') as HTMLButtonElement).click();
    const all = http.expectOne((r) => r.url === url);
    expect(all.request.params.has('answer')).toBe(false);
    expect(all.request.params.get('filter')).toBe('All');
    http.verify();
  });

  it('Upgrades only shows the upgrade flag and sentence', () => {
    const { fixture, http, el, c } = create();
    http.expectOne((r) => r.url === url).flush(page([]));
    c.setUpgrades(true);
    const req = http.expectOne((r) => r.url === url);
    expect(req.request.params.get('upgrades')).toBe('true');
    req.flush(page([row({ progress: progress({ upgradeVolumes: [15], upgradeCount: 1 }) })]));
    fixture.detectChanges();
    expect(text(el, 'series-upgrade')).toContain('Upgrade available');
    expect(text(el, 'official-upgrade')).toBe('Volume 15 available in English - you hold it as chapters');
  });

  it('pages with Load more and switches library', () => {
    const { fixture, http, el, c } = create();
    http.expectOne((r) => r.url === url).flush(page([row({ nodeId: 'series-2' })], '1'));
    fixture.detectChanges();
    (el.querySelector('[data-testid="official-more"]') as HTMLButtonElement).click();
    const more = http.expectOne((r) => r.url === url);
    expect(more.request.params.get('cursor')).toBe('1');
    more.flush(page([row()]));
    expect(c.items().map((i) => i.nodeId)).toEqual(['series-2', 'series-1']);

    c.setLibrary('lib1');
    expect(http.expectOne((r) => r.url === url).request.params.get('library')).toBe('lib1');
  });

  it('explains an empty list per answer, per language and for Upgrades only', () => {
    const { fixture, http, el } = create();
    http.expectOne((r) => r.url === url).flush(page([], null, 'fr'));
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="official-empty"] p')!.textContent)
      .toBe("No linked series here has ended with all of it in your folder. Official volume totals are known only for English (MangaUpdates' English publishers); your preferred language is French.");
    expect(completionEmptyText('CantTell', 'en')).toBe('MangaPixer can compare every linked series here.');
    expect(completionEmptyText('All', 'en')).toBe('No linked series folders yet.');
    expect(completionEmptyText('HaveItAll', 'en', true)).toBe('No linked series here has an official English volume that you hold only as chapters.');
  });

  it('counts a filter from the summary; All is every answer', () => {
    expect(filterCount(null, 'All')).toBeNull();
    expect(filterCount(summary, 'CantTell')).toBe(3);
    expect(filterCount(summary, 'All')).toBe(9);
    expect(filterCount({ series: 1, upgrades: 0, finishedNotHeld: 0, completeCollections: 0 }, 'HaveItAll')).toBe(0);
  });

  it('reports a failed load', () => {
    const { fixture, http, el } = create();
    http.expectOne((r) => r.url === url).flush('x', { status: 500, statusText: 'err' });
    fixture.detectChanges();
    expect(el.querySelector('[role="alert"]')!.textContent).toContain('could not be loaded');
  });
});

describe('CompletionMarkComponent', () => {
  it('shows the two Finished answers in the approved words, and nothing otherwise', () => {
    const fixture = TestBed.createComponent(CompletionMarkComponent);
    const base: SeriesProgressDto = {
      trackers: { language: 'en', origin: 'Japan', originStatus: 'Complete' }, missingVolumes: 2, missingChapters: 0, releaseKnown: true,
      upgradeVolumes: [], upgradeCount: 0, completion: 'FinishedNotHeld', completionBasis: 'OfficialVolumes', completionTarget: 14,
      completionHeld: 12, answer: 'FinishedMissing',
    };
    fixture.componentRef.setInput('progress', base);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="completion-mark"]')!.textContent).toContain('Finished - missing some (Official, 12 of 14)');
    expect(el.querySelector('.prompt')).not.toBeNull();

    fixture.componentRef.setInput('progress', { ...base, completion: 'CompleteCollection', completionHeld: 14, answer: 'HaveItAll' });
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="completion-mark"]')!.textContent).toContain('Finished - you have it all (Official)');
    expect(el.querySelector('.complete')).not.toBeNull();

    fixture.componentRef.setInput('progress', { ...base, completion: 'None', answer: 'UpToDate', answerReason: 'Running' });
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="completion-mark"]')).toBeNull();
    fixture.componentRef.setInput('progress', null);
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="completion-mark"]')).toBeNull();
  });
});

describe('SeriesAnswerChipComponent', () => {
  it('renders each answer with its icon, and nothing without an answer', () => {
    const fixture = TestBed.createComponent(SeriesAnswerChipComponent);
    const el = fixture.nativeElement as HTMLElement;
    const cases: [SeriesProgressDto['answer'], string, string][] = [
      ['FinishedMissing', 'flag', 'Finished - missing some'],
      ['MissingSome', 'error_outline', 'Missing some'],
      ['CantTell', 'help_outline', "Can't tell"],
    ];
    for (const [answer, icon, label] of cases) {
      fixture.componentRef.setInput('progress', {
        trackers: { language: 'en' }, missingVolumes: 0, missingChapters: 1, releaseKnown: true, upgradeVolumes: [], upgradeCount: 0,
        completion: 'None', answer, answerReason: 'NoNumbers',
      } satisfies SeriesProgressDto);
      fixture.detectChanges();
      expect(el.querySelector('[data-testid="series-answer"] mat-icon')!.textContent).toBe(icon);
      expect(el.querySelector('[data-testid="series-answer"] span')!.textContent).toBe(label);
      expect(el.querySelector(`.${answer}`)).not.toBeNull();
    }
    fixture.componentRef.setInput('progress', null);
    fixture.detectChanges();
    expect(el.querySelector('[data-testid="series-answer"]')).toBeNull();
  });
});
