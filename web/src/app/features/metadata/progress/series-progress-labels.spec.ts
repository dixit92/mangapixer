import { SeriesProgressDto, SeriesTrackersDto } from '../../../core/api/api-types';
import {
  alsoInVolumeLabel, completionSentence, folderLine, numbersText, officialReleaseLabel, progressIcon, reachSentence, trackersLine,
  upgradeSentence,
} from './series-progress-labels';

/**
 * The progress wording (1.30.0, reach): every worked example of the lane R design - the owner's example (before and after the
 * English volume 15), a volumes-only folder, a chapters-only webtoon, a mixed folder with overlap, a complete series held whole,
 * a finished scanlation held whole, and French as the preferred language.
 */
describe('series progress labels', () => {
  function progress(trackers: Partial<SeriesTrackersDto>, over: Partial<SeriesProgressDto> = {}): SeriesProgressDto {
    return {
      trackers: { language: 'en', ...trackers },
      reach: null, missingVolumes: 0, missingChapters: 0, releaseKnown: true, upgradeVolumes: [], upgradeCount: 0, completion: 'None',
      ...over,
    };
  }
  const span = (from: number, to: number) => ({ from, to });
  const reach = (volumes: [number, number][], chapters: [number, number][]) => ({
    volumeFiles: volumes.map(([a, b]) => span(a, b)), chapters: chapters.map(([a, b]) => span(a, b)), overlapChapters: 0, resolution: 'VolumeList' as const,
  });

  const owner = {
    origin: 'Japan' as const, originStatus: 'Ongoing' as const, originVolumes: 22, officialPublisher: 'Yen Press', officialVolumes: 14,
    officialStatus: 'Ongoing' as const, latestChapter: 65, scanlationComplete: false,
  };

  it('the owner example: origin, the English publisher and the scanlation; volumes + chapters up to date', () => {
    const p = progress(owner, { reach: reach([[1, 14]], [[47, 65]]) });
    expect(trackersLine(p)).toBe('Ongoing (Japan): 22 volumes · English (Yen Press): 14 volumes, ongoing · English scanlation: to chapter 65');
    expect(folderLine(p)).toBe('You have volumes 1-14 + chapters 47-65 · up to date');
    expect(progressIcon(p)).toBe('check_circle_outline');
  });

  it('the owner example once the English volume 15 is out: an upgrade, never missing', () => {
    const p = progress({ ...owner, officialVolumes: 15 }, { reach: reach([[1, 14]], [[47, 65]]), upgradeVolumes: [15], upgradeCount: 1 });
    expect(folderLine(p)).toBe('You have volumes 1-14 + chapters 47-65 · up to date · Volume 15 available in English');
    expect(upgradeSentence(p)).toBe('Volume 15 available in English - you hold it as chapters');
    expect(progressIcon(p)).toBe('new_releases');
    const many = progress(owner, { upgradeVolumes: [15, 16, 17, 19], upgradeCount: 5 });
    expect(upgradeSentence(many)).toBe('Volumes 15-17, 19, +1 more available in English - you hold them as chapters');
  });

  it('a volumes-only folder of a series finished in English: missing volumes and the prompt', () => {
    const p = progress(
      { origin: 'Japan', originStatus: 'Complete', originVolumes: 14, officialPublisher: 'Viz Media', officialVolumes: 14, officialStatus: 'Complete' },
      { reach: reach([[1, 12]], []), missingVolumes: 2, completion: 'FinishedNotHeld', completionBasis: 'OfficialVolumes', completionTarget: 14, completionHeld: 12 });
    expect(trackersLine(p)).toBe('Complete (Japan): 14 volumes · English (Viz Media): 14 volumes, complete');
    expect(folderLine(p)).toBe('You have volumes 1-12 · 2 volumes missing · finished - official, English');
    expect(completionSentence(p)).toBe('Finished - Official, English (14 volumes) - you have 12');
    expect(progressIcon(p)).toBe('error_outline');
  });

  it('a chapters-only webtoon: holes and chapters released after the last one here', () => {
    const p = progress({ origin: 'Korea', originStatus: 'Ongoing', originChapters: 150, latestChapter: 150, scanlationComplete: false },
      { reach: reach([], [[1, 99], [101, 148]]), missingChapters: 3 });
    expect(trackersLine(p)).toBe('Ongoing (Korea): 150 chapters · English scanlation: to chapter 150');
    expect(folderLine(p)).toBe('You have chapters 1-99, 101-148 · 3 chapters missing');
    expect(reachSentence(p)).toBe('You have chapters 1-99, 101-148 · 3 chapters missing');
  });

  it('a mixed folder: the chapters inside a volume file are not repeated', () => {
    const p = progress({ origin: 'Japan', originStatus: 'Ongoing', originVolumes: 14, officialPublisher: 'Kodansha USA', officialVolumes: 10,
      officialStatus: 'Ongoing', latestChapter: 120 }, { reach: { ...reach([[1, 10]], [[91, 120]]), overlapChapters: 6 } });
    expect(folderLine(p)).toBe('You have volumes 1-10 + chapters 91-120 · up to date');
    expect(alsoInVolumeLabel('10')).toBe('Also in Volume 10');
    expect(alsoInVolumeLabel(null)).toBe('');
  });

  it('a finished series held whole is a complete collection', () => {
    const p = progress(
      { origin: 'Japan', originStatus: 'Complete', originVolumes: 14, officialPublisher: 'Viz Media', officialVolumes: 14, officialStatus: 'Complete' },
      { reach: reach([[1, 14]], []), completion: 'CompleteCollection', completionBasis: 'OfficialVolumes', completionTarget: 14, completionHeld: 14 });
    expect(folderLine(p)).toBe('You have volumes 1-14 · Complete collection - Official');
    expect(completionSentence(p)).toBe('Complete collection: all 14 volumes of the English edition');
    expect(progressIcon(p)).toBe('workspace_premium');
  });

  it('says which release finished: the official edition or the fan translation (owner, 1.30.0 RC)', () => {
    const fan = progress({ origin: 'Korea', originStatus: 'Complete', originChapters: 172, latestChapter: 172, scanlationComplete: true, licensed: false },
      { reach: reach([], [[1, 6]]), completion: 'FinishedNotHeld', completionBasis: 'AllChapters', completionTarget: 172, completionHeld: 6,
        completionInChapters: true });
    expect(completionSentence(fan)).toBe('Finished - Fan translation, English (172 chapters) - you have 6');
    expect(folderLine(fan)).toContain('finished - fan translation, English');
  });

  it('a finished scanlation held whole, and the origin run', () => {
    const scan = progress({ origin: 'Korea', originStatus: 'Complete', latestChapter: 120, scanlationComplete: true },
      { reach: reach([], [[1, 120]]), completion: 'CompleteCollection', completionBasis: 'AllChapters', completionTarget: 120, completionHeld: 120,
        completionInChapters: true });
    expect(trackersLine(scan)).toBe('Complete (Korea) · English scanlation: to chapter 120, complete');
    expect(folderLine(scan)).toBe('You have chapters 1-120 · Complete collection - Fan translation');
    expect(completionSentence(scan)).toBe('Complete collection: all 120 chapters of the finished English fan translation');
    const origin = progress({ originStatus: 'Complete', originVolumes: 14 }, { completion: 'CompleteCollection', completionBasis: 'OriginRun', completionTarget: 14 });
    expect(completionSentence(origin)).toBe('Complete collection: the whole original run (14 volumes)');
    // Finished in the origin only and not held: no prompt.
    expect(completionSentence(progress({}, { completion: 'FinishedNotHeld', completionBasis: 'OriginRun' }))).toBeNull();
  });

  it('French preferred: the released chapters, no English fact, the language named', () => {
    const p = progress({ language: 'fr', origin: 'Japan', originStatus: 'Ongoing', originVolumes: 22, releasedChapter: 87 },
      { reach: reach([], [[1, 87]]) });
    expect(trackersLine(p)).toBe('Ongoing (Japan): 22 volumes · French: to chapter 87');
    expect(folderLine(p)).toBe('You have chapters 1-87 · up to date');
    expect(officialReleaseLabel('fr')).toBe('Available in French');
    expect(officialReleaseLabel(null)).toBe('');
  });

  it('says what is unknown: no status word, not licensed, a dropped publisher, no numbers', () => {
    expect(trackersLine(progress({ origin: 'Japan', originVolumes: 5, licensed: false }))).toBe('Japan: 5 volumes · English: not licensed');
    expect(trackersLine(progress({ officialPublisher: 'Gone Press', officialVolumes: 4, officialStatus: 'Cancelled' }))).toBe('English (Gone Press): 4 volumes, dropped');
    expect(trackersLine(progress({ originVolumes: 3 }))).toBe('Original run: 3 volumes');
    expect(trackersLine(progress({}))).toBeNull();
    expect(folderLine(progress({}))).toBeNull();
    expect(folderLine(progress({}, { reach: reach([[3, 3]], [[5, 5]]), releaseKnown: false }))).toBe('You have volume 3 + chapter 5');
    expect(folderLine(progress({}, { reach: reach([], [[1, 2], [4, 5], [7, 9]]) }))).toBe('You have chapters 1-9 with gaps · up to date');
    expect(numbersText([19, 15, 16, 17, 15])).toBe('15-17, 19');
  });
});
