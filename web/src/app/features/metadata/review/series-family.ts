import { MetadataReviewCandidateDto } from '../../../core/api/api-types';

/**
 * Series families on a review row (1.30.0, owner): candidates that are one series family - a main story with its spin-offs,
 * side stories, prequels or sequels - are shown together, so an admin does not pair a folder with the main series' metadata
 * when it holds a spin-off, or the other way round.
 */

/** Reason chips a family block's heading already states (left out on its candidates). */
export const FAMILY_REASONS: ReadonlySet<string> = new Set(['subtitle_family', 'series_family']);

/** The heading of a family block. */
export const SERIES_FAMILY_NOTE = 'Same series family - check which one';

const ROLE_LABELS: Record<string, string> = {
  main_story: 'Main story',
  spin_off: 'Spin-off',
  side_story: 'Side story',
  prequel: 'Prequel',
  sequel: 'Sequel',
  alternate: 'Alternate version',
  alternate_story: 'Alternate story',
  adaptation: 'Adaptation',
  source: 'Original work',
  related: 'Related',
};

/** "Main story", "Spin-off", ... for a family role code ('' for none). */
export function familyRoleLabel(role: string | null | undefined): string {
  return role ? ROLE_LABELS[role] ?? role.replace(/_/g, ' ') : '';
}

/** A run of candidates on a review row: one family (two or more) or a single candidate of its own. */
export interface CandidateBlock {
  family: boolean;
  candidates: MetadataReviewCandidateDto[];
}

/**
 * The row's candidates in display order: by rank, except that a family is shown in one block at the place of its best-ranked
 * member (ranks never change - Accept still takes the rank). A family of one (its other members not stored) is shown alone.
 */
export function candidateBlocks(candidates: readonly MetadataReviewCandidateDto[] | null | undefined): CandidateBlock[] {
  const sorted = [...(candidates ?? [])].sort((a, b) => a.rank - b.rank);
  const sizes = new Map<number, number>();
  for (const c of sorted) {
    if (c.familyGroup != null) sizes.set(c.familyGroup, (sizes.get(c.familyGroup) ?? 0) + 1);
  }
  const blocks: CandidateBlock[] = [];
  const shown = new Set<number>();
  for (const c of sorted) {
    const group = c.familyGroup;
    if (group == null || (sizes.get(group) ?? 0) < 2) {
      blocks.push({ family: false, candidates: [c] });
    } else if (!shown.has(group)) {
      shown.add(group);
      blocks.push({ family: true, candidates: sorted.filter((m) => m.familyGroup === group) });
    }
  }
  return blocks;
}
