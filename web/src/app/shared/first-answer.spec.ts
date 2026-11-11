import { Injector, runInInjectionContext } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { FIRST_ANSWER_SPINNER_DELAY_MS, FirstAnswer } from './first-answer';

/** 1.53.0 (visual audit V2): the first-request state shared by browse, Home and Search. */
describe('FirstAnswer', () => {
  let injector: Injector;
  const make = () => runInInjectionContext(injector, () => new FirstAnswer());

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    TestBed.configureTestingModule({});
    injector = TestBed.inject(Injector);
  });

  afterEach(() => vi.useRealTimers());

  it('is not loaded, and shows no spinner, until the delay passes', () => {
    const a = make();
    a.start();
    expect([a.state(), a.slow(), a.loaded(), a.failed()]).toEqual(['pending', false, false, false]);
    vi.advanceTimersByTime(FIRST_ANSWER_SPINNER_DELAY_MS - 1);
    expect(a.slow()).toBe(false);
    vi.advanceTimersByTime(1);
    expect(a.slow()).toBe(true);
  });

  it('a fast answer never shows the spinner', () => {
    const a = make();
    a.start();
    vi.advanceTimersByTime(100);
    a.succeed();
    vi.advanceTimersByTime(1000);
    expect([a.state(), a.slow(), a.loaded()]).toEqual(['loaded', false, true]);
  });

  it('a failure ends the spinner; a new start goes back to pending', () => {
    const a = make();
    a.start();
    vi.advanceTimersByTime(500);
    a.fail();
    expect([a.failed(), a.slow()]).toEqual([true, false]);
    a.start();
    expect([a.state(), a.slow()]).toEqual(['pending', false]);
    vi.advanceTimersByTime(FIRST_ANSWER_SPINNER_DELAY_MS);
    expect(a.slow()).toBe(true);
  });

  it('1.53.0 pre-cut (guide finding 3): a start while still waiting keeps the clock - the spinner does not blink off', () => {
    // Browse starts the wait when the page opens (its view settings come first), then starts again for the folder request.
    const a = make();
    a.start();
    vi.advanceTimersByTime(FIRST_ANSWER_SPINNER_DELAY_MS);
    expect(a.slow()).toBe(true);
    a.start();
    expect([a.state(), a.slow()]).toEqual(['pending', true]);
    expect(vi.getTimerCount()).toBe(0);
    // Not yet slow: the second start neither restarts nor doubles the timer.
    const b = make();
    b.start();
    vi.advanceTimersByTime(200);
    b.start();
    expect(vi.getTimerCount()).toBe(1);
    vi.advanceTimersByTime(FIRST_ANSWER_SPINNER_DELAY_MS - 200);
    expect(b.slow()).toBe(true);
    // After an answer, a new start is a new wait.
    b.succeed();
    b.start();
    expect([b.state(), b.slow()]).toEqual(['pending', false]);
  });

  it('clears its timer with the page', () => {
    const a = make();
    a.start();
    expect(vi.getTimerCount()).toBe(1);
    TestBed.resetTestingModule();
    expect(vi.getTimerCount()).toBe(0);
    expect(a.slow()).toBe(false);
  });
});
