import { libraryPathCopy } from './library-path-copy';

/**
 * Lane WinDeploy H (1.13.0): the admin Libraries "register" field must speak
 * the server's platform idiom — no /media mount jargon on Windows, and no
 * regression to the existing Linux/container wording.
 */
describe('libraryPathCopy', () => {
  it('gives Windows copy: no mount/container jargon, a drive-letter placeholder', () => {
    const copy = libraryPathCopy('windows');
    expect(copy.label).toBe('Library Folder Path');
    expect(copy.placeholder).toBe('D:\\Manga');
    expect(copy.noBrowseRootHint).not.toMatch(/mount/i);
    expect(copy.noBrowseRootHint).not.toContain('/media');
    expect(copy.noBrowseRootHint).toContain('MangaPlex:Storage:MediaRoot');
  });

  it('keeps the existing container wording for a Linux server', () => {
    const copy = libraryPathCopy('linux');
    expect(copy.label).toBe('Root Path (server-side mount)');
    expect(copy.placeholder).toBe('/media/library1');
    expect(copy.noBrowseRootHint).toContain('Mount your media read-only');
  });

  it('falls back to the container wording when the platform is unknown (older server)', () => {
    expect(libraryPathCopy(null)).toEqual(libraryPathCopy('linux'));
    expect(libraryPathCopy(undefined)).toEqual(libraryPathCopy('linux'));
  });
});
