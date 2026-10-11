/**
 * How long the reader waits after a centre tap for a second one (1.41.0, owner decision 6). A second centre tap
 * inside the window is a double tap (fullscreen); without one, the first tap shows / hides the controls once the
 * window has passed. Short enough that the controls still feel immediate, long enough for a relaxed double tap.
 */
export const DoubleTapWindowMs = 280;

/**
 * Tells a single tap from a double tap on one zone. `tap()` on every tap; a lone tap runs `single` after the window,
 * a second tap inside it runs `double` at once (inside the tap's own event, so the Fullscreen API still sees the
 * user's gesture) and the pending single is dropped. `cancel()` forgets a pending tap (reader teardown).
 */
export class DoubleTap {
  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor(
    private readonly single: () => void,
    private readonly double: () => void,
    private readonly windowMs = DoubleTapWindowMs,
  ) {}

  tap(): void {
    if (this.timer !== null) {
      this.cancel();
      this.double();
      return;
    }
    this.timer = setTimeout(() => {
      this.timer = null;
      this.single();
    }, this.windowMs);
  }

  cancel(): void {
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
  }
}
