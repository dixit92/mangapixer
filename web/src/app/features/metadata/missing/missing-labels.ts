import {
  MissingConfidence, MissingConversionBatchResultDto, MissingConversionDto, MissingSeriesDto, MissingTotalSource, MissingUnitGapDto,
  MissingVerdict,
} from '../../../core/api/api-types';

// Wording of the missing volumes / chapters report (1.28.0). Pure functions so the tab, the series page line
// and their specs agree.

export const MISSING_SOURCE_LABELS: Record<MissingTotalSource, string> = {
  English: 'English',
  Origin: 'original run',
  LatestChapter: 'latest release',
  Converted: 'estimate',
};

export const MISSING_CONFIDENCE_LABELS: Record<MissingConfidence, string> = {
  High: 'English publisher total',
  Medium: 'total in the country of origin - the English edition may differ',
  Low: 'latest released chapter (scanlations) - not an official total',
};

export const MISSING_VERDICT_LABELS: Record<MissingVerdict, string> = {
  Behind: 'Behind',
  Holes: 'Gaps',
  UpToDate: 'Up to date',
  NoTotal: 'No total known',
  Mixed: 'Mixed folder',
  NoUnits: 'No numbers',
};

/** `[1, 2, 3, 7, 9, 10]` -> `1-3, 7, 9-10`. */
export function compactNumbers(numbers: readonly number[]): string {
  const parts: string[] = [];
  let i = 0;
  while (i < numbers.length) {
    let j = i;
    while (j + 1 < numbers.length && numbers[j + 1] === numbers[j] + 1) j++;
    parts.push(j === i ? `${numbers[i]}` : `${numbers[i]}-${numbers[j]}`);
    i = j + 1;
  }
  return parts.join(', ');
}

function unitWord(gap: MissingUnitGapDto, plural = true): string {
  const word = gap.kind === 'Volume' ? 'volume' : 'chapter';
  return plural ? `${word}s` : word;
}

/**
 * "You have volumes 1-7 of 10 (English)" / "You have chapters 21-40; no total known". With holes the range would
 * overstate it: "You have 2 volumes (up to 16) of 18 (English)".
 */
export function haveSentence(gap: MissingUnitGapDto): string {
  const range = gap.missingCount > 0
    ? `${gap.unitCount} ${unitWord(gap, gap.unitCount !== 1)} (up to ${gap.have})`
    : gap.lowest === gap.have ? `${unitWord(gap, false)} ${gap.have}` : `${unitWord(gap)} ${gap.lowest}-${gap.have}`;
  if (gap.available == null || !gap.source) return `You have ${range}; no total known`;
  const about = gap.source === 'Converted' ? '~' : '';
  return `You have ${range} of ${about}${gap.available} (${MISSING_SOURCE_LABELS[gap.source]})`;
}

/** "3 behind", "missing 3-4", "3 behind · missing 3-4, +12 more", or "" when complete. */
export function gapDetail(gap: MissingUnitGapDto): string {
  const parts: string[] = [];
  if (gap.behindBy > 0) parts.push(`${gap.behindBy} behind`);
  if (gap.missingCount > 0) {
    const more = gap.missingCount - gap.missing.length;
    parts.push(`missing ${compactNumbers(gap.missing)}${more > 0 ? `, +${more} more` : ''}`);
  }
  return parts.join(' · ');
}

/** The gaps of a row that have numbers, volumes first. */
export function gapsOf(row: MissingSeriesDto): MissingUnitGapDto[] {
  return [row.volumes, row.chapters].filter((g): g is MissingUnitGapDto => !!g);
}

/** Why a row has no verdict. */
export function noVerdictReason(row: MissingSeriesDto): string | null {
  if (row.verdict === 'Mixed') return 'Volumes and chapters are mixed in one folder, so there is nothing to compare.';
  if (row.verdict === 'NoUnits') return 'No archive name states a volume or chapter number.';
  return null;
}

/** "AniList: 116 chapters in 27 volumes, 4.3 per volume" / "AniList: still running, no final totals". */
export function conversionLine(c: MissingConversionDto): string {
  if (c.chaptersPerVolume != null && c.volumes && c.chapters) {
    return `${c.providerName}: ${c.chapters} chapters in ${c.volumes} volumes, ${c.chaptersPerVolume} per volume`;
  }
  return `${c.providerName}: no final volume and chapter totals yet`;
}

/** The batch result in one sentence. */
export function batchSentence(r: MissingConversionBatchResultDto): string {
  const parts = [`Looked up ${r.looked}: ${r.found} found, ${r.noCounts} without final totals, ${r.noMatch} no match.`];
  if (r.remaining > 0) parts.push(`${r.remaining} still without chapters per volume.`);
  if (r.stoppedMessage) parts.push(`Stopped: ${r.stoppedMessage}`);
  return parts.join(' ');
}
