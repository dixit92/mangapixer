import { rematchMessage, sumRematch } from './folder-content';

describe('folder Content: matching again after a change', () => {
  it('says what was queued, or why nothing was', () => {
    expect(rematchMessage(null)).toBe('');
    expect(rematchMessage({ affected: 0, queued: 0 })).toBe('');
    expect(rematchMessage({ affected: 1, queued: 1 })).toBe(' · 1 item below queued to match again');
    expect(rematchMessage({ affected: 250, queued: 0, needsConfirmation: true })).toBe(' · 250 items below were matched without this setting');
    expect(rematchMessage({ affected: 4, queued: 0, automaticOff: true }))
      .toBe(' · automatic matching is off - 4 items below can be re-run from Review');
  });

  it('adds up several folders, ignoring the ones whose doujinshi rule did not change', () => {
    expect(sumRematch([null, undefined])).toBeNull();
    expect(sumRematch([{ affected: 2, queued: 2 }, null, { affected: 300, queued: 0, needsConfirmation: true }]))
      .toEqual({ affected: 302, queued: 2, needsConfirmation: true, automaticOff: false });
  });
});
