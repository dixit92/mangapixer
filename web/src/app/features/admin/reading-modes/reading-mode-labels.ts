import { ReaderMode, ReadingModeConfidence, ReadingModeSuggestionDto } from '../../../core/api/api-types';
import { readerModeGlyph } from '../../../shared/reader-mode-glyph';
import { originLabel } from '../../metadata/series-info-labels';

/** The mode's name, as the library icons and the reader menu name it. */
export function modeLabel(mode: ReaderMode | null | undefined): string {
  return mode ? readerModeGlyph(mode)?.label ?? mode : 'Not set';
}

export const CONFIDENCE_LABELS: Record<ReadingModeConfidence, string> = {
  High: 'High confidence',
  Medium: 'Medium confidence',
  Low: 'Low confidence - a hint',
};

/** What the folder reads as today (users' own overrides left out). */
export function currentText(row: ReadingModeSuggestionDto): string {
  switch (row.currentSource) {
    case 'Library': return `${modeLabel(row.currentMode)} (library default)`;
    case 'ThisFolder': return `${modeLabel(row.currentMode)} (set on this folder)`;
    case 'FolderAbove': return `${modeLabel(row.currentMode)} (set on a folder above)`;
    default: return 'Not set - each reader\'s own default';
  }
}

const plural = (n: number, one: string, many = `${one}s`) => `${n} ${n === 1 ? one : many}`;

/**
 * The evidence behind a row, the signal it rests on first. Only signals that say something are listed: tall-strip pages (when at
 * least 20 were measured), ComicInfo `Manga`, the linked record's webtoon flag or origin.
 */
export function evidenceLines(row: ReadingModeSuggestionDto): string[] {
  const pages = row.measuredPages >= 20
    ? `${row.tallPages} of ${row.measuredPages} sampled pages are tall strips`
      + (row.subfolders > 0 ? ` (in ${plural(row.subfolders, 'subfolder')})` : '')
    : null;
  const comicInfo = row.comicInfoStated > 0
    ? `ComicInfo says right to left in ${row.comicInfoRightToLeft} of ${plural(row.comicInfoStated, 'archive')} that `
      + `${row.comicInfoStated === 1 ? 'states' : 'state'} a direction`
    : null;
  const recordParts = [
    row.linkedWebtoon === true ? 'a webtoon' : row.linkedWebtoon === false ? 'not a webtoon' : null,
    row.linkedOrigin ? `from ${originLabel(row.linkedOrigin)}` : null,
  ].filter((p): p is string => !!p);
  const record = recordParts.length > 0 ? `Linked series: ${recordParts.join(', ')}` : null;
  const ordered = row.signal === 'TallPages' ? [pages, comicInfo, record]
    : row.signal === 'ComicInfo' ? [comicInfo, pages, record]
      : [record, pages, comicInfo];
  return ordered.filter((l): l is string => !!l);
}

export function appliedSentence(applied: number, skipped: number): string {
  const done = applied > 0 ? `Applied to ${plural(applied, 'folder')}.` : 'Nothing was applied.';
  return skipped > 0 ? `${done} ${plural(skipped, 'folder')} could not be changed (removed or not a folder).` : done;
}
