import {
  REVIEW_TABS,
  daysLabel,
  plural,
  overallScoreTip,
  reasonLabel,
  reasonTip,
  reviewCandidateLine,
  reviewTabDef,
  runProgress,
  scorePercent,
  scorePercentLabel,
  waitingLabel,
  workClassLabel,
} from './metadata-admin-labels';
import { contentCaption, contentSuggestion } from '../folder-content';

/** Display helpers of the admin metadata page (stage 2). Pure. */
describe('metadata admin labels', () => {
  it('lists the seven review tabs of design section 5 in order, each with a summary count', () => {
    expect(REVIEW_TABS.map((t) => t.tab)).toEqual(
      ['NeedsReview', 'AutoLinked', 'Unmatched', 'Flags', 'DontMatch', 'Confirmed', 'MissingFolders']);
    expect(reviewTabDef('Flags').count).toBe('openFlags');
    expect(reviewTabDef('MissingFolders').label).toBe('Missing folders');
  });

  it('names known reason codes and shows unknown ones readably', () => {
    expect(reasonLabel('close_second')).toBe('Close second');
    expect(reasonTip('count')).toContain('item count');
    expect(reasonLabel('some_new_code')).toBe('some new code');
    expect(reasonTip('some_new_code')).toBe('');
  });

  it('formats candidates, scores and work classes', () => {
    expect(reviewCandidateLine({ providerType: 'Manga', year: 2014, volumes: 14 })).toBe('Manga · 2014 · 14 vols');
    expect(reviewCandidateLine({ providerType: null, format: 'Novel', year: null, volumes: 1 })).toBe('Novel · 1 vol');
    expect(scorePercent(0.9449)).toBe(94);
    expect(scorePercent(null)).toBeNull();
    expect(scorePercentLabel(0.9449)).toBe('94%');
    expect(scorePercentLabel(1.4)).toBe('100%'); // clamped
    expect(scorePercentLabel(undefined)).toBe('');
    expect(workClassLabel('SeriesWithUnits')).toBe('Series with volume folders');
    expect(workClassLabel(null)).toBe('');
  });

  it('labels a review candidate\'s overall score distinctly from its title-only score', () => {
    expect(overallScoreTip({ titleScore: 1, adjustedScore: 0.92 })).toBe(
      'Overall 92%: title match 100%, adjusted for item count, year, type and origin evidence.');
    expect(overallScoreTip({ titleScore: null, adjustedScore: null })).toBe(
      'Overall unknown: title match unknown, adjusted for item count, year, type and origin evidence.');
  });

  it('computes run progress and explains waiting codes', () => {
    expect(runProgress({ processed: 5, queued: 20 })).toBe(25);
    expect(runProgress({ processed: 0, queued: 0 })).toBe(0);
    expect(runProgress({ processed: 30, queued: 20 })).toBe(100);
    expect(waitingLabel('budget_exhausted')).toContain('budget is spent');
    expect(waitingLabel('odd')).toBe('Waiting (odd).');
    expect(waitingLabel(null)).toBe('');
  });

  it('words estimates and counts', () => {
    expect(daysLabel(0)).toBe('no requests needed');
    expect(daysLabel(0.4)).toBe('within a day');
    expect(daysLabel(2.1)).toBe('about 3 days');
    expect(plural(1, 'folder')).toBe('1 folder');
    expect(plural(1200, 'request')).toBe('1,200 requests');
  });

  it('describes a folder Content value and its source', () => {
    expect(contentCaption(null)).toBe('Content');
    expect(contentCaption({ nodeId: 'a', content: 'NotDoujinshi', effective: 'NotDoujinshi' })).toBe('Content: Not doujinshi (set here)');
    expect(contentCaption({ nodeId: 'a', content: null, effective: 'DoujinshiAndAdultOneShots', sourceNodeId: 'p' }))
      .toBe('Content: Doujinshi & adult one-shots (inherited)');
    expect(contentCaption({ nodeId: 'a', effective: 'Auto' })).toBe('Content: Auto (default)');
    expect(contentSuggestion({ nodeId: 'a', effective: 'Auto', suggested: 'DoujinshiAndAdultOneShots' })).toBe('DoujinshiAndAdultOneShots');
    expect(contentSuggestion({ nodeId: 'a', effective: 'Auto', suggested: 'Auto' })).toBeNull();
  });
});
