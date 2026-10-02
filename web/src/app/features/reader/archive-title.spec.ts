import { describe, expect, it } from 'vitest';
import { archiveTitle } from './archive-title';

describe('archiveTitle', () => {
  it('removes the archive extension, whatever its case', () => {
    expect(archiveTitle('Series - 0025 [Chapter 0023].cbz')).toBe('Series - 0025 [Chapter 0023]');
    expect(archiveTitle('Series v01.CBR')).toBe('Series v01');
    expect(archiveTitle('Series v02.7z')).toBe('Series v02');
  });

  it('keeps a name without a known archive extension, and dots inside the name', () => {
    expect(archiveTitle('Mr. Series No. 6')).toBe('Mr. Series No. 6');
    expect(archiveTitle('Series v1.5')).toBe('Series v1.5');
    expect(archiveTitle('notes.txt')).toBe('notes.txt');
  });

  it('never returns an empty title', () => {
    expect(archiveTitle('.cbz')).toBe('.cbz');
    expect(archiveTitle('')).toBe('');
    expect(archiveTitle(null)).toBe('');
  });
});
