import { describe, it, expect } from 'vitest';
import { DOWNSCALE_FILTERS, DOWNSCALE_FILTER_OPTIONS, filterOptionHint } from './downscale-filters';

describe('Downscale Filter Vocabulary', () => {
  it('should pin the tuple to exactly [sharp, balanced, soft]', () => {
    expect(DOWNSCALE_FILTERS).toEqual(['sharp', 'balanced', 'soft']);
  });

  it('should have exactly three filters', () => {
    expect(DOWNSCALE_FILTERS).toHaveLength(3);
  });

  it('DOWNSCALE_FILTER_OPTIONS values should match DOWNSCALE_FILTERS', () => {
    expect(DOWNSCALE_FILTER_OPTIONS.map(o => o.value)).toEqual(DOWNSCALE_FILTERS);
  });

  it('should provide hints for all filters', () => {
    DOWNSCALE_FILTERS.forEach(filter => {
      expect(filterOptionHint(filter)).toBeTruthy();
    });
  });

  it('sharp hint should be defined', () => {
    expect(filterOptionHint('sharp')).toBe('Crisp lines, may moire on screentones');
  });

  it('balanced hint should be defined', () => {
    expect(filterOptionHint('balanced')).toBe('Balanced (default)');
  });

  it('soft hint should be defined', () => {
    expect(filterOptionHint('soft')).toBe('Smoothest screentones');
  });
});
