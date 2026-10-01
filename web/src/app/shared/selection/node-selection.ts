import { signal } from '@angular/core';

/** Anything a selection can hold: the id is the key. */
export interface SelectableNode {
  id: string;
}

/**
 * The browse Select mode as one reusable piece of state (1.30.0, extracted from `library-browse.component.ts` so the volume
 * stack page selects exactly like a folder does and the two cannot drift). Every rule is the one browse has always had:
 *
 *  - a plain click or Ctrl/Cmd-click toggles one item and moves the range ANCHOR to it; Shift-click fills the inclusive range
 *    from the anchor to the clicked item, in the displayed order (`items()`);
 *  - touch has no shift key: a long-press enters select mode on that item, and in select mode with an anchor it opens
 *    "Select to here" on the pressed item - tap = one, long-press = range fill; `longPressTriggered` swallows the click the
 *    browser fires on release;
 *  - a list row's leading checkbox selects without first entering select mode (the first one turns it on).
 *
 * `canSelect` marks what may never be selected (a missing-volume placeholder): it is skipped by a tap, a range, Select all
 * and the long-press alike.
 */
export class NodeSelection<T extends SelectableNode> {
  /** Select mode: a tap selects instead of opening, and the selection bar replaces the toolbar. */
  readonly mode = signal(false);
  readonly selected = signal<Set<string>>(new Set());

  /**
   * The ANCHOR: the index (within `items()`, the displayed order) of the last INDIVIDUALLY selected or deselected item.
   * Shift-click and "Select to here" fill the range between it and the target, so a range follows the active sort.
   */
  readonly anchorIndex = signal<number | null>(null);

  /** The item currently showing the long-press "Select to here" prompt, or null (only one at a time). */
  readonly rangePromptNode = signal<T | null>(null);

  private longPressTimer: ReturnType<typeof setTimeout> | null = null;
  private longPressTriggered = false;
  private readonly longPressMs = 550;

  constructor(
    /** The displayed items, in display order. */
    private readonly items: () => readonly T[],
    /** False for an item that must never be selected. */
    private readonly canSelect: (node: T) => boolean = () => true,
  ) {}

  isSelected(node: SelectableNode): boolean {
    return this.selected().has(node.id);
  }

  toggleMode(): void {
    this.mode.update((v) => !v);
    if (!this.mode()) this.clear();
  }

  /**
   * Card click, file-browser semantics (1.7.0). A long-press already handled this pointer session: swallow the trailing
   * click. Outside select mode a click is a normal navigation and this does nothing.
   */
  click(event: MouseEvent, node: T): void {
    if (this.longPressTriggered) {
      this.longPressTriggered = false;
      event.preventDefault();
      event.stopPropagation();
      return;
    }
    if (!this.mode()) return;
    event.preventDefault();
    event.stopPropagation();
    if (!this.canSelect(node)) return;

    const index = this.indexOf(node);
    if (index === -1) return;
    if (event.shiftKey && this.anchorIndex() !== null) {
      this.selectRange(this.anchorIndex()!, index);
      return;
    }
    this.toggleOne(node, index);
  }

  /**
   * A list row's leading checkbox (1.21.0): selects without requiring select mode first. Selecting the first item turns
   * select mode on; deselecting back to zero does NOT turn it off (only "Done" does).
   */
  rowSelect(event: Event, node: T): void {
    event.preventDefault();
    event.stopPropagation();
    if (!this.canSelect(node)) return;
    const index = this.indexOf(node);
    if (index === -1) return;
    if (!this.mode()) this.mode.set(true);
    this.toggleOne(node, index);
  }

  // --- Touch range selection: long-press -> "Select to here" (1.7.0) ---

  /** Starts the long-press timer for a touch or pen pointer only; a mouse uses shift-click. */
  pointerDown(event: PointerEvent, node: T): void {
    if (event.pointerType !== 'touch' && event.pointerType !== 'pen') return;
    this.clearLongPressTimer();
    this.longPressTimer = setTimeout(() => this.longPress(node), this.longPressMs);
  }

  /** A tap released before the long-press fired, or a scroll / interruption: cancel the pending long-press. */
  pointerEnd(): void {
    this.clearLongPressTimer();
  }

  /** "Select to here" button (its click must not reach the card's own click handler). */
  selectToHere(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    this.confirmSelectToHere();
  }

  /** Prompt "Cancel" button (its click must not reach the card's own click handler). */
  dismissPrompt(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    this.dismissRangePrompt();
  }

  /** Fills the range from the anchor to the prompted item. */
  confirmSelectToHere(): void {
    const target = this.rangePromptNode();
    const anchor = this.anchorIndex();
    this.rangePromptNode.set(null);
    if (target === null || anchor === null) return;
    const index = this.indexOf(target);
    if (index !== -1) this.selectRange(anchor, index);
  }

  dismissRangePrompt(): void {
    this.rangePromptNode.set(null);
  }

  // --- Whole-list selection (1.7.0) ---

  /** Selects every selectable item (the listed ones, not just the loaded page of a longer list). */
  selectAll(): void {
    this.selected.set(new Set(this.items().filter((n) => this.canSelect(n)).map((n) => n.id)));
    const count = this.items().length;
    this.anchorIndex.set(count > 0 ? count - 1 : null);
  }

  /** Selects every selectable item the predicate keeps ("all unread" / "all read"). */
  selectWhere(keep: (node: T) => boolean): void {
    this.selected.set(new Set(this.items().filter((n) => this.canSelect(n) && keep(n)).map((n) => n.id)));
    this.anchorIndex.set(null);
  }

  clear(): void {
    this.selected.set(new Set());
    this.anchorIndex.set(null);
    this.rangePromptNode.set(null);
    this.clearLongPressTimer();
    this.longPressTriggered = false;
  }

  private indexOf(node: SelectableNode): number {
    return this.items().findIndex((n) => n.id === node.id);
  }

  private toggleOne(node: T, index: number): void {
    this.selected.update((set) => {
      const next = new Set(set);
      if (next.has(node.id)) next.delete(node.id);
      else next.add(node.id);
      return next;
    });
    this.anchorIndex.set(index);
  }

  /** Adds the inclusive range [fromIndex, toIndex] (display order) to the selection. */
  private selectRange(fromIndex: number, toIndex: number): void {
    const lo = Math.min(fromIndex, toIndex);
    const hi = Math.max(fromIndex, toIndex);
    const rangeIds = this.items().slice(lo, hi + 1).filter((n) => this.canSelect(n)).map((n) => n.id);
    this.selected.update((set) => new Set([...set, ...rangeIds]));
    this.anchorIndex.set(toIndex);
  }

  /**
   * Long-press fired on `node`. Not yet in select mode: enter it and select + anchor this item. In select mode with no
   * anchor: the same. With an anchor: open "Select to here" on this item, so a range is filled only on confirmation.
   */
  private longPress(node: T): void {
    if (!this.canSelect(node)) return;
    this.longPressTriggered = true;
    const index = this.indexOf(node);
    if (index === -1) return;

    if (!this.mode()) {
      this.mode.set(true);
      this.toggleOne(node, index);
      return;
    }
    if (this.anchorIndex() === null) {
      this.toggleOne(node, index);
      return;
    }
    this.rangePromptNode.set(node);
  }

  private clearLongPressTimer(): void {
    if (this.longPressTimer !== null) {
      clearTimeout(this.longPressTimer);
      this.longPressTimer = null;
    }
  }
}
