import {
  MetadataOrigin, MetadataOriginStatus, SeriesAnswer, SeriesProgressDto, SeriesReachDto, UnitSpanDto, CompletionBasis,
} from '../../../core/api/api-types';

// Wording of a linked series' progress (1.30.0, reach): the per-kind trackers (origin, the official release and the released
// chapters in the preferred language) and what the folder holds, with the missing count, the upgrades and the completion mark.
// Pure functions shared by the Volumes view status line, the series page line, the Missing and the Completion tabs. 1.32.0: the one
// answer per series of the Completion tab (owner-approved wording) and the completion mark in the same words.

/** A language code as an English name ("fr" -> "French"); the code itself when the runtime cannot name it. */
export function languageName(code: string | null | undefined): string {
  if (!code) return '';
  try {
    return new Intl.DisplayNames(['en'], { type: 'language' }).of(code) ?? code;
  } catch {
    return code;
  }
}

/** The origin status word ("Complete (Japan)"); the status word stays the country of origin's (1.29.0). */
export const STATUS_WORDS: Partial<Record<MetadataOriginStatus, string>> = {
  Ongoing: 'Ongoing',
  Complete: 'Complete',
  Hiatus: 'On hiatus',
  Cancelled: 'Cancelled',
};

/** A publisher's own status, lower case after its totals ("14 volumes, ongoing"); cancelled = the publisher dropped it. */
export const OFFICIAL_STATUS_WORDS: Partial<Record<MetadataOriginStatus, string>> = {
  Ongoing: 'ongoing',
  Complete: 'complete',
  Hiatus: 'on hiatus',
  Cancelled: 'dropped',
};

/** Where the publication status applies ("Complete (Japan)"); null for an unknown or "other" origin. */
export const ORIGIN_PLACES: Partial<Record<MetadataOrigin, string>> = {
  Japan: 'Japan',
  Korea: 'Korea',
  ChinaTaiwan: 'China / Taiwan',
  EnglishOriginal: 'English original',
  Philippines: 'Philippines',
  Indonesia: 'Indonesia',
  Thailand: 'Thailand',
  Vietnam: 'Vietnam',
  Malaysia: 'Malaysia',
  Nordic: 'Nordic',
  French: 'France',
  Spanish: 'Spain',
  German: 'Germany',
  Italian: 'Italy',
  Dutch: 'Netherlands / Flanders',
};

function plural(n: number, word: string): string {
  return `${n} ${word}${n === 1 ? '' : 's'}`;
}

/** `[{1,3},{7,7}]` -> `1-3, 7`. */
export function spanText(spans: readonly UnitSpanDto[]): string {
  return spans.map((s) => (s.from === s.to ? `${s.from}` : `${s.from}-${s.to}`)).join(', ');
}

/** Compacts numbers into spans: `[15, 16, 17, 19]` -> `15-17, 19`. */
export function numbersText(numbers: readonly number[]): string {
  const sorted = [...new Set(numbers)].sort((a, b) => a - b);
  const spans: UnitSpanDto[] = [];
  for (const n of sorted) {
    const last = spans[spans.length - 1];
    if (last && n === last.to + 1) last.to = n;
    else spans.push({ from: n, to: n });
  }
  return spanText(spans);
}

function unitsText(word: string, spans: readonly UnitSpanDto[]): string | null {
  if (spans.length === 0) return null;
  if (spans.length === 1 && spans[0].from === spans[0].to) return `${word} ${spans[0].from}`;
  // More than two runs read as one range "with gaps" (the missing count says how many).
  if (spans.length > 2) return `${word}s ${spans[0].from}-${spans[spans.length - 1].to} with gaps`;
  return `${word}s ${spanText(spans)}`;
}

/**
 * Line 1, the trackers: "Ongoing (Japan): 22 volumes · English (Yen Press): 14 volumes, ongoing · English chapters: to chapter 65".
 * Each segment only when known; the language is named from its code, never assumed.
 */
