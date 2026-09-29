import { CardCoverSource, CoverMode, CoverStateDto, WebCoverDto } from '../../core/api/api-types';

/** What the automatic layer uses, in words (the picker's "Automatic - now: ..." line). Keys are `AutoCoverReason` names. */
const REASONS: Record<string, string> = {
  Spread: 'the front half of page 1 (a jacket spread)',
  SpreadOtherSide: 'the other half of page 1 (it matches the web cover)',
  LocalNotCover: 'the volume cover from the web (page 1 is not the cover)',
  FileMatchesWeb: "this file's cover (it matches the web cover)",
  UncertainKept: "this file's cover (close to the web cover - another edition?)",
  NoWebCover: "this file's cover (no web cover)",
  SeriesVolume1: 'the volume 1 cover from the web',
  ChapterFolderDefault: 'the series cover from the web (a chapter folder)',
  WebtoonDefault: 'the series cover from the web (a webtoon)',
  OneShotDefault: 'the cover from the web (a one-shot)',
  SubfolderFirstVolume: 'the web cover of the volume this part starts in',
};

const SOURCES: Record<CardCoverSource, string> = {
  File: "this file's cover",
  Crop: 'a half of page 1',
  WebVolume: 'a volume cover from the web',
  WebMain: 'the series cover from the web',
  Poster: 'the stored series poster',
  Chosen: 'your choice',
};

/** "now: ..." text of the automatic layer. */
export function automaticLabel(state: CoverStateDto | null | undefined): string {
  if (!state) return '';
  if (state.reason && REASONS[state.reason]) return REASONS[state.reason];
  return SOURCES[state.autoSource ?? 'File'];
}

/** The card source a state shows (for the card patch after a change). */
export function cardSource(state: CoverStateDto): CardCoverSource {
  switch (state.mode) {
    case 'Automatic': return state.autoSource ?? 'File';
    case 'FilePinned': return 'File';
    default: return 'Chosen';
  }
}

/** The current mode in words. */
export function modeLabel(mode: CoverMode): string {
  switch (mode) {
    case 'Automatic': return 'Automatic';
    case 'FilePinned': return "Always this file's cover";
    case 'Archive': return "Another item's cover";
    case 'VolumeCover': return 'A cover from the web';
    case 'Crop': return 'A half of page 1';
  }
}

/** Why there are no web covers to choose from (a `CoverOptionsDto.webUnavailableReason` code). */
export function webUnavailableLabel(code: string | null | undefined): string {
  switch (code) {
    case 'not_linked': return 'Covers from the web need the series to be identified first.';
    case 'dont_match': return "This item is marked “Don't match”: no covers from the web.";
    case 'volume_covers_off': return '“Volume covers from the web” is off (Metadata Manager > Settings).';
    case 'web_covers_hidden': return 'This library does not show saved web covers (library settings).';
    case 'no_companion': return 'No covers from the web are stored for this series yet.';
    default: return 'Covers from the web are not available here.';
  }
}

/** A web cover tile's label: its language, plus the edition variant and whether it is downloaded. */
export function webCoverLabel(cover: WebCoverDto): string {
  const variant = cover.variant ? ` · edition ${cover.variant}` : '';
  const main = cover.kind === 'Main' ? 'Series cover · ' : '';
  return `${main}${cover.locale.toUpperCase()}${variant}${cover.stored ? '' : ' · not downloaded yet'}`;
}
