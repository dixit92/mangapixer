import { JumpIndexBucketDto } from '../../core/api/api-types';
import { LONG_LEVEL_ROWS, bubbleTitle, bucketIndexAt, isLongLevel, labelStride, markText } from './jump-scrub';

const bucket = (label: string, count: number, range?: string): JumpIndexBucketDto => ({ label, count, firstCursor: null, range });

/** The phone scrub strip's pure parts (1.49.0). */
describe('jump-scrub', () => {
  describe('isLongLevel', () => {
    it('is long from 50 rows (the buckets added up) with two or more buckets', () => {
      expect(LONG_LEVEL_ROWS).toBe(50);
      expect(isLongLevel([bucket('A', 25), bucket('B', 25)])).toBe(true);
      expect(isLongLevel([bucket('A', 25), bucket('B', 24)])).toBe(false);
      expect(isLongLevel([bucket('A', 30), bucket('B', 20), bucket('C', 100)])).toBe(true);
    });

    it('a single bucket is never long, however many rows (nothing to scrub through)', () => {
      expect(isLongLevel([bucket('A', 500)])).toBe(false);
      expect(isLongLevel([])).toBe(false);
    });

    it('takes another threshold', () => {
      expect(isLongLevel([bucket('A', 3), bucket('B', 3)], 6)).toBe(true);
      expect(isLongLevel([bucket('A', 3), bucket('B', 2)], 6)).toBe(false);
    });
  });

  describe('bucketIndexAt', () => {
    it('cuts the strip into equal slices from its top', () => {
      // 5 buckets over 100..600: slices of 100 px.
      const at = (y: number) => bucketIndexAt(y, 100, 500, 5);
      expect(at(100)).toBe(0);
      expect(at(199)).toBe(0);
      expect(at(200)).toBe(1);
      expect(at(350)).toBe(2);
      expect(at(599)).toBe(4);
    });

    it('clamps a drag above or below the strip to the first / last bucket', () => {
      expect(bucketIndexAt(-50, 100, 500, 5)).toBe(0);
      expect(bucketIndexAt(600, 100, 500, 5)).toBe(4);
      expect(bucketIndexAt(5000, 100, 500, 5)).toBe(4);
    });

    it('has nothing to pick without buckets, and takes the first bucket for a strip without height', () => {
      expect(bucketIndexAt(10, 0, 100, 0)).toBe(-1);
      expect(bucketIndexAt(10, 0, 0, 5)).toBe(0);
    });
  });

  describe('thinning', () => {
    it('labels every mark while each has room (18 px)', () => {
      expect(labelStride(10, 400)).toBe(1);
      expect(labelStride(27, 486)).toBe(1); // exactly 18 px each
    });

    it('labels every Nth mark when the strip is crowded', () => {
      expect(labelStride(27, 400)).toBe(2); // 14.8 px each
      expect(labelStride(60, 400)).toBe(3); // 6.7 px each
      expect(labelStride(100, 400)).toBe(5);
    });

    it('is 1 for no buckets or no height', () => {
      expect(labelStride(0, 400)).toBe(1);
      expect(labelStride(10, 0)).toBe(1);
    });

    it('a mark carries its label only on every stride-th index; a word label is always a dot', () => {
      const marks = ['A', 'B', 'C', 'D', 'E'].map((l) => bucket(l, 10));
      expect(marks.map((b, i) => markText(b, i, 2))).toEqual(['A', null, 'C', null, 'E']);
      expect(markText(bucket('Folders', 3), 0, 1)).toBeNull();
      expect(markText(bucket('Other', 3), 0, 1)).toBeNull();
      expect(markText(bucket('100', 3), 0, 1)).toBe('100');
      expect(markText(bucket('#', 3), 0, 1)).toBe('#');
    });
  });

  it('the bubble shows a number chip\'s range and else the label', () => {
    expect(bubbleTitle(bucket('50', 22, '50-99'))).toBe('50-99');
    expect(bubbleTitle(bucket('Folders', 2))).toBe('Folders');
  });
});
