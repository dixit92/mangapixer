import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { JumpRailComponent, bucketOwners } from './jump-rail.component';
import { JumpIndexBucketDto } from '../../core/api/api-types';

/** The jump rail component (1.41.0) and its scroll-spy helper. */
describe('JumpRailComponent', () => {
  const letters: JumpIndexBucketDto[] = [
    { label: 'Folders', count: 2, firstCursor: null, firstNodeId: 'f1' },
    { label: 'A', count: 3, firstCursor: 'k1', firstNodeId: 'a1' },
    { label: 'B', count: 1, firstCursor: 'k2', firstNodeId: 'b1' },
  ];
  const numbers: JumpIndexBucketDto[] = [
    { label: '0', count: 50, firstCursor: null, firstNodeId: 'c0', range: '0-49' },
    { label: '50', count: 22, firstCursor: 'k49', firstNodeId: 'c50', range: '50-99' },
  ];

  function create(buckets: JumpIndexBucketDto[], mode: 'letters' | 'numbers', active: JumpIndexBucketDto | null = null) {
    TestBed.configureTestingModule({ imports: [JumpRailComponent], providers: [provideNoopAnimations()] });
    const fixture = TestBed.createComponent(JumpRailComponent);
    fixture.componentRef.setInput('buckets', buckets);
    fixture.componentRef.setInput('mode', mode);
    fixture.componentRef.setInput('active', active);
    fixture.componentRef.setInput('top', 42);
    fixture.detectChanges();
    return { fixture, comp: fixture.componentInstance, el: fixture.nativeElement as HTMLElement };
  }

  it('renders one chip per bucket in order, a wide chip for a word label, and emits the clicked bucket', () => {
    const { comp, el } = create(letters, 'letters', letters[1]);
    const emitted: JumpIndexBucketDto[] = [];
    comp.jump.subscribe((b) => emitted.push(b));

    const chips = Array.from(el.querySelectorAll<HTMLElement>('nav.jump-rail .jump-chip'));
    expect(chips.map((c) => c.textContent?.trim())).toEqual(['Folders', 'A', 'B']);
    expect(chips[0].classList.contains('wide')).toBe(true);
    expect(chips[1].classList.contains('active')).toBe(true);
    expect((el.querySelector('nav.jump-rail') as HTMLElement).style.top).toBe('42px');
    expect(el.querySelector('nav.jump-rail')!.getAttribute('aria-label')).toBe('Jump to letter');

    chips[2].click();
    expect(emitted).toEqual([letters[2]]);
  });

  it('numbers: the chip shows the range start, the long form (tooltip, phone menu, trigger) the range', () => {
    const { comp, el } = create(numbers, 'numbers', numbers[1]);

    expect(Array.from(el.querySelectorAll('nav.jump-rail .jump-chip')).map((c) => c.textContent?.trim())).toEqual(['0', '50']);
    expect(comp.titleOf(numbers[0])).toBe('0-49');
    expect(comp.triggerText()).toBe('50-99');
    expect(el.querySelector('.jump-menu-trigger')!.getAttribute('aria-label')).toBe('Jump to number');
  });

  it('the phone trigger reads A-Z / Jump until a bucket is active', () => {
    expect(create(letters, 'letters').comp.triggerText()).toBe('A-Z');
    TestBed.resetTestingModule();
    expect(create(numbers, 'numbers').comp.triggerText()).toBe('Jump');
  });

  it('shows nothing for fewer than two buckets', () => {
    const { el } = create([letters[1]], 'letters');
    expect(el.querySelector('nav.jump-rail')).toBeNull();
    expect(el.querySelector('.jump-menu-trigger')).toBeNull();
  });

  describe('bucketOwners', () => {
    const ids = (...xs: string[]) => xs.map((id) => ({ id }));

    it('assigns each card the last bucket starting at or above it', () => {
      expect(bucketOwners(ids('f1', 'f2', 'a1', 'a2', 'a3', 'b1'), letters)).toEqual([0, 0, 1, 1, 1, 2]);
    });

    it('cards above the first bucket start in the window belong to the bucket before it (a window that starts mid-bucket)', () => {
      expect(bucketOwners(ids('a2', 'a3', 'b1'), letters)).toEqual([1, 1, 2]);
    });

    it('is -1 everywhere when no bucket starts in the window', () => {
      expect(bucketOwners(ids('a2', 'a3'), letters)).toEqual([-1, -1]);
      expect(bucketOwners(ids('x'), [{ label: 'A', count: 1, firstCursor: null }])).toEqual([-1]);
    });
  });
});
