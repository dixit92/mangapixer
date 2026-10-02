import { describe, expect, it } from 'vitest';
import { EdgeAdvance, EdgeAdvanceWindowMs } from './edge-advance';

describe('EdgeAdvance', () => {
  it('arms on the first input and goes on the same direction within the window', () => {
    const e = new EdgeAdvance();
    expect(e.press(1, 1000)).toBe('arm');
    expect(e.armedDirection(1000 + EdgeAdvanceWindowMs)).toBe(1);
    expect(e.press(1, 1000 + EdgeAdvanceWindowMs)).toBe('go');
    expect(e.armedDirection(1000 + EdgeAdvanceWindowMs)).toBeNull(); // used up
  });

  it('re-arms after the window, on the other direction, and after reset', () => {
    const e = new EdgeAdvance();
    e.press(1, 0);
    expect(e.press(1, EdgeAdvanceWindowMs + 1)).toBe('arm');
    expect(e.press(-1, EdgeAdvanceWindowMs + 2)).toBe('arm');
    expect(e.armedDirection(EdgeAdvanceWindowMs + 2)).toBe(-1);
    e.reset();
    expect(e.press(-1, EdgeAdvanceWindowMs + 3)).toBe('arm');
  });
});
