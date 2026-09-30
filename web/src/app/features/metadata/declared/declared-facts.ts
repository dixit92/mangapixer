import {
  DeclaredCreatorDto,
  DeclaredFactSource,
  DeclaredType,
  EffectiveDeclaredFactsDto,
  NodeDeclaredFactsDto,
} from '../../../core/api/api-types';

/**
 * Declared facts (1.28.0): what an admin states about the works below a folder or a whole
 * library - their type / format and their creators. Inherited downwards, the nearest
 * declaration winning per key (a closer creator list replaces a farther one). Explicit
 * settings only: nothing is ever inferred from library or folder names.
 */
export const DECLARED_TYPE_OPTIONS: readonly { value: DeclaredType; label: string }[] = [
  // Each type names its country of origin (1.30.0, owner): the matcher prefers records from that country, and the
  // three words are easy to mix up. Same labels as the server (`DeclaredFactKeys.TypeLabel`).
  { value: 'Manga', label: 'Manga (Japan)' },
  { value: 'Manhwa', label: 'Manhwa (Korea)' },
  { value: 'Manhua', label: 'Manhua (China)' },
  { value: 'Webtoon', label: 'Webtoon (any country)' },
  { value: 'Comic', label: 'Comic (Western)' },
  { value: 'GraphicNovel', label: 'Graphic novel (Western)' },
  { value: 'Novel', label: 'Novel (any country)' },
];

/** Creator roles the server accepts; '' = no role. Labels match the series credits (Story / Art). */
export const DECLARED_ROLE_OPTIONS: readonly { value: string; label: string }[] = [
  { value: '', label: 'Any role' },
  { value: 'author', label: 'Story & art' },
  { value: 'writer', label: 'Story' },
  { value: 'artist', label: 'Art' },
];

/** Same limits as the server (`DeclaredFactKeys`). */
export const DECLARED_MAX_CREATORS = 20;
export const DECLARED_MAX_NAME = 200;

export function declaredTypeLabel(type: DeclaredType | null | undefined): string {
  if (!type) return '';
  return DECLARED_TYPE_OPTIONS.find((o) => o.value === type)?.label ?? type;
}

export function declaredRoleLabel(role: string | null | undefined): string {
  if (!role) return '';
  return DECLARED_ROLE_OPTIONS.find((o) => o.value === role)?.label ?? role;
}

/** "A (Story), B" - names with their role in brackets when one is set. */
export function creatorsText(creators: readonly DeclaredCreatorDto[] | null | undefined): string {
  return (creators ?? []).map((c) => (c.role ? `${c.name} (${declaredRoleLabel(c.role)})` : c.name)).join(', ');
}

/** Where a value comes from, for a tooltip or caption: "set here" / "from Shelf" / "from the library". */
export function sourceText(source: DeclaredFactSource | null | undefined, from: string | null | undefined): string {
  switch (source) {
    case 'Own': return 'set here';
    case 'Library': return from ? `from the ${from} library` : 'from the library';
    case 'Inherited': return from ? `from ${from}` : 'from a parent folder';
    default: return '';
  }
}

/** True when anything is declared (type or at least one creator). */
export function hasDeclared(e: EffectiveDeclaredFactsDto | null | undefined): boolean {
  return !!e && (!!e.type || (e.creators ?? []).length > 0);
}

/** One-line summary for a library row: "Manga (Japan) · 2 creators" ('' when nothing is declared). */
export function declaredSummary(type: DeclaredType | null | undefined, creators: readonly DeclaredCreatorDto[] | null | undefined): string {
  const parts: string[] = [];
  if (type) parts.push(declaredTypeLabel(type));
  const n = (creators ?? []).length;
  if (n > 0) parts.push(n === 1 ? creators![0].name : `${n} creators`);
  return parts.join(' · ');
}

/**
 * The conflict line of the Info panel: both sides, e.g.
 * "MangaUpdates says: Manga · Web Author" (null when nothing conflicts).
 */
export function conflictText(dto: NodeDeclaredFactsDto | null | undefined): string | null {
  const c = dto?.conflict;
  if (!c || (!c.type && !c.creators)) return null;
  const parts: string[] = [];
  if (c.type) parts.push(c.recordType || 'another type');
  if (c.creators) {
    const names = c.recordCreators ?? [];
    parts.push(names.length > 0 ? names.join(', ') : 'other creators');
  }
  return `${c.providerName} says: ${parts.join(' · ')}`;
}

/** A declared-facts save error in words; the server's validation codes first. */
export function declaredErrorText(err: { error?: string; message?: string; status?: number } | null | undefined): string {
  switch (err?.error) {
    case 'creator_name_invalid': return `A creator name must be 1-${DECLARED_MAX_NAME} characters.`;
    case 'creators_too_many': return `At most ${DECLARED_MAX_CREATORS} creators.`;
    case 'creator_role_invalid': return 'Pick a role from the list.';
    case 'not_a_folder': return 'Declared facts can only be set on a folder or a library.';
    default: break;
  }
  if (err?.status === 404) return 'This folder or library no longer exists.';
  return err?.message || 'Could not save.';
}
