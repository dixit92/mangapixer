/**
 * Edge-of-archive confirmation (owner, 2026-10-02): at the first or last page, ONE more "next" / "previous" input
 * no longer leaves the archive. The first input at the edge arms the move (the reader shows which archive comes
 * next); the same direction again - a second tap, click, swipe or key press, whatever input the reader uses -
 * within {@link EdgeAdvanceWindowMs} opens the neighbouring archive. A page turn back inside the archive, the
 * other direction, or waiting past the window disarms it, so a stray input at the end never jumps on its own.
 * Pure: the caller passes the time.
 */
export const EdgeAdvanceWindowMs = 3000;

export type EdgeDirection = 1 | -1;

/** What one input at an edge does: `arm` (show the hint) or `go` (open the neighbouring archive). */
export type EdgeAdvanceStep = 'arm' | 'go';

export class EdgeAdvance {
  private armed: { direction: EdgeDirection; at: number } | null = null;

  /** One input toward `direction` while the reader stands at that edge. */
  press(direction: EdgeDirection, now: number): EdgeAdvanceStep {
    const a = this.armed;
    if (a && a.direction === direction && now - a.at <= EdgeAdvanceWindowMs) {
      this.armed = null;
      return 'go';
    }
    this.armed = { direction, at: now };
    return 'arm';
  }

  /** Forget an armed edge (a page turn inside the archive, a new archive, leaving the reader). */
  reset(): void {
    this.armed = null;
  }

  /** The armed direction, if the window has not passed. */
  armedDirection(now: number): EdgeDirection | null {
    const a = this.armed;
    return a && now - a.at <= EdgeAdvanceWindowMs ? a.direction : null;
  }
}
