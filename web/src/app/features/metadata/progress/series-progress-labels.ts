import { MetadataOrigin, MetadataOriginStatus, SeriesProgressDto, SeriesReachDto, UnitSpanDto, CompletionBasis } from '../../../core/api/api-types';

// Wording of a linked series' progress (1.30.0, reach): the per-kind trackers (origin, the official release and the released
// chapters in the preferred language) and what the folder holds, with the missing count, the upgrades and the completion mark.
// Pure functions shared by the Volumes view status line, the series page line, the Missing and the Official releases tabs.

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

/** True for a series finished in the preferred language (the official edition or every chapter), not the origin only. */
function finishedInLanguage(progress: SeriesProgressDto): boolean {
  return progress.completionBasis === 'OfficialVolumes' || progress.completionBasis === 'AllChapters';
}

/**
 * The completion basis in words (owner, 1.30.0 RC): the official edition, the chapters or the original run. "Chapter-based", not
 * "Fan translation" (1.30.1, owner): a finished chapter release can be official (MANGA Plus); who translated it is not known.
 */
export function completionBasisLabel(basis: CompletionBasis | null | undefined): string {
  return basis === 'AllChapters' ? 'Chapter-based' : basis === 'OriginRun' ? 'Original run' : 'Official';
}

/** The completion mark: "Complete collection - Official" / "- Chapter-based" / "- Original run" (owner, 1.30.0 RC; 1.30.1). */
export function completeCollectionLabel(progress: SeriesProgressDto): string {
  return `Complete collection - ${completionBasisLabel(progress.completionBasis)}`;
}

/** Which release finished (owner, 1.30.0 RC): "Official, English" or "Chapter-based, English". */
function finishedKind(progress: SeriesProgressDto): string {
  const lang = languageName(progress.trackers.language);
  return progress.completionBasis === 'AllChapters' ? `Chapter-based, ${lang}` : `Official, ${lang}`;
}

/**
 * Line 2, the folder: "You have volumes 1-14 + chapters 47-65 · up to date · Volume 15 available in English",
 * "You have volumes 1-12 · 2 volumes missing · finished - official, English", "You have volumes 1-14 · Complete collection".
 */
export function folderLine(progress: SeriesProgressDto | null | undefined): string | null {
  if (!progress) return null;
  const reach = reachText(progress.reach);
  if (!reach) return null;
  const parts = [`You have ${reach}`];
  const missing = missingText(progress);
  if (missing) parts.push(missing);
  else if (progress.completion === 'CompleteCollection') parts.push(completeCollectionLabel(progress));
  else if (progress.releaseKnown) parts.push('up to date');
  const upgrade = upgradeText(progress);
  if (upgrade) parts.push(upgrade);
  if (missing && progress.completion === 'CompleteCollection') parts.push(completeCollectionLabel(progress));
  if (progress.completion === 'FinishedNotHeld' && finishedInLanguage(progress)) {
    parts.push(`finished - ${finishedKind(progress).replace(/^./, (c) => c.toLowerCase())}`);
  }
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
  if (progress.completion === 'CompleteCollection') return 'workspace_premium';
  if (progress.missingVolumes + progress.missingChapters > 0) return 'error_outline';
  if (progress.upgradeCount > 0) return 'new_releases';
  return 'check_circle_outline';
}

function targetText(progress: SeriesProgressDto, n: number | null | undefined): string {
  return plural(n ?? 0, progress.completionInChapters ? 'chapter' : 'volume');
}

/**
 * The completion in one sentence (the tab and the tooltip): "Complete collection: every one of the 14 English volumes",
 * "Finished - Official, English (14 volumes) - you have 12", "Finished - Chapter-based, English (172 chapters) - you have 6", or null.
 */
export function completionSentence(progress: SeriesProgressDto): string | null {
  const lang = languageName(progress.trackers.language);
  const target = progress.completionTarget ?? 0;
  if (progress.completion === 'CompleteCollection') {
    switch (progress.completionBasis) {
      case 'OfficialVolumes':
        return `Complete collection: all ${targetText(progress, target)} of the ${lang} edition`;
      case 'AllChapters':
        return `Complete collection: all ${targetText(progress, target)} of the finished ${lang} chapter release`;
      default:
        return `Complete collection: the whole original run (${targetText(progress, target)})`;
    }
  }
  if (progress.completion === 'FinishedNotHeld' && finishedInLanguage(progress)) {
    return `Finished - ${finishedKind(progress)} (${targetText(progress, target)}) - you have ${progress.completionHeld ?? 0}`;
  }
  return null;
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
