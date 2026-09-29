import { retryUrl } from './cover-image.directive';

/** The pending-cover retry URL (1.29.0): cache-busted, the cover version `v` kept. */
describe('retryUrl', () => {
  it('adds the attempt and keeps the cover version', () => {
    expect(retryUrl('/api/v1/nodes/a1/cover?v=abc', 1)).toBe('/api/v1/nodes/a1/cover?v=abc&r=1');
    expect(retryUrl('/api/v1/items/a1/cover', 2)).toBe('/api/v1/items/a1/cover?r=2');
    expect(retryUrl('/api/v1/items/a1/cover?v=3&r=1', 2)).toBe('/api/v1/items/a1/cover?v=3&r=2');
  });
});
