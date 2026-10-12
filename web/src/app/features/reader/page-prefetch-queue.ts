/**
 * Page prefetch queue (1.42.0, "current page before its own prefetch").
 *
 * The reader used to warm all of its prefetch targets at once (6 ahead + 2 behind as `new Image()`), so the page
 * the reader actually waits for - the current one - queued on the server behind its own prefetches. This queue:
 *  - starts at most `maxInFlight` prefetches at a time, in the order they were asked for (nearest first);
 *  - starts none while it is HELD (the reader holds it while the current page is still loading) - a hold ends by
 *    itself after `holdLimitMs`, so a lost load event can never stop prefetching for good;
 *  - can DROP a URL that became the current page before its prefetch started (the page's own `<img>` asks for it);
 *  - ABORTS everything on a chapter change.
 *
 * Prefetches are sent with `X-MangaPixer-Prefetch: 1` and a low fetch priority: the server serves them after any
 * reader's current page. The URL is byte-identical to the `<img>`'s, so the response lands in the browser's HTTP
 * cache under that URL and the later `<img>` is a cache hit (the header changes only the server's queue order, never
 * the response).
 */
export type PrefetchFetcher = (url: string, signal: AbortSignal) => Promise<void>;

export const PREFETCH_HEADER = 'X-MangaPixer-Prefetch';

/** The browser fetcher: low priority, marked as a prefetch, body read to the end so the cache entry is complete. */
export const browserPrefetchFetcher: PrefetchFetcher = async (url, signal) => {
  const init: RequestInit & { priority?: 'low' | 'high' | 'auto' } = {
    credentials: 'same-origin',
    headers: { [PREFETCH_HEADER]: '1' },
    priority: 'low',
    signal,
  };
  const response = await fetch(url, init);
  await response.arrayBuffer();
};

export class PagePrefetchQueue {
  private readonly pending: string[] = [];
  private readonly inFlight = new Map<string, AbortController>();
  private readonly done = new Set<string>();
  private held = false;
  private holdTimer: ReturnType<typeof setTimeout> | null = null;

  constructor(
    private readonly fetcher: PrefetchFetcher = browserPrefetchFetcher,
    readonly maxInFlight = 2,
    private readonly holdLimitMs = 4000,
  ) {}

  /** Prefetches running now. */
  get running(): number {
    return this.inFlight.size;
  }

  /** URLs waiting to start, in order. */
  get waiting(): readonly string[] {
    return this.pending;
  }

  /** Asks for `url` (ignored when it is already waiting, running or done). */
  enqueue(url: string): void {
    if (!url || this.done.has(url) || this.inFlight.has(url) || this.pending.includes(url)) return;
    this.pending.push(url);
    this.pump();
  }

  /** Forgets a waiting URL (it became the current page; its own `<img>` loads it). A running prefetch is kept. */
  drop(url: string): void {
    const i = this.pending.indexOf(url);
    if (i >= 0) this.pending.splice(i, 1);
  }

  /** Starts no new prefetch until `release()` (or `holdLimitMs`). */
  hold(): void {
    this.held = true;
    if (this.holdTimer !== null) clearTimeout(this.holdTimer);
    this.holdTimer = setTimeout(() => this.release(), this.holdLimitMs);
  }

  release(): void {
    if (this.holdTimer !== null) {
      clearTimeout(this.holdTimer);
      this.holdTimer = null;
    }
    if (!this.held) return;
    this.held = false;
    this.pump();
  }

  /** Chapter change / reader closed: abort running prefetches and forget everything. */
  abortAll(): void {
    for (const controller of this.inFlight.values()) controller.abort();
    this.inFlight.clear();
    this.pending.length = 0;
    this.done.clear();
    if (this.holdTimer !== null) clearTimeout(this.holdTimer);
    this.holdTimer = null;
    this.held = false;
  }

  private pump(): void {
    while (!this.held && this.inFlight.size < this.maxInFlight && this.pending.length > 0) {
      const url = this.pending.shift()!;
      const controller = new AbortController();
      this.inFlight.set(url, controller);
      this.fetcher(url, controller.signal)
        .catch(() => undefined)
        .finally(() => {
          // An aborted entry was already removed by abortAll; do not mark it done or pump for it.
          if (this.inFlight.get(url) !== controller) return;
          this.inFlight.delete(url);
          if (!controller.signal.aborted) this.done.add(url);
          this.pump();
        });
    }
  }
}
