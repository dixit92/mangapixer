import { BrowseCompletionCountsDto, SeriesAnswer } from '../../core/api/api-types';
import { ANSWER_FILTERS, ANSWER_ICONS } from '../metadata/progress/series-progress-labels';

// The library Filter menu's Completion section (1.44.0, owner-approved): keep only the series folders whose Completion answer is the
// chosen one - the Completion tab's answers, short labels and icons. It shows only where the browsed level holds linked series
// (`linkedSeries` > 0); elsewhere the server ignores the choice and the menu hides it. Pure helpers, shared with the specs.

export interface CompletionOption {
  value: SeriesAnswer;
  label: string;
  icon: string;
}

/** The five answers in the Completion tab's order, with its short labels and icons. */
export const COMPLETION_OPTIONS: readonly CompletionOption[] = ANSWER_FILTERS.map((f) => ({ ...f, icon: ANSWER_ICONS[f.value] }));

const COUNT_KEYS: Record<SeriesAnswer, keyof BrowseCompletionCountsDto> = {
  HaveItAll: 'haveItAll',
  FinishedMissing: 'finishedMissing',
  UpToDate: 'upToDate',
  MissingSome: 'missingSome',
  CantTell: 'cantTell',
};

/** "Missing some (42)" once the server counted the level's answers (while the filter applies); the bare label before. */
export function completionOptionLabel(option: CompletionOption, counts: BrowseCompletionCountsDto | null | undefined): string {
  const n = counts?.[COUNT_KEYS[option.value]];
  return n === undefined ? option.label : `${option.label} (${n})`;
}

/** The short label of an answer ("Have it all"). */
export function completionLabel(answer: SeriesAnswer): string {
  return COMPLETION_OPTIONS.find((o) => o.value === answer)?.label ?? answer;
}
