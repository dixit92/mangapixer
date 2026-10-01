import { DuplicateUnitDto } from '../core/api/api-types';

/**
 * Duplicate chapter / volume numbers (1.31.0): the same number in more than one file of the same folder - a chapter uploaded
 * twice, for example. The server finds them (`DuplicateUnits`); these helpers only word them, so the stack card, the stack page,
 * the Missing report and the review row all say the same thing.
 */

/** "Chapter 1: 2 files" / "Volume 3: 2 files". */
export function duplicateLine(d: DuplicateUnitDto): string {
  return `${d.kind === 'Volume' ? 'Volume' : 'Chapter'} ${d.number}: ${d.files} files`;
}

/** "1 duplicate chapter", "2 duplicate chapters", "1 duplicate volume" or both joined; '' when there are none. */
export function duplicateCountLabel(chapters: number, volumes: number): string {
  const parts: string[] = [];
  if (chapters > 0) parts.push(`${chapters} duplicate chapter${chapters === 1 ? '' : 's'}`);
  if (volumes > 0) parts.push(`${volumes} duplicate volume${volumes === 1 ? '' : 's'}`);
  return parts.join(', ');
}

/** The duplicate counts per kind of a list. */
export function duplicateTotals(duplicates: readonly DuplicateUnitDto[] | null | undefined): { chapters: number; volumes: number } {
  const list = duplicates ?? [];
  return { chapters: list.filter((d) => d.kind === 'Chapter').length, volumes: list.filter((d) => d.kind === 'Volume').length };
}

/** The label of a list of duplicates: "2 duplicate chapters"; '' when empty. */
export function duplicatesLabel(duplicates: readonly DuplicateUnitDto[] | null | undefined): string {
  const { chapters, volumes } = duplicateTotals(duplicates);
  return duplicateCountLabel(chapters, volumes);
}

/** "Chapter 1: 2 files, Chapter 2: 2 files" - and " and 3 more" when the server capped the list below `total`. */
export function duplicateListText(duplicates: readonly DuplicateUnitDto[] | null | undefined, total?: number | null): string {
  const list = duplicates ?? [];
  const text = list.map(duplicateLine).join(', ');
  const more = (total ?? list.length) - list.length;
  return more > 0 ? `${text} and ${more} more` : text;
}

/** The explanation shown as a tooltip. */
export const DUPLICATE_TIP =
  'More than one file in the same folder has this number - the same chapter uploaded twice, for example. Every file is still listed; '
  + 'the counts treat each number once.';
