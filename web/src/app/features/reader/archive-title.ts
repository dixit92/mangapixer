/**
 * The archive's name as the reader shows it (owner, 2026-10-02): the catalog's display name without its archive extension
 * ("Title - 0025 [Chapter 0023].cbz" -> "Title - 0025 [Chapter 0023]"). Only the extensions MangaPixer reads are removed
 * (the server's list, TitleNormalizer), so a name that merely contains a dot keeps it. Pure.
 */
export const ArchiveExtensions: readonly string[] = ['.cbz', '.zip', '.cbr', '.rar', '.cb7', '.7z', '.cbt', '.tar', '.pdf', '.epub'];

export function archiveTitle(displayName: string | null | undefined): string {
  const name = (displayName ?? '').trim();
  const lower = name.toLowerCase();
  const ext = ArchiveExtensions.find((e) => lower.endsWith(e));
  const title = ext ? name.slice(0, name.length - ext.length).trimEnd() : name;
  return title.length > 0 ? title : name;
}
