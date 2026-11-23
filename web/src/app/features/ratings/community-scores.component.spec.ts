import { TestBed } from '@angular/core/testing';
import { CommunityScoresComponent, communityScoreLabel } from './community-scores.component';

describe('CommunityScoresComponent (1.54.0)', () => {
  it('shows one line per source with its own 0-10 number, named for screen readers', () => {
    const f = TestBed.createComponent(CommunityScoresComponent);
    f.componentRef.setInput('scores', [
      { source: 'mangaupdates', sourceName: 'MangaUpdates', score: 8.98, votes: 1200 },
      { source: 'mangadex', sourceName: 'MangaDex', score: 8.1, votes: 1 },
      { source: 'myanimelist', sourceName: 'MyAnimeList', score: 8.62, votes: null },
    ]);
    f.detectChanges();
    const items = Array.from((f.nativeElement as HTMLElement).querySelectorAll('li'));
    expect(items.map((li) => [li.querySelector('.name')!.textContent!.trim(), li.querySelector('.value')!.textContent!.trim()]))
      .toEqual([['MangaUpdates', '9.0'], ['MangaDex', '8.1'], ['MyAnimeList', '8.6']]);
    expect(items.map((li) => li.getAttribute('aria-label'))).toEqual([
      'MangaUpdates community score 8.98 of 10, 1,200 ratings',
      'MangaDex community score 8.1 of 10, 1 rating',
      'MyAnimeList community score 8.62 of 10',
    ]);
    expect(items.every((li) => li.getAttribute('role') === 'img')).toBe(true);
  });

  it('renders nothing without scores', () => {
    const f = TestBed.createComponent(CommunityScoresComponent);
    f.detectChanges();
    expect((f.nativeElement as HTMLElement).querySelector('[data-testid="community-scores"]')).toBeNull();
    expect(communityScoreLabel({ source: 'x', sourceName: 'X', score: 7 })).toBe('X community score 7 of 10');
  });
});
