/** A preferred cover language offered in Settings (MangaDex locale codes; the server accepts any `xx` / `xx-yy`). */
export interface CoverLanguageOption {
  code: string;
  label: string;
}

/** Common MangaDex cover locales, English first (the default). */
export const COVER_LANGUAGES: readonly CoverLanguageOption[] = [
  { code: 'en', label: 'English' },
  { code: 'ja', label: 'Japanese' },
  { code: 'ko', label: 'Korean' },
  { code: 'zh', label: 'Chinese (simplified)' },
  { code: 'zh-hk', label: 'Chinese (traditional)' },
  { code: 'fr', label: 'French' },
  { code: 'de', label: 'German' },
  { code: 'es', label: 'Spanish' },
  { code: 'es-la', label: 'Spanish (Latin America)' },
  { code: 'it', label: 'Italian' },
  { code: 'pt-br', label: 'Portuguese (Brazil)' },
  { code: 'ru', label: 'Russian' },
  { code: 'pl', label: 'Polish' },
  { code: 'id', label: 'Indonesian' },
  { code: 'vi', label: 'Vietnamese' },
  { code: 'th', label: 'Thai' },
];

/** The options with the current value added when it is not a common one (so a select never hides it). */
export function coverLanguageOptions(current: string | null | undefined): readonly CoverLanguageOption[] {
  return !current || COVER_LANGUAGES.some((o) => o.code === current)
    ? COVER_LANGUAGES
    : [...COVER_LANGUAGES, { code: current, label: current }];
}

/** Why the background volume-cover pass is waiting, in words (codes from `CoverPassStatusDto.waiting`). */
export function volumeCoversWaitingLabel(code: string | null | undefined): string | null {
  switch (code) {
    case null:
    case undefined:
      return null;
    case 'volume_covers_off':
      return 'Volume covers are off.';
    case 'volume_covers_disabled':
      return 'Switched off in the server configuration.';
    case 'automatic_off':
      return 'Automatic matching is off: covers are only fetched when you use Refresh or Change MangaDex match.';
    case 'metadata_disabled':
      return 'Fetching from the web is off.';
    case 'metadata_network_disabled':
      return 'Web lookups are disabled in the server configuration.';
    case 'provider_not_allowed':
      return 'MangaDex is not on the allowed sites.';
    case 'provider_backoff':
      return 'MangaDex asked us to slow down; it continues later.';
    case 'budget_exhausted':
      return "Today's request budget is used up; it continues tomorrow.";
    default:
      return 'Waiting (' + code + ').';
  }
}
