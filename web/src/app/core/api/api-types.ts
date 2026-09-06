/**
 * API DTO types matching the .NET contracts in MangaPlex.Core/Api/ApiDtos.cs.
 * These are schema-derived HttpClient types, not auto-generated.
 * No source paths or private fields are present in these types.
 */

export type SortDirection = 'Ascending' | 'Descending';

export interface PageCursor {
  cursor: string | null;
  pageSize: number;
  direction: SortDirection;
}

export interface PageResponse<T> {
  items: T[];
  totalCount: number;
  nextCursor: string | null;
  hasMore: boolean;
}

export type CatalogNodeKind = 'Folder' | 'Archive';

export type CatalogNodeAvailability =
  | 'Available'
  | 'Preparing'
  | 'Unsupported'
  | 'Corrupt'
  | 'Unavailable'
  | 'Tombstoned';

export interface CatalogNodeDto {
  id: string;
  parentId: string;
  libraryId: string;
  kind: CatalogNodeKind;
  displayName: string;
  availability: CatalogNodeAvailability;
  coverUrl: string | null;
  childFolderCount: number | null;
  childArchiveCount: number | null;
  pageCount: number | null;
  readingState: ReadingState | null;
  lastReadPage: number | null;
}

export interface BreadcrumbEntry {
  id: string;
  displayName: string;
}

export interface BreadcrumbsDto {
  nodeId: string;
  trail: BreadcrumbEntry[];
}

export interface SearchResultsDto {
  query: string;
  items: CatalogNodeDto[];
  totalCount: number;
  nextCursor: string | null;
  hasMore: boolean;
}

export type ReadingState = 'Unread' | 'InProgress' | 'Completed';

export interface ReadingProgressDto {
  itemId: string;
  pageIndex: number;
  contentVersion: number;
  updatedAt: string;
  state: ReadingState;
  isStale: boolean;
}

export interface UpdateProgressRequest {
  pageIndex: number;
  expectedContentVersion: number;
}

export type ReaderMode = 'PagedLtr' | 'PagedRtl' | 'DoubleSpread' | 'VerticalWebtoon';

export interface UserPreferencesDto {
  defaultReaderMode: ReaderMode;
  preferDoubleSpread: boolean;
  reducedMotion: boolean;
  preferredBackground: string | null;
}

export interface LibraryDto {
  id: string;
  name: string;
  isScanning: boolean;
  itemCount: number | null;
  lastScanCompleted: string | null;
}

export interface ApiError {
  error: string;
  message: string;
  detail: string | null;
  correlationId: string | null;
}

export interface AuthUserDto {
  id: string;
  username: string;
  role: string;
  isAdmin: boolean;
}

export interface CsrfTokenDto {
  token: string;
}

export interface LoginRequest {
  username: string;
  password: string;
}

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}
