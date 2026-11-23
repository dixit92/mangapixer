import {
  afterPageChange, isLeaveGuarded, JumpLeaveGuardMs, jumpStorageKey, parseJump, recordJump, restoreJump, useJump,
} from './jump-back';

describe('jump back rules (1.54.0)', () => {
  it('a move of 3+ pages is a jump; fewer keeps the current offer', () => {
    expect(recordJump(null, 41, 186, 1000)).toEqual({ origin: 41, landing: 186, mode: 'back', at: 1000 });
    expect(recordJump(null, 10, 12, 1000)).toBeNull();
    const s = recordJump(null, 0, 50, 1);
    expect(recordJump(s, 50, 52, 2)).toBe(s);
    expect(recordJump(null, 50, 47, 1)).toEqual({ origin: 50, landing: 47, mode: 'back', at: 1 }); // backwards counts too
  });

  it('using the button swaps the pages: Back flips to Return and back again', () => {
    const s = recordJump(null, 41, 186, 0)!;
    const r = useJump(s, 5);
    expect(r).toEqual({ origin: 186, landing: 41, mode: 'return', at: 5 });
    expect(useJump(r, 6)).toEqual({ origin: 41, landing: 186, mode: 'back', at: 6 });
  });

  it('reading on 3 pages from the landing page ends the offer; 2 keeps it', () => {
    const s = recordJump(null, 41, 186, 0);
    expect(afterPageChange(s, 188)).toBe(s);
    expect(afterPageChange(s, 184)).toBe(s);
    expect(afterPageChange(s, 189)).toBeNull();
    expect(afterPageChange(null, 3)).toBeNull();
  });

  it('the leave guard holds for 2 s after a jump, and not after Back', () => {
    const s = recordJump(null, 41, 186, 1000)!;
    expect(isLeaveGuarded(s, 1000 + JumpLeaveGuardMs)).toBe(true);
    expect(isLeaveGuarded(s, 1001 + JumpLeaveGuardMs)).toBe(false);
    expect(isLeaveGuarded(useJump(s, 1000), 1000)).toBe(false);
    expect(isLeaveGuarded(null, 0)).toBe(false);
  });

  it('a stored offer re-attaches to the page the reader opened on', () => {
    const s = { ...recordJump(null, 41, 186, 0)!, leftAfterJump: true };
    // The guard saved page 41, so a reload resumes there: the offer becomes Return to 186.
    expect(restoreJump(s, 41)).toEqual({ origin: 186, landing: 41, mode: 'return', at: 0, leftAfterJump: false });
    expect(restoreJump(s, 186)).toBe(s); // still on the landing page (the app was only hidden)
    expect(restoreJump(s, 120)).toBeNull(); // elsewhere: stale
    expect(restoreJump(null, 3)).toBeNull();
  });

  it('parses only well-formed stored offers', () => {
    expect(parseJump(JSON.stringify({ origin: 1, landing: 9, mode: 'return', at: 3 })))
      .toEqual({ origin: 1, landing: 9, mode: 'return', at: 3, leftAfterJump: false });
    for (const bad of [null, '', '{', '{"origin":1}', '{"origin":-1,"landing":3,"mode":"back","at":0}',
      '{"origin":1.5,"landing":3,"mode":"back","at":0}', '{"origin":1,"landing":3,"mode":"x","at":0}']) {
      expect(parseJump(bad)).toBeNull();
    }
    expect(jumpStorageKey('abc')).toBe('mp.reader.jumpBack.abc');
  });
});
