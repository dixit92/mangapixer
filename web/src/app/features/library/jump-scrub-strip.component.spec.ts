import { TestBed } from '@angular/core/testing';

import { JumpIndexBucketDto } from '../../core/api/api-types';
import { JumpRailComponent } from './jump-rail.component';
import { JumpScrubStripComponent } from './jump-scrub-strip.component';

const bucket = (label: string, count: number, extra: Partial<JumpIndexBucketDto> = {}): JumpIndexBucketDto =>
  ({ label, count, firstCursor: `k-${label}`, firstNodeId: `n-${label}`, ...extra });

/** Five chips, 60 rows: a long level. */
const LONG = [bucket('A', 20), bucket('B', 10), bucket('C', 10), bucket('D', 10), bucket('E', 10)];
const SHORT = [bucket('A', 10), bucket('B', 10), bucket('C', 10)];

/** The strip is laid out as 100..600 px (500 px tall: five slices of 100 px). */
const STRIP_TOP = 100;
const STRIP_HEIGHT = 500;

function create(buckets: JumpIndexBucketDto[], active: JumpIndexBucketDto | null = null) {
  TestBed.configureTestingModule({ imports: [JumpScrubStripComponent] });
  const fixture = TestBed.createComponent(JumpScrubStripComponent);
  fixture.componentRef.setInput('buckets', buckets);
  fixture.componentRef.setInput('active', active);
  fixture.componentRef.setInput('top', 56);
  fixture.detectChanges();
  const el = fixture.nativeElement as HTMLElement;
  const strip = el.querySelector<HTMLElement>('.scrub');
  if (strip) strip.getBoundingClientRect = () => ({ top: STRIP_TOP, height: STRIP_HEIGHT, bottom: STRIP_TOP + STRIP_HEIGHT, left: 346, right: 390, width: 44, x: 346, y: STRIP_TOP, toJSON: () => ({}) });
  const jumped: JumpIndexBucketDto[] = [];
  fixture.componentInstance.jump.subscribe((b) => jumped.push(b));
  const fire = (type: string, clientY: number, pointerId = 1) => {
    strip!.dispatchEvent(new PointerEvent(type, { clientY, pointerId, pointerType: 'touch', bubbles: true, cancelable: true }));
    fixture.detectChanges();
  };
  return { fixture, el, strip, jumped, fire };
}

