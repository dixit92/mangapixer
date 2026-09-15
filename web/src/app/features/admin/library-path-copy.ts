import { SystemPlatform } from '../../core/api/api-types';

/**
 * Platform-aware copy for the "Register New Library" path field (lane
 * WinDeploy H, 1.13.0). The server has no mounts on Windows — the admin just
 * types a drive path — so the field label, placeholder, and the "no browse
 * root" hint must speak the server's idiom instead of always assuming a
 * container. Falls back to the historical container wording for `'linux'`,
 * `null`, and `undefined` (older servers that predate the platform field).
 */
export interface LibraryPathCopy {
  label: string;
  placeholder: string;
  noBrowseRootHint: string;
}

const LINUX_COPY: LibraryPathCopy = {
  label: 'Root Path (server-side mount)',
  placeholder: '/media/library1',
  noBrowseRootHint:
    'No media browse root is configured or accessible on the server. Mount your ' +
    'media read-only (e.g. at /media) or set MangaPlex:Storage:MediaRoot, then ' +
    'reload — or type the path above directly.',
};

const WINDOWS_COPY: LibraryPathCopy = {
  label: 'Library Folder Path',
  placeholder: 'D:\\Manga',
  noBrowseRootHint:
    'No media browse root is configured on the server. Type the folder path of ' +
    'the library on the server machine above (e.g. D:\\Manga) — or set ' +
    'MangaPlex:Storage:MediaRoot to a folder to enable browsing, then reload.',
};

export function libraryPathCopy(platform: SystemPlatform | null | undefined): LibraryPathCopy {
  return platform === 'windows' ? WINDOWS_COPY : LINUX_COPY;
}
