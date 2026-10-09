import { NodeDeclaredFactsDto } from '../../../core/api/api-types';
import {
  conflictText, creatorsText, declaredErrorText, declaredSummary, declaredTypeLabel, DECLARED_EDITION_OPTIONS, DECLARED_TYPE_OPTIONS, editionText,
  hasDeclared, hasEdition, sourceText,
} from './declared-facts';

/** Declared facts labels (1.28.0): sources, summaries, the conflict line (both sides), error words. */
describe('declared facts labels', () => {
  it('says where a value comes from', () => {
    expect(sourceText('Own', 'Series')).toBe('set here');
    expect(sourceText('Inherited', 'Shelf')).toBe('from Shelf');
    expect(sourceText('Inherited', null)).toBe('from a parent folder');
    expect(sourceText('Library', 'Comics')).toBe('from the Comics library');
    expect(sourceText(null, null)).toBe('');
  });

  it('names every type with its country of origin (1.30.0)', () => {
    expect(DECLARED_TYPE_OPTIONS.map((o) => o.label)).toEqual([
      'Manga (Japan)', 'Manhwa (Korea)', 'Manhua (China)', 'Webtoon (any country)', 'Comic (Western)', 'Graphic novel (Western)',
      'Novel (any country)',
    ]);
    expect(declaredTypeLabel('Manhua')).toBe('Manhua (China)');
    expect(declaredTypeLabel(null)).toBe('');
  });

  it('lists creators with their role and summarizes a scope', () => {
    expect(creatorsText([{ name: 'A Writer', role: 'writer' }, { name: 'An Artist', role: 'artist' }, { name: 'Someone' }]))
      .toBe('A Writer (Story), An Artist (Art), Someone');
    expect(declaredSummary('GraphicNovel', [])).toBe('Graphic novel (Western)');
    expect(declaredSummary('Manga', [{ name: 'One Name' }])).toBe('Manga (Japan) · One Name');
    expect(declaredSummary(null, [{ name: 'A' }, { name: 'B' }])).toBe('2 creators');
    expect(declaredSummary(null, null)).toBe('');
    expect(hasDeclared({ creators: [] })).toBe(false);
    expect(hasDeclared({ type: 'Novel' })).toBe(true);
  });

  it('shows the record side of a conflict, and nothing without one', () => {
    const base: NodeDeclaredFactsDto = { nodeId: 'n1', effective: { type: 'Manhwa', creators: [{ name: 'Someone Else' }] } };
    expect(conflictText(base)).toBeNull();
    expect(conflictText({ ...base, conflict: { providerName: 'MangaUpdates', type: true, recordType: 'Manga' } }))
      .toBe('MangaUpdates says: Manga');
    expect(conflictText({
      ...base,
      conflict: { providerName: 'MangaUpdates', type: true, recordType: 'Manga', creators: true, recordCreators: ['Web Author', 'Web Artist'] },
    })).toBe('MangaUpdates says: Manga · Web Author, Web Artist');
    expect(conflictText({ ...base, conflict: { providerName: 'MangaUpdates', type: false, creators: false } })).toBeNull();
  });

  it('explains save errors', () => {
    expect(declaredErrorText({ error: 'creators_too_many' })).toBe('At most 20 creators.');
    expect(declaredErrorText({ error: 'not_a_folder' })).toContain('folder or a library');
    expect(declaredErrorText({ error: 'http_error', status: 404 })).toContain('no longer exists');
    expect(declaredErrorText({ error: 'x', message: 'Server says no' })).toBe('Server says no');
    expect(declaredErrorText(null)).toBe('Could not save.');
  });
});

/** 1.39.0: the folder's own edition facts in words. */
describe('declared edition labels', () => {
  it('names the edition, its volumes and tracking off', () => {
    expect(editionText({ volumeTotal: 12, edition: 'Omnibus', tracking: true })).toBe('Omnibus - 12 volumes');
    expect(editionText({ volumeTotal: 1 })).toBe('1 volume');
    expect(editionText({ edition: 'Master', tracking: false })).toBe('Master · Completion not tracked');
    expect(editionText({ tracking: false })).toBe('Completion not tracked');
    expect(editionText(null)).toBe('');
    expect(hasEdition({ tracking: true })).toBe(false);
    expect(hasEdition({ tracking: false })).toBe(true);
    expect(DECLARED_EDITION_OPTIONS.map((o) => o.value)).toEqual(['Regular', 'Omnibus', 'Master', 'Deluxe']);
    expect(declaredErrorText({ error: 'volumes_invalid' })).toContain('from 1 to 999');
  });
});
