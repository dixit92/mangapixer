import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { TrackerReadMarkComponent } from './tracker-read-mark.component';

/** The card mark (1.43.0, owner's V2): a gold ring with the trackers' names for screen readers; nothing when none counts it. */
describe('TrackerReadMarkComponent', () => {
  function create(trackers: string[] | null | undefined) {
    TestBed.configureTestingModule({ imports: [TrackerReadMarkComponent], providers: [provideNoopAnimations()] });
    const fixture = TestBed.createComponent(TrackerReadMarkComponent);
    fixture.componentRef.setInput('trackers', trackers);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  afterEach(() => TestBed.resetTestingModule());

  it('names one tracker, or several', () => {
    let mark = create(['mal']).querySelector('[data-testid="tracker-read-mark"]')!;
    expect(mark.getAttribute('role')).toBe('img');
    expect(mark.getAttribute('aria-label')).toBe('Read on MyAnimeList');
    expect(mark.getAttribute('data-trackers')).toBe('mal');
    TestBed.resetTestingModule();
    mark = create(['mal', 'mangaupdates']).querySelector('[data-testid="tracker-read-mark"]')!;
    expect(mark.getAttribute('aria-label')).toBe('Read on MyAnimeList and MangaUpdates');
  });

  it('renders nothing for no tracker', () => {
    expect(create(null).querySelector('[data-testid="tracker-read-mark"]')).toBeNull();
    TestBed.resetTestingModule();
    expect(create([]).querySelector('[data-testid="tracker-read-mark"]')).toBeNull();
  });
});