export function trackersLine(progress: SeriesProgressDto | null | undefined): string | null {
  if (!progress) return null;
  const t = progress.trackers;
  const lang = languageName(t.language);
  const parts: string[] = [];

  const word = t.originStatus ? STATUS_WORDS[t.originStatus] ?? null : null;
  const place = t.origin ? ORIGIN_PLACES[t.origin] ?? null : null;
  const originCount = t.originVolumes ? plural(t.originVolumes, 'volume') : t.originChapters ? plural(t.originChapters, 'chapter') : null;
  const originHead = word && place ? `${word} (${place})` : word ?? place ?? (originCount ? 'Original run' : null);
  if (originHead) parts.push(originCount ? `${originHead}: ${originCount}` : originHead);

  if (t.officialPublisher || t.officialVolumes || t.officialChapters) {
    const head = t.officialPublisher ? `${lang} (${t.officialPublisher})` : lang;
    const count = t.officialVolumes ? plural(t.officialVolumes, 'volume') : t.officialChapters ? plural(t.officialChapters, 'chapter') : null;
    const status = t.officialStatus ? OFFICIAL_STATUS_WORDS[t.officialStatus] ?? null : null;
    const tail = [count, status].filter((x): x is string => !!x).join(', ');
    parts.push(tail ? `${head}: ${tail}` : head);
  } else if (t.licensed === false) {
    parts.push(`${lang}: not licensed`);
  }

  const latest = Math.max(t.latestChapter ?? 0, t.releasedChapter ?? 0) || null;
  if (t.latestChapter != null || t.scanlationComplete != null) {
    // MangaUpdates' chapter facts (English only; the server sends them only for English). "Chapters", not "scanlation" (1.30.1,
    // owner): MangaUpdates' "Completely Scanlated?" also covers official chapter-by-chapter releases (MANGA Plus).
    const done = t.scanlationComplete === true ? 'complete' : t.scanlationComplete === false && !latest ? 'ongoing' : null;
    const tail = [latest ? `to chapter ${latest}` : null, done].filter((x): x is string => !!x).join(', ');
    parts.push(`${lang} chapters: ${tail}`);
  } else if (t.releasedChapter) {
    parts.push(`${lang}: to chapter ${t.releasedChapter}`);
  }
  return parts.length > 0 ? parts.join(' · ') : null;
}

/** "volumes 1-14 + chapters 47-65", "chapters 1-148", "volume 3"; null when nothing states a number. */
export function reachText(reach: SeriesReachDto | null | undefined): string | null {
  if (!reach) return null;
  const parts = [unitsText('volume', reach.volumeFiles), unitsText('chapter', reach.chapters)].filter((x): x is string => !!x);
  return parts.length > 0 ? parts.join(' + ') : null;
}

/** "Volume 15 available in English" / "Volumes 15-19 available in English"; null without upgrades. */
export function upgradeText(progress: SeriesProgressDto): string | null {
  if (progress.upgradeCount <= 0) return null;
  const more = progress.upgradeCount - progress.upgradeVolumes.length;
  const numbers = numbersText(progress.upgradeVolumes) + (more > 0 ? `, +${more} more` : '');
  const word = progress.upgradeCount === 1 ? 'Volume' : 'Volumes';
  return `${word} ${numbers} available in ${languageName(progress.trackers.language)}`;
}

/** The missing count ("2 volumes, 3 chapters missing"), or null when nothing is missing. */
export function missingText(progress: SeriesProgressDto): string | null {
  const parts: string[] = [];
  if (progress.missingVolumes > 0) parts.push(plural(progress.missingVolumes, 'volume'));
  if (progress.missingChapters > 0) parts.push(plural(progress.missingChapters, 'chapter'));
  return parts.length > 0 ? `${parts.join(', ')} missing` : null;
}

