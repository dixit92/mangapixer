import { vi } from 'vitest';
import { ReadStateService } from './read-state.service';

describe('ReadStateService', () => {
  it('emits the item id to subscribers on notifyChanged', () => {
    const svc = new ReadStateService();
    const seen: string[] = [];
    svc.itemChanged$.subscribe((id) => seen.push(id));

    svc.notifyChanged('item-1');
    svc.notifyChanged('item-2');

    expect(seen).toEqual(['item-1', 'item-2']);
  });

  it('ignores an empty item id (nothing to notify about)', () => {
    const svc = new ReadStateService();
    const spy = vi.fn();
    svc.itemChanged$.subscribe(spy);

    svc.notifyChanged('');

    expect(spy).not.toHaveBeenCalled();
  });
});
