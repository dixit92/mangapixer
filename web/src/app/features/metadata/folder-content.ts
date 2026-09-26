import { FolderMetadataContentDto, MetadataFolderContent } from '../../core/api/api-types';
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

/** The detector's suggestion, when it differs from what applies now. */
export function contentSuggestion(dto: FolderMetadataContentDto | null | undefined): MetadataFolderContent | null {
  return dto?.suggested && dto.suggested !== dto.effective ? dto.suggested : null;
}