/**
 * Line 2, the folder: "You have volumes 1-14 + chapters 47-65 · up to date · Volume 15 available in English",
 * "You have volumes 1-12 · 2 volumes missing · finished - missing some (Official, 12 of 14)",
 * "You have volumes 1-14 · finished - you have it all (Official)" (1.32.0 wording).
 */
export function folderLine(progress: SeriesProgressDto | null | undefined): string | null {
  if (!progress) return null;
  const reach = reachText(progress.reach);
  if (!reach) return null;
  const parts = [`You have ${reach}`];
  const missing = missingText(progress);
  const answer = answerOf(progress);
  const mark = completionMarkLabel(progress);
  const markText = mark ? mark.replace(/^./, (c) => c.toLowerCase()) : null;
  if (missing) parts.push(missing);
  else if (answer === 'HaveItAll' && markText) parts.push(markText);
  else if (progress.releaseKnown && answer !== 'FinishedMissing') parts.push('up to date');
  const upgrade = upgradeText(progress);
  if (upgrade) parts.push(upgrade);
  if (markText && (answer === 'FinishedMissing' || (missing && answer === 'HaveItAll'))) parts.push(markText);
  return parts.join(' · ');
}

/** "You have volumes 1-14 + chapters 47-65 · 2 chapters missing" (the tabs: no upgrade / completion, they have their own line). */
export function reachSentence(progress: SeriesProgressDto | null | undefined): string | null {
  const reach = reachText(progress?.reach);
  if (!progress || !reach) return null;
  return [`You have ${reach}`, missingText(progress)].filter((x): x is string => !!x).join(' · ');
}

/** The icon of the status line: the completion mark first, then missing, then an upgrade, then fine. */
export function progressIcon(progress: SeriesProgressDto): string {
  if (answerOf(progress) === 'HaveItAll') return 'workspace_premium';
  if (progress.missingVolumes + progress.missingChapters > 0) return 'error_outline';
  if (progress.upgradeCount > 0) return 'new_releases';
  return 'check_circle_outline';
}

/**
 * The edition in words (owner, 1.30.0 RC; 1.30.1 "Chapter-based"; 1.32.0 "Official chapters"): "Chapter-based", not "Fan translation" -
 * a finished chapter release can be official (MANGA Plus); who translated it is known only when an official chapter-by-chapter
 * publisher covers the run.
 */
export function completionBasisLabel(basis: CompletionBasis | null | undefined): string {
  switch (basis) {
    case 'AllChapters': return 'Chapter-based';
    case 'OfficialChapters': return 'Official chapters';
    case 'OriginRun': return 'Original run';
    default: return 'Official';
  }
}

/** The answer chips of the Completion tab (1.32.0, owner-approved wording). */
export const ANSWER_LABELS: Record<SeriesAnswer, string> = {
  HaveItAll: 'Finished - you have it all',
  FinishedMissing: 'Finished - missing some',
  UpToDate: 'Everything released so far',
  MissingSome: 'Missing some',
  CantTell: 'Can\'t tell',
};

/** The short filter labels, in the order shown. */
export const ANSWER_FILTERS: readonly { value: SeriesAnswer; label: string }[] = [
  { value: 'HaveItAll', label: 'Have it all' },
  { value: 'FinishedMissing', label: 'Finished, missing some' },
  { value: 'UpToDate', label: 'Everything so far' },
  { value: 'MissingSome', label: 'Missing some' },
  { value: 'CantTell', label: 'Can\'t tell' },
];

export const ANSWER_ICONS: Record<SeriesAnswer, string> = {
  HaveItAll: 'workspace_premium',
  FinishedMissing: 'flag',
  UpToDate: 'check_circle_outline',
  MissingSome: 'error_outline',
  CantTell: 'help_outline',
};

