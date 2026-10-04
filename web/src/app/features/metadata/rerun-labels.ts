import { MetadataReviewBulkItemResultDto } from '../../core/api/api-types';

/** "Re-run matching" per-item answers (1.34.0), in plain words. Unknown codes are shown as they are. */
const REASONS: Record<string, string> = {
  linked: 'already linked - unlink first',
  covered_by_folder: 'inside a folder that is linked, marked Don\'t match or waiting in review',
  not_found: 'not found any more',
};

export function rerunReason(code: string): string {
  return REASONS[code] ?? code;
}

/**
 * The snackbar after "Re-run matching" on a selection: how many were queued, and for each refusal reason how many items it
 * covers ("2 not queued: 1 already linked - unlink first; 1 inside a folder that is linked, marked Don't match or waiting in review").
 */
export function rerunMessage(results: readonly MetadataReviewBulkItemResultDto[]): string {
  const queued = results.filter((r) => r.code === 'ok').length;
  const refused = new Map<string, number>();
  for (const r of results) if (r.code !== 'ok') refused.set(r.code, (refused.get(r.code) ?? 0) + 1);
  const head = queued > 0 ? `${queued} item${queued === 1 ? '' : 's'} queued to match again` : 'Nothing was queued';
  if (refused.size === 0) return head;
  const total = [...refused.values()].reduce((a, b) => a + b, 0);
  const reasons = [...refused].map(([code, n]) => `${n} ${rerunReason(code)}`).join('; ');
  return `${head}. ${total} not queued: ${reasons}`;
}
