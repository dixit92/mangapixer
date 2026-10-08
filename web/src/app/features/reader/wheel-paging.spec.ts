import { describe, expect, it } from 'vitest';
import { WheelLinePx, WheelMinDeltaPx, WheelPager, WheelQuietGapMs, wheelPixels } from './wheel-paging';

const Pixel = 0;
const Line = 1;
const Page = 2;

/** One vertical wheel event in pixel units. */
function down(at: number, deltaY = 100) {
  return { deltaX: 0, deltaY, deltaMode: Pixel, at };
}

describe('wheelPixels', () => {
  it('converts line and page units to CSS pixels; pixels pass through', () => {
    expect(wheelPixels(3, Line, 800)).toBe(3 * WheelLinePx);
    expect(wheelPixels(-1, Page, 800)).toBe(-800);
    expect(wheelPixels(42, Pixel, 800)).toBe(42);
  });

  it('a non-finite delta counts as no movement', () => {
    expect(wheelPixels(Number.NaN, Pixel, 800)).toBe(0);
    expect(wheelPixels(Number.POSITIVE_INFINITY, Line, 800)).toBe(0);
  });
});

describe('WheelPager', () => {
  it('wheel down turns to the next page, wheel up to the previous one', () => {
    const w = new WheelPager();
    expect(w.input(down(0, 100), true)).toBe('next');
    expect(w.input(down(1000, -100), true)).toBe('prev');
  });

  it('one gesture = one page: a long inertial stream turns once and swallows the rest', () => {
    const w = new WheelPager();
    const steps: string[] = [];
    // A trackpad flick: 90 events a frame apart (1.5 s), the deltas decaying from 60 px to 1 px.
    for (let i = 0; i < 90; i++) steps.push(w.input(down(i * 16, Math.max(1, 60 - i)), true));
    expect(steps[0]).toBe('next');
    expect(steps.slice(1).every((s) => s === 'swallow')).toBe(true);
  });

  it('a fresh gesture after the quiet gap turns again (a mouse notch per page)', () => {
    const w = new WheelPager();
    expect(w.input(down(0), true)).toBe('next');
    expect(w.input(down(WheelQuietGapMs - 1), true)).toBe('swallow'); // still the same gesture
    // The gap is measured from the LAST event of the gesture, not its first.
    expect(w.input(down(2 * WheelQuietGapMs - 2), true)).toBe('swallow');
    expect(w.input(down(3 * WheelQuietGapMs), true)).toBe('next');
  });

  it('notches spun faster than the quiet gap stay one gesture', () => {
    const w = new WheelPager();
    const steps = [0, 60, 120, 180, 240].map((t) => w.input(down(t), true));
    expect(steps).toEqual(['next', 'swallow', 'swallow', 'swallow', 'swallow']);
  });

  it('reversing direction inside a gesture does not turn back', () => {
    const w = new WheelPager();
    expect(w.input(down(0, 80), true)).toBe('next');
    expect(w.input(down(50, -80), true)).toBe('swallow');
  });

  it('ignores a horizontal wheel or trackpad swipe (never ours), also inside a gesture', () => {
    const w = new WheelPager();
    expect(w.input({ deltaX: 120, deltaY: 10, deltaMode: Pixel, at: 0 }, true)).toBe('ignore');
    expect(w.input({ deltaX: -120, deltaY: 0, deltaMode: Pixel, at: 500 }, true)).toBe('ignore');
    expect(w.input(down(1000), true)).toBe('next');
    expect(w.input({ deltaX: 90, deltaY: 5, deltaMode: Pixel, at: 1010 }, true)).toBe('ignore');
    // The sideways event still belonged to the gesture: its tail stays swallowed.
    expect(w.input(down(1010 + WheelQuietGapMs - 1), true)).toBe('swallow');
  });

  it('tiny deltas (resting-finger jitter, inertia tails) never start a turn', () => {
    const w = new WheelPager();
    expect(w.input(down(0, WheelMinDeltaPx - 0.5), true)).toBe('ignore');
    expect(w.input(down(500, -(WheelMinDeltaPx - 0.5)), true)).toBe('ignore');
    expect(w.input(down(1000, 0), true)).toBe('ignore');
    expect(w.input(down(1500, WheelMinDeltaPx), true)).toBe('next');
  });

  it('a scrollable page starts nothing (native scrolling)', () => {
    const w = new WheelPager();
    expect(w.input(down(0), false)).toBe('ignore');
    expect(w.input(down(16), false)).toBe('ignore');
    expect(w.input(down(32), true)).toBe('next'); // the page became fixed: the next event may turn
  });

  it('the tail of a gesture that turned stays swallowed even if the new page scrolls', () => {
    const w = new WheelPager();
    expect(w.input(down(0), true)).toBe('next');
    expect(w.input(down(16), false)).toBe('swallow');
    expect(w.input(down(32), false)).toBe('swallow');
    expect(w.input(down(32 + WheelQuietGapMs), false)).toBe('ignore'); // a fresh gesture scrolls natively
  });

  it('line-mode deltas (Firefox mouse notches) turn a page; page-mode too', () => {
    const w = new WheelPager();
    expect(w.input({ deltaX: 0, deltaY: 3, deltaMode: Line, at: 0 }, true)).toBe('next');
    expect(w.input({ deltaX: 0, deltaY: -1, deltaMode: Page, at: 1000 }, true, 900)).toBe('prev');
    // A line-mode sideways notch dominated by deltaX is still sideways after the conversion.
    expect(w.input({ deltaX: 2, deltaY: 1, deltaMode: Line, at: 2000 }, true)).toBe('ignore');
  });

  it('reset forgets a running gesture; a clock that went backwards starts a fresh one', () => {
    const w = new WheelPager();
    expect(w.input(down(1000), true)).toBe('next');
    w.reset();
    expect(w.input(down(1010), true)).toBe('next');
    expect(w.input(down(500), true)).toBe('next');
  });
});