/** The series' answer; a server without it (before 1.32.0) gives the two Finished answers from the completion. */
export function answerOf(progress: SeriesProgressDto | null | undefined): SeriesAnswer | null {
  if (!progress) return null;
  if (progress.answer) return progress.answer;
  return progress.completion === 'CompleteCollection' ? 'HaveItAll' : progress.completion === 'FinishedNotHeld' ? 'FinishedMissing' : null;
}

/** A one-shot: the one-shot rule, or an ended original run of a single volume held whole. */
function isOneShot(progress: SeriesProgressDto): boolean {
  return progress.answerReason === 'OneShot'
    || (answerOf(progress) === 'HaveItAll' && progress.completionBasis === 'OriginRun' && progress.completionTarget === 1 && !progress.completionInChapters);
}

/** The edition label of the two Finished answers ("Official", "Official chapters", "Chapter-based", "Original run", "One-shot"), else null. */
export function editionLabel(progress: SeriesProgressDto): string | null {
  const answer = answerOf(progress);
  if (answer !== 'HaveItAll' && answer !== 'FinishedMissing') return null;
  if (isOneShot(progress)) return 'One-shot';
  return progress.completionBasis ? completionBasisLabel(progress.completionBasis) : null;
}

/**
 * The completion mark (series page, Missing tab, Volumes view): "Finished - you have it all (Chapter-based)", "Finished - missing some
 * (Official, 12 of 14)"; null for the other answers.
 */
export function completionMarkLabel(progress: SeriesProgressDto): string | null {
  const answer = answerOf(progress);
  if (answer !== 'HaveItAll' && answer !== 'FinishedMissing') return null;
  const edition = editionLabel(progress);
  const count = answer === 'FinishedMissing' && progress.completionTarget ? `${progress.completionHeld ?? 0} of ${progress.completionTarget}` : null;
  const tail = [edition, count].filter((x): x is string => !!x).join(', ');
  return tail ? `${ANSWER_LABELS[answer]} (${tail})` : ANSWER_LABELS[answer];
}

function unitsWord(progress: SeriesProgressDto, n: number): string {
  return plural(n, progress.completionInChapters ? 'chapter' : 'volume');
}

/** "all 14 volumes", "its one volume". */
function allOf(n: number, word: string): string {
  return n === 1 ? `its one ${word}` : `all ${plural(n, word)}`;
}

/** "2 volumes, 3 chapters" (what is missing, without the word), or null. */
function missingCounts(progress: SeriesProgressDto): { text: string; total: number } | null {
  const parts: string[] = [];
  if (progress.missingVolumes > 0) parts.push(plural(progress.missingVolumes, 'volume'));
  if (progress.missingChapters > 0) parts.push(plural(progress.missingChapters, 'chapter'));
  return parts.length > 0 ? { text: parts.join(', '), total: progress.missingVolumes + progress.missingChapters } : null;
}

/**
 * The answer in one sentence - the "why" line of the Completion tab and the mark's tooltip (1.32.0, owner-approved wording):
 * "Ended in Japan, and the English edition is complete: you have all 14 volumes.", "Still running in Japan: you have everything out
 * in English so far.", "The file names carry no volume or chapter numbers, so MangaPixer cannot compare them."; null without an answer.
 */