/** The phone scrub strip (1.49.0): a long level only, scrub with a bubble, release jumps, cancel does not. */
describe('JumpScrubStripComponent', () => {
  it('renders no strip on a short level (the picker stands alone) or with a single chip', () => {
    expect(create(SHORT).strip).toBeNull();
    TestBed.resetTestingModule();
    expect(create([bucket('A', 500)]).strip).toBeNull();
  });

  it('renders one mark per chip on a long level, hidden from assistive technology and not focusable', () => {
    const { strip, el } = create(LONG);
    expect(strip).not.toBeNull();
    expect(strip!.getAttribute('aria-hidden')).toBe('true');
    expect(strip!.querySelector('button, a, [tabindex]')).toBeNull();
    expect(Array.from(el.querySelectorAll('.mark')).map((m) => m.textContent?.trim())).toEqual(['A', 'B', 'C', 'D', 'E']);
  });

  it('is offset below the top bar', () => {
    expect(create(LONG).strip!.style.top).toBe('64px'); // 56 + 8
  });

  it('highlights the scroll-spy chip while idle', () => {
    const { el } = create(LONG, LONG[2]);
    expect(Array.from(el.querySelectorAll('.mark')).map((m) => m.classList.contains('on'))).toEqual([false, false, true, false, false]);
  });

  it('scrubbing shows a bubble with the bucket under the finger and jumps nowhere until release', () => {
    const { el, jumped, fire } = create(LONG);
    expect(el.querySelector('.bubble')).toBeNull();

    fire('pointerdown', 120); // slice 0
    expect(el.querySelector('.bubble-label')!.textContent).toBe('A');
    expect(el.querySelector('.bubble-count')!.textContent).toBe('20');
    fire('pointermove', 330); // slice 2
    expect(el.querySelector('.bubble-label')!.textContent).toBe('C');
    expect(Array.from(el.querySelectorAll('.mark')).map((m) => m.classList.contains('on'))).toEqual([false, false, true, false, false]);
    fire('pointermove', 590); // slice 4
    expect(el.querySelector('.bubble-label')!.textContent).toBe('E');
    expect(jumped).toEqual([]);
  });

  it('releasing jumps to the bucket under the finger (the same bucket object a chip emits) and hides the bubble', () => {
    const { el, jumped, fire } = create(LONG);
    fire('pointerdown', 120);
    fire('pointermove', 450); // slice 3
    fire('pointerup', 450);
    expect(jumped).toEqual([LONG[3]]);
    expect(jumped[0]).toBe(LONG[3]);
    expect(el.querySelector('.bubble')).toBeNull();
  });

  it('a tap is a drag of zero length: it jumps to the tapped chip', () => {
    const { jumped, fire } = create(LONG);
    fire('pointerdown', 230);
    fire('pointerup', 230);
    expect(jumped).toEqual([LONG[1]]);
  });

  it('a drag past either end sticks to the first / last chip', () => {
    const { jumped, fire } = create(LONG);
    fire('pointerdown', 300);
    fire('pointermove', 20);
    fire('pointerup', 20);
    fire('pointerdown', 300);
    fire('pointermove', 900);
    fire('pointerup', 900);
    expect(jumped).toEqual([LONG[0], LONG[4]]);
  });

  it('a cancelled gesture (the system took it) jumps nowhere and hides the bubble', () => {
    const { el, jumped, fire } = create(LONG);
    fire('pointerdown', 300);
    expect(el.querySelector('.bubble')).not.toBeNull();
    fire('pointercancel', 300);
    expect(el.querySelector('.bubble')).toBeNull();
    fire('pointerup', 300); // a late up of the dead gesture does nothing
    expect(jumped).toEqual([]);
  });

  it('ignores another finger while one is scrubbing', () => {
    const { jumped, fire } = create(LONG);
    fire('pointerdown', 120, 1);
    fire('pointermove', 450, 2);
    fire('pointerup', 450, 2);
    expect(jumped).toEqual([]);
    fire('pointerup', 120, 1);
    expect(jumped).toEqual([LONG[0]]);
  });

  it('the touch-down is a default-prevented event, so the page does not scroll or select', () => {
    const { strip, fixture } = create(LONG);
    const down = new PointerEvent('pointerdown', { clientY: 200, pointerId: 1, pointerType: 'touch', bubbles: true, cancelable: true });
    strip!.dispatchEvent(down);
    fixture.detectChanges();
    expect(down.defaultPrevented).toBe(true);
  });

  it('thins the labels when there are more chips than fit and keeps a dot for the rest', () => {
    const many = Array.from({ length: 40 }, (_, i) => bucket(String(i * 10), 5));
    const { el, fixture } = create(many);
    const texts = Array.from(el.querySelectorAll('.mark')).map((m) => m.textContent?.trim() ?? '');
    expect(fixture.componentInstance.stride()).toBeGreaterThan(1);
    expect(texts[0]).toBe('0');
    expect(texts[1]).toBe('');
    expect(texts.filter((t) => t !== '').length).toBeLessThan(40);
    expect(el.querySelectorAll('.mark.dot').length).toBeGreaterThan(0);
  });

  it('a number chip shows its range in the bubble', () => {
    const numbers = [bucket('0', 50, { range: '0-49' }), bucket('50', 22, { range: '50-99' })];
    const { el, fire } = create(numbers);
    fire('pointerdown', 590);
    expect(el.querySelector('.bubble-label')!.textContent).toBe('50-99');
  });
});

/** The strip is part of the jump rail (the one mount hunk): same buckets in, same jump out. */
describe('the jump rail with the scrub strip', () => {
  function createRail(buckets: JumpIndexBucketDto[]) {
    TestBed.configureTestingModule({ imports: [JumpRailComponent] });
    const fixture = TestBed.createComponent(JumpRailComponent);
    fixture.componentRef.setInput('buckets', buckets);
    fixture.componentRef.setInput('top', 56);
    fixture.detectChanges();
    return { fixture, el: fixture.nativeElement as HTMLElement };
  }

  it('mounts the strip on a long level and not on a short one; the picker button is on both', () => {
    const long = createRail(LONG);
    expect(long.el.querySelector('.scrub')).not.toBeNull();
    expect(long.el.querySelector('.jump-menu-trigger')).not.toBeNull();
    TestBed.resetTestingModule();
    const short = createRail(SHORT);
    expect(short.el.querySelector('.scrub')).toBeNull();
    expect(short.el.querySelector('.jump-menu-trigger')).not.toBeNull();
  });

  it('a release on the strip comes out of the rail as a jump', () => {
    const { fixture, el } = createRail(LONG);
    const jumped: JumpIndexBucketDto[] = [];
    fixture.componentInstance.jump.subscribe((b) => jumped.push(b));
    const strip = el.querySelector<HTMLElement>('.scrub')!;
    strip.getBoundingClientRect = () => ({ top: 100, height: 500, bottom: 600, left: 346, right: 390, width: 44, x: 346, y: 100, toJSON: () => ({}) });
    strip.dispatchEvent(new PointerEvent('pointerdown', { clientY: 250, pointerId: 1, bubbles: true, cancelable: true }));
    strip.dispatchEvent(new PointerEvent('pointerup', { clientY: 250, pointerId: 1, bubbles: true, cancelable: true }));
    expect(jumped).toEqual([LONG[1]]);
  });
});
