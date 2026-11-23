/**
 * Reader jump back (1.54.0, owner request 2026-11-17): after a jump of 3+ pages (a slider tap or drag, a bookmark, Home / End)
 * the reader offers a way back to the page the jump came FROM. Pure rules here; the reader component owns the timers, the DOM and
 * the saves. Design: vault note "MangaPixer - Reader Jump Back" (recommendations 1-9, all approved).
 *
 * The state is a pair of pages: `origin` is where the bar's button leads, `landing` is where the reader is. Using the button swaps
 * them, so Back flips to Return (a deliberate peek ahead costs one tap) and Return flips to Back again.
 */

/** A jump counts from this many pages (a spread counts as its pages; 2 would fire on a double-page spread turn). */
export const JumpThresholdPages = 3;
/** How long the chip stays on screen, counted only while the app is visible. */
export const JumpChipMs = 10_000;
/** Leaving the app this soon after a jump keeps the saved place on the page jumped FROM. */
export const JumpLeaveGuardMs = 2_000;

export interface JumpState {
  /** 0-based page the button leads to. */
  readonly origin: number;
  /** 0-based page the reader landed on (where it is while the offer stands). */
  readonly landing: number;
  /** 'back' after a jump; 'return' after the button was used once (it then leads to the page jumped TO). */
  readonly mode: 'back' | 'return';
  /** Epoch ms of the jump (or of the last use of the button). */
  readonly at: number;
  /** The app was hidden within {@link JumpLeaveGuardMs} of the jump; the chip asks again on return. */
  readonly leftAfterJump?: boolean;
}

/** A move from `from` to `to`: a new offer when it is a jump, else the current offer unchanged. */
export function recordJump(current: JumpState | null, from: number, to: number, now: number): JumpState | null {
  if (Math.abs(to - from) < JumpThresholdPages) return current;
  return { origin: from, landing: to, mode: 'back', at: now };
}

/** The button was used: the reader goes to `origin`, and the offer now leads back to where it was. */
export function useJump(state: JumpState, now: number): JumpState {
  return { origin: state.landing, landing: state.origin, mode: state.mode === 'back' ? 'return' : 'back', at: now };
}

/** The reader is on `page` (a page turn, a scroll): the offer ends once it is 3+ pages from the landing page. */
export function afterPageChange(state: JumpState | null, page: number): JumpState | null {
  if (!state) return null;
  return Math.abs(page - state.landing) >= JumpThresholdPages ? null : state;
}

/** True when hiding the app now must keep the saved place on the origin (the jump was a moment ago and not undone). */
export function isLeaveGuarded(state: JumpState | null, now: number): boolean {
  return !!state && state.mode === 'back' && now - state.at <= JumpLeaveGuardMs;
}

/**
 * Re-attach a stored offer to the page the reader opened on (the iPad may reload a suspended Home Screen app): on the origin
 * (the guard saved it, so the reader resumed there) the offer becomes Return to the landing page; on or near the landing page it
 * stands; anywhere else it is stale.
 */
export function restoreJump(stored: JumpState | null, page: number): JumpState | null {
  if (!stored) return null;
  if (page === stored.origin && stored.mode === 'back') {
    return { origin: stored.landing, landing: stored.origin, mode: 'return', at: stored.at, leftAfterJump: false };
  }
  return afterPageChange(stored, page);
}

/** Parse a stored offer (sessionStorage); null for anything malformed. */
export function parseJump(raw: string | null): JumpState | null {
  if (!raw) return null;
  try {
    const v = JSON.parse(raw) as Partial<JumpState>;
    const ok = Number.isInteger(v.origin) && Number.isInteger(v.landing) && (v.mode === 'back' || v.mode === 'return')
      && typeof v.at === 'number' && (v.origin as number) >= 0 && (v.landing as number) >= 0;
    return ok ? { origin: v.origin!, landing: v.landing!, mode: v.mode!, at: v.at!, leftAfterJump: !!v.leftAfterJump } : null;
  } catch {
    return null;
  }
}

/** The sessionStorage key of one archive's offer. */
export function jumpStorageKey(itemId: string): string {
  return `mp.reader.jumpBack.${itemId}`;
}
