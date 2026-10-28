import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { JumpRailComponent, MIN_ROWS_FOR_RAIL, bucketOwners } from './jump-rail.component';
import { JumpIndexBucketDto } from '../../core/api/api-types';

/** The jump rail component (1.41.0) and its scroll-spy helper. */
describe('JumpRailComponent', () => {
  const letters: JumpIndexBucketDto[] = [
    { label: 'Folders', count: 2, firstCursor: null, firstNodeId: 'f1' },
    { label: 'A', count: 9, firstCursor: 'k1', firstNodeId: 'a1' },
    { label: 'B', count: 4, firstCursor: 'k2', firstNodeId: 'b1' },
  ];
  const numbers: JumpIndexBucketDto[] = [
    { label: '0', count: 50, firstCursor: null, firstNodeId: 'c0', range: '0-49' },
    { label: '50', count: 22, firstCursor: 'k49', firstNodeId: 'c50', range: '50-99' },
  ];

  function create(buckets: JumpIndexBucketDto[], mode: 'letters' | 'numbers' | 'dates', active: JumpIndexBucketDto | null = null) {
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

  it('1.49.0: a level under 12 rows has no rail (the sum of the chips counts), 12 rows has one', () => {
    expect(MIN_ROWS_FOR_RAIL).toBe(12);
    const few: JumpIndexBucketDto[] = [
      { label: 'A', count: 6, firstCursor: null, firstNodeId: 'a1' },
      { label: 'B', count: 5, firstCursor: 'k1', firstNodeId: 'b1' },
    ];
    const hidden = create(few, 'letters');
    expect(hidden.el.querySelector('nav.jump-rail')).toBeNull();
    expect(hidden.el.querySelector('.jump-menu-trigger')).toBeNull();
    TestBed.resetTestingModule();

    const exactly = create([{ ...few[0], count: 7 }, few[1]], 'letters');
    expect(exactly.el.querySelectorAll('nav.jump-rail .jump-chip').length).toBe(2);
  });

  it('1.49.0: dates mode - time chips, a clock icon, "Jump to date"', () => {
    const dates: JumpIndexBucketDto[] = [
      { label: 'Today', count: 5, firstCursor: null, firstNodeId: 'n1' },
      { label: 'This week', count: 4, firstCursor: 'a:1:1:1', firstNodeId: 'n2' },
      { label: '2025', count: 20, firstCursor: 'a:1:2:2', firstNodeId: 'n3' },
    ];
    const { comp, el } = create(dates, 'dates', dates[1]);

    expect(Array.from(el.querySelectorAll('nav.jump-rail .jump-chip')).map((c) => c.textContent?.trim())).toEqual(['Today', 'This week', '2025']);
    expect(el.querySelector('nav.jump-rail')!.getAttribute('aria-label')).toBe('Jump to date');
    expect(el.querySelector('.jump-menu-trigger mat-icon')!.textContent?.trim()).toBe('schedule');
    expect(comp.triggerText()).toBe('This week');
  });

  it('1.49.0: a level whose folders and files both have chips labels each kind once (a heading before its first chip)', () => {
    const grouped: JumpIndexBucketDto[] = [
      { label: 'A', count: 8, firstCursor: null, firstNodeId: 'f1', group: 'folders' },
      { label: 'B', count: 8, firstCursor: 'k1', firstNodeId: 'f2', group: 'folders' },
      { label: 'A', count: 8, firstCursor: 'k2', firstNodeId: 'a1', group: 'files' },
      { label: 'B', count: 8, firstCursor: 'k3', firstNodeId: 'a2', group: 'files' },
    ];
    const { comp, el } = create(grouped, 'letters');

    expect(Array.from(el.querySelectorAll('nav.jump-rail .jump-group')).map((c) => c.textContent?.trim())).toEqual(['Folders', 'Files']);
    expect(el.querySelectorAll('nav.jump-rail .jump-chip').length).toBe(4);
    expect(comp.groupLabel(0)).toBe('Folders');
    expect(comp.groupLabel(1)).toBeNull();
    expect(comp.groupLabel(2)).toBe('Files');
    // Ungrouped levels show no heading.
    TestBed.resetTestingModule();
    expect(create(letters, 'letters').el.querySelector('.jump-group')).toBeNull();
  });

  it('shows nothing for fewer than two buckets', () => {
    const { el } = create([{ ...letters[1], count: 40 }], 'letters');
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