export function answerSentence(progress: SeriesProgressDto): string | null {
  const answer = answerOf(progress);
  if (!answer) return null;
  const t = progress.trackers;
  const lang = languageName(t.language) || t.language;
  const place = t.origin ? ORIGIN_PLACES[t.origin] ?? null : null;
  const inPlace = place ? ` in ${place}` : '';
  const ended = t.originStatus === 'Cancelled' ? `Cancelled${inPlace}` : `Ended${inPlace}`;
  const running = t.originStatus === 'Hiatus' ? `On hiatus${inPlace}` : t.originStatus === 'Ongoing' ? `Still running${inPlace}` : null;
  const target = progress.completionTarget ?? 0;
  const held = progress.completionHeld ?? 0;
  const missing = missingCounts(progress);
  const notHere = missing ? `${missing.text} out in ${lang} ${missing.total === 1 ? 'is' : 'are'} not here.` : null;

  switch (answer) {
    case 'HaveItAll':
      if (isOneShot(progress)) return `A one-shot, ${ended.replace(/^./, (c) => c.toLowerCase())}: you have it.`;
      switch (progress.completionBasis) {
        case 'OfficialVolumes': return `${ended}, and the ${lang} edition is complete: you have ${allOf(target, 'volume')}.`;
        case 'OfficialChapters': return `${ended}, and every chapter is out officially in ${lang}: you have ${allOf(target, 'chapter')}.`;
        case 'AllChapters': return `${ended}, and every chapter is out in ${lang}: you have ${allOf(target, 'chapter')}.`;
        default: return `${ended}: you have ${allOf(target, progress.completionInChapters ? 'chapter' : 'volume')} of the original run.`;
      }
    case 'FinishedMissing':
      switch (progress.completionBasis) {
        case 'OfficialVolumes': return `${ended}, and the ${lang} edition is complete: you have ${held} of ${plural(target, 'volume')}.`;
        case 'OfficialChapters': return `${ended}, and every chapter is out officially in ${lang}: you have ${held} of ${plural(target, 'chapter')}.`;
        case 'AllChapters': return `${ended}, and every chapter is out in ${lang}: you have ${held} of ${plural(target, 'chapter')}.`;
        case 'OriginRun': return `${ended}: you have ${held} of the ${unitsWord(progress, target)} of the original run.`;
        default: return notHere ? `${ended}: ${notHere}` : `${ended}: some of it is not here.`;
      }
    case 'UpToDate':
      switch (progress.answerReason) {
        case 'WaitingForLanguage':
          if (t.officialVolumes && t.originVolumes && t.officialVolumes < t.originVolumes) {
            return `${ended}, but ${lang} volumes are still coming (${t.officialVolumes} of ${t.originVolumes}): you have everything out so far.`;
          }
          return `${ended}, but not all of it is out in ${lang} yet: you have everything out so far.`;
        case 'LanguageEditionDropped':
          return `${ended}; the ${lang} edition stopped after ${plural(t.officialVolumes ?? 0, 'volume')}, and you have all of them.`;
        case 'StatusUnknown':
          return `You have everything out in ${lang} so far; MangaUpdates does not say whether the series has ended.`;
        default:
          return `${running ?? `Still running${inPlace}`}: you have everything out in ${lang} so far.`;
      }
    case 'MissingSome':
      if (!notHere) return null;
      return running ? `${running}: ${notHere}` : notHere.replace(/^./, (c) => c.toUpperCase());
    case 'CantTell':
      switch (progress.answerReason) {
        case 'NumberingRestarts': return 'Volume or chapter numbers start again in subfolders, so MangaPixer cannot compare them.';
        case 'NothingKnownReleased': return `Nothing is known about what is out in ${lang}.`;
        case 'NoVolumeTotal':
          return `${running ? `${running}. ` : ''}MangaPixer does not know how many volumes are out in ${lang} yet, so it cannot say whether you have them all.`;
        default: return 'The file names carry no volume or chapter numbers, so MangaPixer cannot compare them.';
      }
  }
}

/** "Volumes 15-19 available in English - you hold them as chapters". */
export function upgradeSentence(progress: SeriesProgressDto): string | null {
  const text = upgradeText(progress);
  if (!text) return null;
  return `${text} - you hold ${progress.upgradeCount === 1 ? 'it' : 'them'} as chapters`;
}

/** The stack / card marker of an official volume held as chapters: "Available in English". */
export function officialReleaseLabel(language: string | null | undefined): string {
  return language ? `Available in ${languageName(language)}` : '';
}

/** The chapter card marker: "Also in Volume 10". */
export function alsoInVolumeLabel(volume: string | null | undefined): string {
  return volume ? `Also in Volume ${volume}` : '';
}
