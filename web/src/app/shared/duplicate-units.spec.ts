import { DuplicateUnitDto } from '../core/api/api-types';
import { duplicateCountLabel, duplicateLine, duplicateListText, duplicateTotals, duplicatesLabel } from './duplicate-units';

/** The shared wording of duplicate chapter / volume numbers (1.31.0). */
describe('duplicate units wording', () => {
  const chapter = (number: string, files = 2): DuplicateUnitDto => ({ kind: 'Chapter', number, files });
  const volume = (number: string, files = 2): DuplicateUnitDto => ({ kind: 'Volume', number, files });

  it('words one duplicate as "Chapter 1: 2 files"', () => {
    expect(duplicateLine(chapter('1'))).toBe('Chapter 1: 2 files');
    expect(duplicateLine(volume('3', 3))).toBe('Volume 3: 3 files');
    expect(duplicateLine(chapter('45.5'))).toBe('Chapter 45.5: 2 files');
  });

  it('counts per kind with the right plural', () => {
    expect(duplicateCountLabel(1, 0)).toBe('1 duplicate chapter');
    expect(duplicateCountLabel(2, 1)).toBe('2 duplicate chapters, 1 duplicate volume');
    expect(duplicateCountLabel(0, 2)).toBe('2 duplicate volumes');
    expect(duplicateCountLabel(0, 0)).toBe('');
  });

  it('totals and labels a list, and tolerates a missing one', () => {
    const list = [chapter('1'), chapter('2'), volume('3')];
    expect(duplicateTotals(list)).toEqual({ chapters: 2, volumes: 1 });
    expect(duplicatesLabel(list)).toBe('2 duplicate chapters, 1 duplicate volume');
    expect(duplicatesLabel(undefined)).toBe('');
    expect(duplicatesLabel(null)).toBe('');
  });

  it('lists the duplicates and says how many the server left out', () => {
    expect(duplicateListText([chapter('1'), chapter('2')])).toBe('Chapter 1: 2 files, Chapter 2: 2 files');
    expect(duplicateListText([chapter('1')], 4)).toBe('Chapter 1: 2 files and 3 more');
    expect(duplicateListText([chapter('1')], 1)).toBe('Chapter 1: 2 files');
    expect(duplicateListText(undefined)).toBe('');
  });
});
