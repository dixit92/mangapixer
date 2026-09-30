import { vi } from 'vitest';

import { NodeSelection } from './node-selection';

/** The browse Select mode as shared state (1.30.0): the same rules for a folder list and a volume stack page. */
describe('NodeSelection', () => {
  interface Item { id: string; blocked?: boolean; read?: boolean }

  const items: Item[] = [{ id: 'a' }, { id: 'b' }, { id: 'c', blocked: true }, { id: 'd', read: true }, { id: 'e' }];

  function create() {
    return new NodeSelection<Item>(() => items, (n) => !n.blocked);
  }

  function click(opts: Partial<MouseEvent> = {}): MouseEvent {
    return { shiftKey: false, preventDefault: vi.fn(), stopPropagation: vi.fn(), ...opts } as unknown as MouseEvent;
  }

  function item(id: string): Item {
    return items.find((i) => i.id === id)!;
  }

  it('does nothing on a click outside select mode, so the link navigates', () => {
    const sel = create();
    const event = click();

    sel.click(event, item('a'));

    expect(sel.selected().size).toBe(0);
    expect(event.preventDefault).not.toHaveBeenCalled();
  });

  it('a click in select mode toggles one item, moves the anchor and swallows the navigation', () => {
    const sel = create();
    sel.mode.set(true);
    const event = click();

    sel.click(event, item('b'));

    expect([...sel.selected()]).toEqual(['b']);
    expect(sel.anchorIndex()).toBe(1);
    expect(event.preventDefault).toHaveBeenCalled();
    sel.click(click(), item('b'));
    expect(sel.selected().size).toBe(0);
  });

  it('never selects an item canSelect refuses: by a click, a range or Select all', () => {
    const sel = create();
    sel.mode.set(true);

    sel.click(click(), item('c'));
    expect(sel.selected().size).toBe(0);

    sel.click(click(), item('a'));
    sel.click(click({ shiftKey: true }), item('e'));
    expect([...sel.selected()].sort()).toEqual(['a', 'b', 'd', 'e']); // c sits inside the range and is skipped

    sel.selectAll();
    expect([...sel.selected()].sort()).toEqual(['a', 'b', 'd', 'e']);
    expect(sel.anchorIndex()).toBe(4);
  });

  it('selectWhere keeps the predicate and resets the anchor', () => {
    const sel = create();
    sel.selectAll();

    sel.selectWhere((n) => !!n.read);

    expect([...sel.selected()]).toEqual(['d']);
    expect(sel.anchorIndex()).toBeNull();
  });

  it('a row checkbox selects without select mode and turns it on', () => {
    const sel = create();
    const event = click();

    sel.rowSelect(event, item('b'));

    expect(sel.mode()).toBe(true);
    expect([...sel.selected()]).toEqual(['b']);
    sel.rowSelect(click(), item('c'));
    expect([...sel.selected()]).toEqual(['b']);
  });

  it('toggling select mode off clears the selection, anchor and prompt', () => {
    const sel = create();
    sel.rowSelect(click(), item('a'));

    sel.toggleMode();

    expect(sel.mode()).toBe(false);
    expect(sel.selected().size).toBe(0);
    expect(sel.anchorIndex()).toBeNull();
  });

  describe('touch long-press', () => {
    function press(sel: NodeSelection<Item>, target: Item, pointerType = 'touch'): void {
      sel.pointerDown({ pointerType } as PointerEvent, target);
      vi.advanceTimersByTime(600);
    }

    beforeEach(() => vi.useFakeTimers());
    afterEach(() => vi.useRealTimers());

    it('enters select mode on the pressed item, then opens "Select to here" once an anchor exists', () => {
      const sel = create();

      press(sel, item('a'));
      expect(sel.mode()).toBe(true);
      expect([...sel.selected()]).toEqual(['a']);

      press(sel, item('e'));
      expect(sel.rangePromptNode()?.id).toBe('e');
      expect([...sel.selected()]).toEqual(['a']); // unchanged until confirmed

      sel.confirmSelectToHere();
      expect([...sel.selected()].sort()).toEqual(['a', 'b', 'd', 'e']);
      expect(sel.rangePromptNode()).toBeNull();
    });

    it('swallows the click that follows the press, once', () => {
      const sel = create();
      press(sel, item('a'));

      const trailing = click();
      sel.click(trailing, item('a'));
      expect(trailing.preventDefault).toHaveBeenCalled();
      expect([...sel.selected()]).toEqual(['a']); // not toggled off

      sel.click(click(), item('a')); // the next real click toggles
      expect(sel.selected().size).toBe(0);
    });

    it('a mouse never long-presses, and a released press cancels the timer', () => {
      const sel = create();

      press(sel, item('a'), 'mouse');
      expect(sel.mode()).toBe(false);

      sel.pointerDown({ pointerType: 'touch' } as PointerEvent, item('a'));
      sel.pointerEnd();
      vi.advanceTimersByTime(600);
      expect(sel.mode()).toBe(false);
    });

    it('a long-press on an item canSelect refuses does nothing', () => {
      const sel = create();

      press(sel, item('c'));

      expect(sel.mode()).toBe(false);
      expect(sel.selected().size).toBe(0);
    });

    it('dismissing the prompt keeps the selection', () => {
      const sel = create();
      press(sel, item('a'));
      press(sel, item('e'));

      sel.dismissPrompt(click());

      expect(sel.rangePromptNode()).toBeNull();
      expect([...sel.selected()]).toEqual(['a']);
    });
  });
});
