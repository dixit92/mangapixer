import { FolderMetadataContentDto, MetadataContentRematchDto, MetadataFolderContent } from '../../core/api/api-types';
import { FOLDER_CONTENT_HINTS, FOLDER_CONTENT_LABELS } from './admin-metadata/metadata-admin-labels';

/**
 * Folder "Content" setting (metadata stage 2, decision 4b): Auto / Doujinshi & adult
 * one-shots / Not doujinshi, inherited like the folder reading direction (nearest
 * ancestor wins). It only changes what AUTOMATIC searches leave out below the folder;
 * the work detector may suggest a value from name signals but never sets it.
 */
export const FOLDER_CONTENT_OPTIONS: readonly { value: MetadataFolderContent; label: string; hint: string; icon: string }[] = [
  { value: 'Auto', label: FOLDER_CONTENT_LABELS.Auto, hint: FOLDER_CONTENT_HINTS.Auto, icon: 'auto_mode' },
  { value: 'DoujinshiAndAdultOneShots', label: FOLDER_CONTENT_LABELS.DoujinshiAndAdultOneShots,
    hint: FOLDER_CONTENT_HINTS.DoujinshiAndAdultOneShots, icon: 'collections' },
  { value: 'NotDoujinshi', label: FOLDER_CONTENT_LABELS.NotDoujinshi, hint: FOLDER_CONTENT_HINTS.NotDoujinshi, icon: 'menu_book' },
];

/** "Content: Auto (inherited)" / "Content: Not doujinshi (set here)" for the menu caption. */
export function contentCaption(dto: FolderMetadataContentDto | null | undefined): string {
  if (!dto) return 'Content';
  const effective = FOLDER_CONTENT_LABELS[dto.effective] ?? dto.effective;
  if (dto.content) return `Content: ${effective} (set here)`;
  return dto.sourceNodeId ? `Content: ${effective} (inherited)` : `Content: ${effective} (default)`;
}

/** Adds up the re-queue results of one or more Content changes (null when none changed the doujinshi rule). */
export function sumRematch(results: readonly (MetadataContentRematchDto | null | undefined)[]): MetadataContentRematchDto | null {
  const changed = results.filter((r): r is MetadataContentRematchDto => !!r);
  if (changed.length === 0) return null;
  return {
    affected: changed.reduce((n, r) => n + r.affected, 0),
    queued: changed.reduce((n, r) => n + r.queued, 0),
    needsConfirmation: changed.some((r) => r.needsConfirmation),
    automaticOff: changed.some((r) => r.automaticOff),
  };
}

/**
 * What a Content change did to matching below the folder, as a message tail: the Needs-review and
 * Unmatched items queued again, or why they were not ('' when there was nothing to redo).
 */
export function rematchMessage(r: MetadataContentRematchDto | null | undefined): string {
  if (!r || r.affected === 0) return '';
  const items = `${r.affected} item${r.affected === 1 ? '' : 's'} below`;
  if (r.automaticOff) return ` · automatic matching is off - ${items} can be re-run from Review`;
  if (r.needsConfirmation) return ` · ${items} were matched without this setting`;
  return ` · ${r.queued} item${r.queued === 1 ? '' : 's'} below queued to match again`;
}

/** The detector's suggestion, when it differs from what applies now. */
export function contentSuggestion(dto: FolderMetadataContentDto | null | undefined): MetadataFolderContent | null {
  return dto?.suggested && dto.suggested !== dto.effective ? dto.suggested : null;
}
