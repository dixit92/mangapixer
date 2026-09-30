import { MetadataReviewCandidateDto } from '../../../core/api/api-types';
import { candidateBlocks, familyRoleLabel } from './series-family';

/** Series families on a review row (1.30.0): display blocks and role labels. */
describe('series family blocks', () => {
  const c = (rank: number, familyGroup: number | null = null, familyRole: string | null = null): MetadataReviewCandidateDto => ({
    rank, provider: 'mangaupdates', externalId: String(100 + rank), title: `T${rank}`, titleScore: 0.9, adjustedScore: 0.9,
    familyGroup, familyRole,
  });

  it('keeps rank order when there is no family', () => {
    expect(candidateBlocks([c(2), c(1)]).map((b) => [b.family, b.candidates.map((x) => x.rank)])).toEqual([
      [false, [1]], [false, [2]]]);
  });

  it('moves a family into one block at the place of its best-ranked member', () => {
    const blocks = candidateBlocks([c(1, 1, 'spin_off'), c(2), c(3, 1, 'main_story'), c(4, 4, 'sequel'), c(5, 4, 'prequel')]);
    expect(blocks.map((b) => [b.family, b.candidates.map((x) => x.rank)])).toEqual([
      [true, [1, 3]], [false, [2]], [true, [4, 5]]]);
  });

  it('shows a family of one (its other members not stored) as a candidate of its own', () => {
    expect(candidateBlocks([c(1, 1, 'spin_off'), c(2)]).every((b) => !b.family)).toBe(true);
    expect(candidateBlocks(null)).toEqual([]);
  });

  it('labels the roles', () => {
    expect(['main_story', 'spin_off', 'side_story', 'prequel', 'sequel', 'source', 'related', 'something_new', null].map(familyRoleLabel))
      .toEqual(['Main story', 'Spin-off', 'Side story', 'Prequel', 'Sequel', 'Original work', 'Related', 'something new', '']);
  });
});
