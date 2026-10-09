import { SeriesProgressDto, SeriesTrackersDto } from '../../../core/api/api-types';
import {
  alsoInVolumeLabel, answerSentence, completionMarkLabel, editionLabel, folderLine, numbersText, officialReleaseLabel, progressIcon,
  reachSentence, trackersLine, upgradeSentence,
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

  it('an edition declared on the folder (1.39.0): its volumes first, the edition named in the mark and the sentence', () => {
    const ended = { origin: 'Japan' as const, originStatus: 'Complete' as const, originVolumes: 30, officialVolumes: 30, officialStatus: 'Complete' as const };
    const all = progress(ended, {
      reach: reach([[1, 10]], []), volumeTotalOverride: 10, edition: 'Omnibus', completion: 'CompleteCollection', completionBasis: 'Edition',
      completionTarget: 10, completionHeld: 10, answer: 'HaveItAll',
    });
    expect(trackersLine(all)).toBe('Omnibus edition: 10 volumes · Complete (Japan): 30 volumes · English: 30 volumes, complete');
    expect(completionMarkLabel(all)).toBe('Finished - you have it all (Omnibus edition)');
    expect(answerSentence(all)).toBe('Ended in Japan: you have all 10 volumes of the omnibus edition.');
    const some = progress(ended, {
      reach: reach([[1, 2]], []), volumeTotalOverride: 6, missingVolumes: 4, completion: 'FinishedNotHeld', completionBasis: 'Edition',
      completionTarget: 6, completionHeld: 2, answer: 'FinishedMissing',
    });
    expect(trackersLine(some)).toContain('This edition: 6 volumes');
    expect(editionLabel(some)).toBe('Your edition');
    expect(answerSentence(some)).toBe('Ended in Japan: you have 2 of the 6 volumes of this edition.');
  });

  it('tracking off (1.39.0): what the folder holds, then "Completion not tracked" - no answer', () => {
    const p = progress(owner, { reach: reach([[1, 14]], []), trackingOff: true, answer: 'CantTell', answerReason: 'NotTracked' });
    expect(folderLine(p)).toBe('You have volumes 1-14 · completion not tracked');
    expect(folderLine({ ...p, reach: null })).toBe('Completion not tracked');
    expect(progressIcon(p)).toBe('remove_circle_outline');
    expect(completionMarkLabel(p)).toBeNull();
    expect(answerSentence(p)).toBe('Completion not tracked: an admin turned it off for this folder.');
  });

  it('the owner example: origin, the English publisher and the scanlation; volumes + chapters up to date', () => {
    const p = progress(owner, { reach: reach([[1, 14]], [[47, 65]]) });
    expect(trackersLine(p)).toBe('Ongoing (Japan): 22 volumes · English (Yen Press): 14 volumes, ongoing · English chapters: to chapter 65');
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
      { reach: reach([[1, 12]], []), missingVolumes: 2, completion: 'FinishedNotHeld', completionBasis: 'OfficialVolumes', completionTarget: 14, completionHeld: 12,
        answer: 'FinishedMissing' });
    expect(trackersLine(p)).toBe('Complete (Japan): 14 volumes · English (Viz Media): 14 volumes, complete');
    expect(folderLine(p)).toBe('You have volumes 1-12 · 2 volumes missing · finished - missing some (Official, 12 of 14)');
    expect(completionMarkLabel(p)).toBe('Finished - missing some (Official, 12 of 14)');
    expect(answerSentence(p)).toBe('Ended in Japan, and the English edition is complete: you have 12 of 14 volumes.');
    expect(progressIcon(p)).toBe('error_outline');
  });

  it('a chapters-only webtoon: holes and chapters released after the last one here', () => {
    const p = progress({ origin: 'Korea', originStatus: 'Ongoing', originChapters: 150, latestChapter: 150, scanlationComplete: false },
      { reach: reach([], [[1, 99], [101, 148]]), missingChapters: 3 });
    expect(trackersLine(p)).toBe('Ongoing (Korea): 150 chapters · English chapters: to chapter 150');
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
      { reach: reach([[1, 14]], []), completion: 'CompleteCollection', completionBasis: 'OfficialVolumes', completionTarget: 14, completionHeld: 14,
        answer: 'HaveItAll' });
    expect(folderLine(p)).toBe('You have volumes 1-14 · finished - you have it all (Official)');
    expect(answerSentence(p)).toBe('Ended in Japan, and the English edition is complete: you have all 14 volumes.');
    expect(progressIcon(p)).toBe('workspace_premium');
  });

  it('says which release finished: the official edition or the chapters (owner, 1.30.0 RC; 1.30.1 Chapter-based)', () => {
    const fan = progress({ origin: 'Korea', originStatus: 'Complete', originChapters: 172, latestChapter: 172, scanlationComplete: true, licensed: false },
      { reach: reach([], [[1, 6]]), completion: 'FinishedNotHeld', completionBasis: 'AllChapters', completionTarget: 172, completionHeld: 6,
        completionInChapters: true, answer: 'FinishedMissing' });
    expect(answerSentence(fan)).toBe('Ended in Korea, and every chapter is out in English: you have 6 of 172 chapters.');
    // Never "up to date" next to a Finished - missing some.
    expect(folderLine(fan)).toBe('You have chapters 1-6 · finished - missing some (Chapter-based, 6 of 172)');
  });

  it('a finished scanlation held whole, and the origin run', () => {
    const scan = progress({ origin: 'Korea', originStatus: 'Complete', latestChapter: 120, scanlationComplete: true },
      { reach: reach([], [[1, 120]]), completion: 'CompleteCollection', completionBasis: 'AllChapters', completionTarget: 120, completionHeld: 120,
        completionInChapters: true, answer: 'HaveItAll' });
    expect(trackersLine(scan)).toBe('Complete (Korea) · English chapters: to chapter 120, complete');
    expect(folderLine(scan)).toBe('You have chapters 1-120 · finished - you have it all (Chapter-based)');
    expect(answerSentence(scan)).toBe('Ended in Korea, and every chapter is out in English: you have all 120 chapters.');
    const origin = progress({ originStatus: 'Complete', originVolumes: 14 },
      { completion: 'CompleteCollection', completionBasis: 'OriginRun', completionTarget: 14, answer: 'HaveItAll' });
    expect(answerSentence(origin)).toBe('Ended: you have all 14 volumes of the original run.');
    expect(completionMarkLabel(origin)).toBe('Finished - you have it all (Original run)');
    // The other answers carry no mark.
    expect(completionMarkLabel(progress({}, { answer: 'UpToDate', answerReason: 'Running' }))).toBeNull();
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
  describe('the answer of the Completion tab (1.32.0, owner-approved wording)', () => {
    const japan = { origin: 'Japan' as const, originVolumes: 14 };

    it('says why a series is finished and held whole, per edition', () => {
      const official = progress({ ...japan, originStatus: 'Cancelled' },
        { answer: 'HaveItAll', completion: 'CompleteCollection', completionBasis: 'OfficialVolumes', completionTarget: 14 });
      expect(answerSentence(official)).toBe('Cancelled in Japan, and the English edition is complete: you have all 14 volumes.');
      const plus = progress({ ...japan, originStatus: 'Complete' }, { answer: 'HaveItAll', completion: 'CompleteCollection',
        completionBasis: 'OfficialChapters', completionTarget: 60, completionInChapters: true });
      expect(answerSentence(plus)).toBe('Ended in Japan, and every chapter is out officially in English: you have all 60 chapters.');
      expect(editionLabel(plus)).toBe('Official chapters');
      const oneShot = progress({ ...japan, originStatus: 'Complete', originVolumes: 1 }, { answer: 'HaveItAll', answerReason: 'OneShot',
        completion: 'CompleteCollection', completionBasis: 'OriginRun', completionTarget: 1, completionHeld: 1 });
      expect(answerSentence(oneShot)).toBe('A one-shot, ended in Japan: you have it.');
      expect(editionLabel(oneShot)).toBe('One-shot');
      expect(completionMarkLabel(oneShot)).toBe('Finished - you have it all (One-shot)');
      // A one-volume run whose file is numbered reads the same (review instance, 1.32.0): never "all 1 volume".
      const single = progress({ ...japan, originStatus: 'Complete', originVolumes: 1 }, { answer: 'HaveItAll', completion: 'CompleteCollection',
        completionBasis: 'OriginRun', completionTarget: 1, completionHeld: 1 });
      expect(answerSentence(single)).toBe('A one-shot, ended in Japan: you have it.');
      expect(editionLabel(single)).toBe('One-shot');
      const one = progress({ ...japan, originStatus: 'Complete' }, { answer: 'HaveItAll', completion: 'CompleteCollection',
        completionBasis: 'OfficialVolumes', completionTarget: 1 });
      expect(answerSentence(one)).toBe('Ended in Japan, and the English edition is complete: you have its one volume.');
    });

    it('an ended series with released ones not here, without an edition', () => {
      const p = progress({ ...japan, originStatus: 'Complete' }, { answer: 'FinishedMissing', missingVolumes: 2, missingChapters: 3 });
      expect(answerSentence(p)).toBe('Ended in Japan: 2 volumes, 3 chapters out in English are not here.');
      expect(editionLabel(p)).toBeNull();
      expect(completionMarkLabel(p)).toBe('Finished - missing some');
      expect(answerSentence(progress({ ...japan, originStatus: 'Complete' }, { answer: 'FinishedMissing', missingChapters: 1 })))
        .toBe('Ended in Japan: 1 chapter out in English is not here.');
    });

    it('everything released so far, by reason', () => {
      const so = (reason: SeriesProgressDto['answerReason'], t: Partial<SeriesTrackersDto> = {}) =>
        answerSentence(progress({ ...japan, ...t }, { answer: 'UpToDate', answerReason: reason }));
      expect(so('Running', { originStatus: 'Ongoing' })).toBe('Still running in Japan: you have everything out in English so far.');
      expect(so('OnHiatus', { originStatus: 'Hiatus' })).toBe('On hiatus in Japan: you have everything out in English so far.');
      expect(so('StatusUnknown')).toBe('You have everything out in English so far; MangaUpdates does not say whether the series has ended.');
      expect(so('WaitingForLanguage', { originStatus: 'Complete' }))
        .toBe('Ended in Japan, but not all of it is out in English yet: you have everything out so far.');
      expect(so('WaitingForLanguage', { originStatus: 'Complete', originVolumes: 12, officialVolumes: 10 }))
        .toBe('Ended in Japan, but English volumes are still coming (10 of 12): you have everything out so far.');
      expect(so('LanguageEditionDropped', { originStatus: 'Complete', officialVolumes: 7, officialStatus: 'Cancelled' }))
        .toBe('Ended in Japan; the English edition stopped after 7 volumes, and you have all of them.');
    });

    it('missing some, and can\'t tell', () => {
      expect(answerSentence(progress({ ...japan, originStatus: 'Ongoing' }, { answer: 'MissingSome', answerReason: 'Running', missingChapters: 3 })))
        .toBe('Still running in Japan: 3 chapters out in English are not here.');
      expect(answerSentence(progress({ language: 'fr' }, { answer: 'MissingSome', answerReason: 'StatusUnknown', missingVolumes: 1 })))
        .toBe('1 volume out in French is not here.');
      const cant = (reason: SeriesProgressDto['answerReason'], t: Partial<SeriesTrackersDto> = {}) =>
        answerSentence(progress({ ...japan, ...t }, { answer: 'CantTell', answerReason: reason }));
      expect(cant('NoNumbers')).toBe('The file names carry no volume or chapter numbers, so MangaPixer cannot compare them.');
      expect(cant('NumberingRestarts')).toBe('Volume or chapter numbers start again in subfolders, so MangaPixer cannot compare them.');
      expect(cant('NothingKnownReleased', { language: 'fr' })).toBe('Nothing is known about what is out in French.');
      expect(cant('NoVolumeTotal', { originStatus: 'Ongoing' }))
        .toBe('Still running in Japan. MangaPixer does not know how many volumes are out in English yet, so it cannot say whether you have them all.');
    });

    it('reads a server without answers from the completion', () => {
      expect(completionMarkLabel(progress({}, { completion: 'CompleteCollection', completionBasis: 'AllChapters' })))
        .toBe('Finished - you have it all (Chapter-based)');
      expect(answerSentence(progress({}))).toBeNull();
    });
  });
});
