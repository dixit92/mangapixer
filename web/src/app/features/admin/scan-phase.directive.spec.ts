import { vi } from 'vitest';

import { alignToPageClock } from './scan-phase.directive';

/** 1.45.0: every running scan's ring and bar share one phase, pinned to the page's own clock. */
describe('alignToPageClock', () => {
  it('pins every running animation of the element to the document timeline\'s start', () => {
    const animations = [{ startTime: 1234 }, { startTime: 99 }] as unknown as Animation[];
    const el = document.createElement('span');
    Object.assign(el, { getAnimations: () => animations });
    alignToPageClock(el);
    expect(animations.map((a) => a.startTime)).toEqual([0, 0]);
    expect(el.style.animationDelay).toBe('');
  });

  it('without the Web Animations API: a negative delay of the time since the page loaded (the same phase to within a frame)', () => {
    vi.spyOn(performance, 'now').mockReturnValue(4321.4);
    const el = document.createElement('span');
    Object.assign(el, { getAnimations: undefined });
    alignToPageClock(el);
    expect(el.style.animationDelay).toBe('-4321ms');
    vi.restoreAllMocks();
  });
});
